using System.Globalization;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;
using Conflict = Vigo.Core.Results.Conflict;

namespace Vigo.Git;

/// <summary>
/// Interactive rebase (J5.5, ADR-041): <c>git rebase -i</c> whose todo list is written by VIGO and copied over git's
/// own by <c>GIT_SEQUENCE_EDITOR</c>; rewords are a pick followed by an <c>exec</c> amending the message from a file.
/// </summary>
public sealed partial class GitService
{
    // Fields of the commit list: SHA, parents, author name, email, date (strict ISO), raw message; records end with RS.
    private const string RebaseCommitFormat = "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%B%x1e";

    public async Task<GitResult<IReadOnlyList<RebaseCommit>>> GetRebaseCommitsAsync(
        string repositoryPath, string upstream, CancellationToken cancellationToken = default)
    {
        if (!IsRevision(upstream)) return new Failure(Loc.Get("GitError.InvalidRef"));
        // upstream...HEAD with --right-only --cherry-pick: the commits of HEAD git would replay, without those whose
        // patch is already in the upstream (git rebase leaves them out of its own todo list).
        string[] arguments =
        [
            "-c", "log.showSignature=false", "log", "--reverse", "--right-only", "--cherry-pick", "--encoding=UTF-8",
            RebaseCommitFormat, $"{upstream}...HEAD", "--",
        ];
        var result = await Cli.RunAsync(repositoryPath, arguments, cancellationToken);
        if (result is not Success<CliOutput> s) return Propagate<IReadOnlyList<RebaseCommit>>(result);

        var commits = new List<RebaseCommit>();
        foreach (var record in s.Value.StandardOutput.Split('\x1e'))
        {
            var fields = record.TrimStart('\n', '\r').Split('\0');
            if (fields.Length < 6 || !GitSha.TryParse(fields[0], out var sha)) continue;
            var message = fields[5].TrimEnd('\n', '\r', ' ');
            var subject = message.Split('\n', 2)[0].Trim();
            var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var date = DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTimeOffset.MinValue;
            commits.Add(new RebaseCommit(sha, subject, message, fields[2], fields[3], date, parents));
        }
        return new Success<IReadOnlyList<RebaseCommit>>(commits);
    }

    public async Task<GitResult<RebaseOutcome>> InteractiveRebaseAsync(
        string repositoryPath, string upstream, IReadOnlyList<RebaseTodoItem> todo, CancellationToken cancellationToken = default)
    {
        if (!IsRevision(upstream)) return new Failure(Loc.Get("GitError.InvalidRef"));
        if (todo.All(t => t.Action == RebaseAction.Drop))
            return new Failure(Loc.Get("GitError.RebaseDropsAll"));
        if (todo.FirstOrDefault(t => t.Action != RebaseAction.Drop) is { Action: RebaseAction.Squash or RebaseAction.Fixup })
            return new Failure(Loc.Get("GitError.RebaseFirstSquash"));
        if (todo.Any(t => t.Action == RebaseAction.Reword && string.IsNullOrWhiteSpace(t.Message)))
            return new Failure(Loc.Get("GitError.RebaseEmptyReword"));
        if (await GetGitDirectoryAsync(repositoryPath, cancellationToken) is not { } gitDirectory)
            return new Failure(Loc.Get("GitError.GitDirNotFound"));

        // Files of this rebase (todo list, reword messages): kept until the next interactive rebase, since the exec
        // lines read them when they run, possibly after a stop and a restart of VIGO.
        var directory = Path.Combine(gitDirectory, "vigo", "rebase");
        string todoPath;
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);
            todoPath = Path.Combine(directory, "git-rebase-todo");
            var lines = new StringBuilder();
            var reword = 0;
            foreach (var item in todo)
            {
                lines.Append(Command(item.Action)).Append(' ').Append(item.Commit.ToString()).Append('\n');
                if (item.Action != RebaseAction.Reword) continue;

                var messagePath = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"reword-{++reword}.txt"));
                await File.WriteAllTextAsync(messagePath, item.Message!.Trim() + "\n", new UTF8Encoding(false), cancellationToken);
                // --only: the message alone, never what the user may have staged; --cleanup=whitespace: lines starting
                // with '#' are kept, as typed.
                lines.Append("exec git commit --amend --only --no-verify --allow-empty --cleanup=whitespace -F ")
                    .Append(ShellQuote(messagePath)).Append('\n');
            }
            await File.WriteAllTextAsync(todoPath, lines.ToString(), new UTF8Encoding(false), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Failure(Loc.Get("GitError.RebasePrepareFailed"), Exception: ex, Details: ex.Message);
        }

        // git runs the sequence editor through its shell with the path of its own todo list appended.
        var environment = new Dictionary<string, string?> { ["GIT_SEQUENCE_EDITOR"] = "cp " + ShellQuote(todoPath) };
        // --empty=drop: a commit whose changes are already there disappears instead of stopping the rebase;
        // --no-autosquash: the order is the one chosen in VIGO (its own autosquash preset it).
        string[] arguments = ["rebase", "--interactive", "--empty=drop", "--no-autosquash", upstream];
        var result = ToResult(await Cli.RunAsync(repositoryPath, arguments, environment, cancellationToken));
        var stillRunning = await IsRebaseInProgressAsync(gitDirectory);
        if (result.Value is Done)
            return new Success<RebaseOutcome>(stillRunning ? RebaseOutcome.Stopped : RebaseOutcome.Rebased);

        var stopped = await StoppedOnConflictsAsync(repositoryPath, result, DisplayRevision(upstream), "rebase");
        return stopped switch
        {
            Done => new Failure(Loc.Get("Common.UnexpectedResult")),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            // Stopped without conflict (a reword whose message could not be applied, a hook…): left in progress.
            Failure f when stillRunning => f with
            {
                Message = Loc.Get("GitError.RebaseStopped"),
                Details = f.Details ?? f.Message,
            },
            Failure f => f,
        };
    }

    private static string Command(RebaseAction action) => action switch
    {
        RebaseAction.Pick or RebaseAction.Reword => "pick",
        RebaseAction.Edit => "edit",
        RebaseAction.Squash => "squash",
        RebaseAction.Fixup => "fixup",
        RebaseAction.Drop => "drop",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>A path for git's POSIX shell: forward slashes, single quotes (a quote inside closes, escapes and reopens).</summary>
    private static string ShellQuote(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static Task<bool> IsRebaseInProgressAsync(string gitDirectory) => Task.Run(() =>
        Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")));
}
