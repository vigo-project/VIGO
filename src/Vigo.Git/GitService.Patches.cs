using System.Text;
using System.Text.RegularExpressions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Patch files: create from commits or from the working directory, apply (J7.12, ADR-079).</summary>
public sealed partial class GitService
{
    /// <summary>Paths go on the command line (<c>git diff</c> has no <c>--pathspec-from-file</c>): stay far under the Windows limit.</summary>
    private const int MaxPatchPathspecChars = 24_000;

    public async Task<GitResult> CreatePatchFromCommitsAsync(
        string repositoryPath, IReadOnlyList<GitSha> commits, string patchPath, CancellationToken cancellationToken = default)
    {
        if (commits.Count == 0) return new Failure(Loc.Get("GitError.PatchNoCommits"));

        var parents = await ReadParentsAsync(repositoryPath, commits, cancellationToken);
        if (parents.Value is not Success<Dictionary<GitSha, GitSha[]>> read) return parents.Value switch
        {
            Canceled c => c,
            AuthenticationFailed a => a,
            Conflict c => c,
            Failure f => f,
            _ => new Failure(Loc.Get("Common.UnexpectedResult")),
        };

        var chain = OrderChain(commits, read.Value);
        if (chain is null) return new Failure(Loc.Get("GitError.PatchNotConsecutive"));
        if (chain.Any(c => read.Value[c].Length > 1)) return new Failure(Loc.Get("GitError.PatchMergeCommit"));

        var oldest = chain[0];
        var newest = chain[^1];
        string[] arguments = chain.Count == 1
            ? ["format-patch", "-1", "--stdout", "--binary", "--full-index", "--root", newest.ToString()]
            : read.Value[oldest].Length == 0
                ? ["format-patch", "--stdout", "--binary", "--full-index", "--root", newest.ToString()]
                : ["format-patch", "--stdout", "--binary", "--full-index", $"{oldest}^..{newest}"];
        return await WritePatchFileAsync(repositoryPath, arguments, patchPath, cancellationToken);
    }

    public async Task<GitResult> CreatePatchFromWorkingTreeAsync(
        string repositoryPath, PatchScope scope, IReadOnlyList<string>? paths, string patchPath, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["--literal-pathspecs", "diff", "--binary", "--full-index", "--no-color", "--no-ext-diff"];
        switch (scope)
        {
            case PatchScope.Staged: arguments.Add("--cached"); break;
            case PatchScope.All: arguments.Add("HEAD"); break;
        }
        if (paths is { Count: > 0 })
        {
            if (paths.Sum(p => p.Length + 1) > MaxPatchPathspecChars) return new Failure(Loc.Get("GitError.PatchTooManyFiles"));
            arguments.Add("--");
            arguments.AddRange(paths);
        }

        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        return await WritePatchFileAsync(top, arguments, patchPath, cancellationToken);
    }

