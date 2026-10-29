using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Conflict prevention (J7.11, ADR-078): the branch to compare with, a dry-run merge, and the files it changed.</summary>
public sealed partial class GitService
{
    /// <summary><c>git merge-tree --write-tree</c> appeared in git 2.38.</summary>
    private static readonly Version MergeTreeVersion = new(2, 38);

    private Task<bool?>? _mergeTreeSupported;

    /// <summary><c>git merge-file --object-id</c> appeared in git 2.43 (<c>--zdiff3</c> in 2.35).</summary>
    private static readonly Version MergeFileObjectIdVersion = new(2, 43);

    private Task<Version?>? _gitVersion;

    private Task<Version?> ReadGitVersionAsync(CancellationToken cancellationToken) => _gitVersion ??= ParseGitVersionAsync(cancellationToken);

    private async Task<Version?> ParseGitVersionAsync(CancellationToken cancellationToken)
    {
        var version = await GetGitVersionAsync(cancellationToken);
        return version.Value is Success<string> { Value: var text } && TryParseVersion(text, out var parsed) ? parsed : null;
    }

    /// <summary>The installed git is known to be older than 2.43: conflicts are read from temporary files.</summary>
    private async Task<bool> IsLegacyMergeFileAsync(CancellationToken cancellationToken) =>
        await ReadGitVersionAsync(cancellationToken) is { } version && version < MergeFileObjectIdVersion;

    public async Task<GitResult<ConflictTarget>> GetConflictTargetAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var branch = await ReadOptionalAsync(repositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        if (branch.Value is not Success<string?> { Value: { } current }) return Fail<ConflictTarget>(branch, "GitError.NoConflictTarget");

        // The remote of the upstream ("." means a local upstream: no remote then); origin when the branch has none.
        var configured = await GetConfigValueAsync(repositoryPath, $"branch.{current}.remote", cancellationToken);
        if (configured.Value is Canceled canceled) return canceled;
        var remote = configured.Value is Success<string?> { Value: { Length: > 0 } name } && name != "." ? name : "origin";

        var symbolic = await ReadOptionalAsync(repositoryPath, ["symbolic-ref", "--quiet", $"refs/remotes/{remote}/HEAD"], cancellationToken);
        if (symbolic.Value is Canceled c) return c;
        List<string> candidates = [];
        if (symbolic.Value is Success<string?> { Value: { } target } && target.StartsWith("refs/remotes/", StringComparison.Ordinal))
            candidates.Add(target["refs/remotes/".Length..]);
        candidates.Add($"{remote}/main");
        candidates.Add($"{remote}/master");

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            var resolved = await ReadOptionalAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/{candidate}^{{commit}}"], cancellationToken);
            if (resolved.Value is Canceled cancel) return cancel;
            if (resolved.Value is Success<string?> { Value: { } sha } && GitSha.TryParse(sha, out var parsed))
                return new Success<ConflictTarget>(new ConflictTarget(candidate, parsed));
        }
        return new Failure(Loc.Get("GitError.NoConflictTarget"));
    }

    public async Task<GitResult<IReadOnlyList<string>>> PredictConflictsAsync(
        string repositoryPath, GitSha head, GitSha target, CancellationToken cancellationToken = default)
    {
        if (await IsMergeTreeSupportedAsync(cancellationToken) == false) return new Failure(Loc.Get("GitError.MergeTreeUnsupported"));
        if (cancellationToken.IsCancellationRequested) return Canceled.Instance;

        // Exit code 1: conflicts (the output still lists them); anything else above is a real failure.
        var result = await Cli.RunAsync(
            repositoryPath, ["merge-tree", "--write-tree", "--name-only", "--no-messages", "-z", head.ToString(), target.ToString()], 1, cancellationToken);
        return result is Success<CliOutput> s
            ? new Success<IReadOnlyList<string>>(ParseMergeTree(s.Value))
            : Propagate<IReadOnlyList<string>>(result);
    }

    /// <summary>
    /// Output of <c>merge-tree --write-tree --name-only -z</c>: the tree id, then (on conflicts, exit code 1) each conflicted
    /// path, NUL-terminated.
    /// </summary>
    internal static List<string> ParseMergeTree(CliOutput output) => output.ExitCode == 0
        ? []
        : [.. output.StandardOutput.Split('\0').Skip(1).Where(path => path.Length > 0).Distinct(StringComparer.Ordinal)];

    public async Task<GitResult<IReadOnlyList<string>>> GetChangedFilesSinceMergeBaseAsync(
        string repositoryPath, GitSha head, GitSha target, CancellationToken cancellationToken = default)
    {
        var bases = await ReadOptionalAsync(repositoryPath, ["merge-base", head.ToString(), target.ToString()], cancellationToken);
        if (bases.Value is not Success<string?> { Value: { } mergeBase }) return Fail<IReadOnlyList<string>>(bases, "GitError.NoMergeBase");

        var diff = await Cli.RunAsync(repositoryPath, ["diff", "--name-only", "-z", "--no-renames", mergeBase, target.ToString(), "--"], cancellationToken);
        return diff is Success<CliOutput> s
            ? new Success<IReadOnlyList<string>>([.. s.Value.StandardOutput.Split('\0').Where(path => path.Length > 0)])
            : Propagate<IReadOnlyList<string>>(diff);
    }

    /// <summary>Whether the installed git has <c>merge-tree --write-tree</c>; null when its version cannot be read. Asked once.</summary>
    private Task<bool?> IsMergeTreeSupportedAsync(CancellationToken cancellationToken) => _mergeTreeSupported ??= ReadMergeTreeSupportAsync(cancellationToken);

    private async Task<bool?> ReadMergeTreeSupportAsync(CancellationToken cancellationToken)
    {
        var version = await GetGitVersionAsync(cancellationToken);
        if (version.Value is not Success<string> { Value: var text }) return null;
        return TryParseVersion(text, out var parsed) ? parsed >= MergeTreeVersion : null;
    }

    /// <summary>"2.50.1.windows.1" → 2.50.1.</summary>
    internal static bool TryParseVersion(string text, out Version version)
    {
        var numbers = text.Split('.').TakeWhile(part => part.Length > 0 && part.All(char.IsAsciiDigit)).Take(3).ToList();
        if (numbers.Count >= 2 && Version.TryParse(string.Join('.', numbers), out var parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version();
        return false;
    }

    /// <summary>Trimmed output of a command that exits with 1 when there is nothing to report; null then.</summary>
    private async Task<GitResult<string?>> ReadOptionalAsync(string repositoryPath, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await Cli.RunAsync(repositoryPath, arguments, 1, cancellationToken);
        return result switch
        {
            Success<CliOutput> { Value: { ExitCode: 0 } output } => new Success<string?>(NullIfEmpty(output.StandardOutput.Trim())),
            Success<CliOutput> => new Success<string?>(null),
            _ => Propagate<string?>(result),
        };
    }

    // Nothing found is a plain failure with a message; a cancellation or a git error is passed on as it is.
    private static GitResult<T> Fail<T>(GitResult<string?> read, string messageKey) => read.Value switch
    {
        Success<string?> => new Failure(Loc.Get(messageKey)),
        Conflict c => c,
        AuthenticationFailed a => a,
        Canceled c => c,
        Failure f => f,
        _ => new Failure(Loc.Get(messageKey)),
    };
}