    public async Task<GitResult<PatchApplyOutcome>> ApplyPatchAsync(string repositoryPath, string patchPath, CancellationToken cancellationToken = default)
    {
        string full;
        bool mailbox;
        try
        {
            full = Path.GetFullPath(patchPath);
            if (!File.Exists(full)) return new Failure(Loc.Format("GitError.PatchFileMissing", patchPath));
            mailbox = IsMailbox(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Failure(Loc.Format("GitError.PatchReadFailed", ex.Message));
        }

        return mailbox
            ? await ApplyMailboxAsync(repositoryPath, full, cancellationToken)
            : await ApplyDiffAsync(repositoryPath, full, cancellationToken);
    }

    // ---- Apply -------------------------------------------------------------------------------------------------------

    private async Task<GitResult<PatchApplyOutcome>> ApplyMailboxAsync(string repositoryPath, string patchPath, CancellationToken cancellationToken)
    {
        var result = ToResult(await Cli.RunAsync(repositoryPath, ["am", "--3way", "--keep-cr", patchPath], cancellationToken));
        if (result.Value is Done) return new Success<PatchApplyOutcome>(PatchApplyOutcome.Committed);

        var stopped = await StoppedOnConflictsAsync(repositoryPath, result, null, "am");
        switch (stopped.Value)
        {
            case Conflict c:
                return c;
            case Canceled c:
                return c;
            case AuthenticationFailed a:
                return a;
            case Failure f:
                var explained = ExplainPatchFailure(f);
                // Stopped without conflicts (the patch does not apply): nothing worth keeping in progress.
                if (await IsMailboxInProgressAsync(repositoryPath)) await Cli.RunAsync(repositoryPath, ["am", "--abort"], CancellationToken.None);
                return explained;
            default:
                return new Failure(Loc.Get("Common.UnexpectedResult"));
        }
    }

    private async Task<GitResult<PatchApplyOutcome>> ApplyDiffAsync(string repositoryPath, string patchPath, CancellationToken cancellationToken)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        var result = ToResult(await Cli.RunAsync(top, ["apply", "--3way", "--whitespace=nowarn", patchPath], cancellationToken));
        switch (result.Value)
        {
            case Done:
                return new Success<PatchApplyOutcome>(PatchApplyOutcome.Applied);
            case Canceled c:
                return c;
            case AuthenticationFailed a:
                return a;
            case Failure f:
                return await ConflictsOrFailureAsync(repositoryPath, f);
            case Conflict c:
                return c;
            default:
                return new Failure(Loc.Get("Common.UnexpectedResult"));
        }
    }

    /// <summary>A 3-way apply that merged with conflicts leaves unmerged paths (and no operation); anything else is a failure to explain.</summary>
    private async Task<GitResult<PatchApplyOutcome>> ConflictsOrFailureAsync(string repositoryPath, Failure failure)
    {
        var status = await GetStatusAsync(repositoryPath, CancellationToken.None);
        if (status.Value is Success<WorkingTreeStatus> s && s.Value.Entries.Where(e => e.IsConflicted).Select(e => e.Path).ToList() is { Count: > 0 } paths)
            return new Conflict(paths, "apply");
        return ExplainPatchFailure(failure);
    }

    private async Task<bool> IsMailboxInProgressAsync(string repositoryPath)
    {
        var status = await GetStatusAsync(repositoryPath, CancellationToken.None);
        return status.Value is Success<WorkingTreeStatus> { Value.Operation.Kind: RepositoryOperationKind.ApplyMailbox };
    }

    private static Failure ExplainPatchFailure(Failure failure)
    {
        var text = failure.Details ?? failure.Message;
        string? message = text switch
        {
            _ when text.Contains("No valid patches in input", StringComparison.Ordinal)
                   || text.Contains("Patch format detection failed", StringComparison.Ordinal)
                   || text.Contains("only garbage was found", StringComparison.Ordinal)
                   || text.Contains("patch with only garbage", StringComparison.Ordinal)
                   || text.Contains("corrupt patch", StringComparison.Ordinal)
                   || text.Contains("Patch is empty", StringComparison.Ordinal) => Loc.Get("GitError.PatchInvalid"),
            _ when text.Contains("Dirty index", StringComparison.Ordinal)
                   || text.Contains("does not match index", StringComparison.Ordinal)
                   || text.Contains("your local changes would be overwritten", StringComparison.OrdinalIgnoreCase) => Loc.Get("GitError.PatchLocalChanges"),
            _ when text.Contains("patch failed", StringComparison.Ordinal)
                   || text.Contains("does not apply", StringComparison.Ordinal)
                   || text.Contains("Patch failed at", StringComparison.Ordinal)
                   || text.Contains("already exists in working directory", StringComparison.Ordinal)
                   || text.Contains("does not exist in index", StringComparison.Ordinal)
                   || text.Contains("Failed to merge in the changes", StringComparison.Ordinal) => Loc.Get("GitError.PatchDoesNotApply"),
            _ when text.Contains("already a rebase-apply", StringComparison.Ordinal)
                   || text.Contains("already a rebase-merge", StringComparison.Ordinal)
                   || text.Contains("You are in the middle of", StringComparison.Ordinal) => OperationInProgressMessage,
            _ => null,
        };
        return message is null ? failure : failure with { Message = message, Details = text };
    }

    /// <summary>A <c>git format-patch</c> file starts with "From &lt;commit id&gt; Mon Sep 17 00:00:00 2001".</summary>
    private static bool IsMailbox(string path)
    {
        Span<byte> head = stackalloc byte[128];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = stream.Read(head);
        return MailboxStart().IsMatch(Encoding.ASCII.GetString(head[..read]));
    }

    [GeneratedRegex("^From [0-9a-f]{40}(?:[0-9a-f]{24})? ", RegexOptions.CultureInvariant)]
    private static partial Regex MailboxStart();

    // ---- Create ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs git and writes its standard output, byte for byte, to <paramref name="patchPath"/> (a temporary file first, so a
    /// failure never leaves half a patch or erases an older one). An empty output is a failure.
    /// </summary>
    private async Task<GitResult> WritePatchFileAsync(string workingDirectory, IReadOnlyList<string> arguments, string patchPath, CancellationToken cancellationToken)
    {
        var temporary = patchPath + ".vigo-tmp";
        long length = 0;
        try
        {
            var result = await Cli.RunStreamingAsync(workingDirectory, arguments, async (reader, token) =>
            {
                await using var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
                await reader.CopyToAsync(stream, token);
                length = stream.Length;
            }, cancellationToken);

            if (result.Value is not Done)
            {
                DeleteQuietly(temporary);
                return result;
            }
            if (length == 0)
            {
                DeleteQuietly(temporary);
                return new Failure(Loc.Get("GitError.PatchEmpty"));
            }
            File.Move(temporary, patchPath, overwrite: true);
            return Done.Instance;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DeleteQuietly(temporary);
            return new Failure(Loc.Format("GitError.PatchWriteFailed", ex.Message), Exception: ex);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file next to the patch is harmless.
        }
    }

    private async Task<GitResult<Dictionary<GitSha, GitSha[]>>> ReadParentsAsync(
        string repositoryPath, IReadOnlyList<GitSha> commits, CancellationToken cancellationToken)
    {
        // One line per commit: "<sha> <parent> <parent>…".
        var result = await Cli.RunAsync(repositoryPath, ["rev-list", "--no-walk=unsorted", "--parents", .. commits.Select(c => c.ToString())], cancellationToken);
        if (result is not Success<CliOutput> output) return Propagate<Dictionary<GitSha, GitSha[]>>(result);

        var parents = new Dictionary<GitSha, GitSha[]>();
        foreach (var line in output.Value.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ids = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0 || !GitSha.TryParse(ids[0], out var sha)) continue;
            parents[sha] = [.. ids.Skip(1).Select(id => GitSha.TryParse(id, out var parent) ? parent : default)];
        }
        return new Success<Dictionary<GitSha, GitSha[]>>(parents);
    }

    /// <summary>
    /// The commits from the oldest to the newest when they form one linear chain (each one the parent of the next), null when
    /// they do not. Duplicates are ignored.
    /// </summary>
    internal static List<GitSha>? OrderChain(IReadOnlyList<GitSha> commits, IReadOnlyDictionary<GitSha, GitSha[]> parents)
    {
        var set = commits.Distinct().ToList();
        if (set.Any(c => !parents.ContainsKey(c))) return null;

        var inSet = set.ToHashSet();
        // The oldest has no parent among the others; every other one has exactly one, and each is the parent of at most one.
        var roots = set.Where(c => !parents[c].Any(inSet.Contains)).ToList();
        if (roots.Count != 1) return null;

        var childOf = new Dictionary<GitSha, GitSha>();
        foreach (var commit in set)
        {
            foreach (var parent in parents[commit].Where(inSet.Contains))
            {
                if (!childOf.TryAdd(parent, commit)) return null;
            }
        }

        var chain = new List<GitSha> { roots[0] };
        while (childOf.TryGetValue(chain[^1], out var next)) chain.Add(next);
        return chain.Count == set.Count ? chain : null;
    }
}
