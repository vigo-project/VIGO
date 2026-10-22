using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Linked working directories (J7.1, ADR-049): list, add, remove, prune.</summary>
public sealed partial class GitService
{
    public async Task<GitResult<IReadOnlyList<Worktree>>> GetWorktreesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await Cli.RunAsync(repositoryPath, ["worktree", "list", "--porcelain", "-z"], cancellationToken);
        return result is Success<CliOutput> s
            ? new Success<IReadOnlyList<Worktree>>(ParseWorktrees(s.Value.StandardOutput))
            : Propagate<IReadOnlyList<Worktree>>(result);
    }

    /// <summary>
    /// Parses <c>git worktree list --porcelain -z</c>: NUL-terminated "key value" fields, an empty field between two
    /// worktrees ("worktree", "HEAD", "branch", "detached", "bare", "locked [reason]", "prunable [reason]").
    /// </summary>
    internal static List<Worktree> ParseWorktrees(string output)
    {
        var worktrees = new List<Worktree>();
        string? path = null;
        GitSha? head = null;
        string? branch = null;
        bool bare = false, detached = false;
        string? locked = null, prunable = null;

        void Flush()
        {
            if (path is not null)
                worktrees.Add(new Worktree(path, head, branch, worktrees.Count == 0, bare, detached, locked, prunable));
            path = null;
            head = null;
            branch = null;
            bare = detached = false;
            locked = prunable = null;
        }

        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                Flush();
                continue;
            }

            var space = field.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? field : field[..space];
            var value = space < 0 ? string.Empty : field[(space + 1)..];
            switch (key)
            {
                case "worktree":
                    Flush();
                    path = value.Replace('/', Path.DirectorySeparatorChar);
                    break;
                case "HEAD":
                    // All zeros: a branch without commit yet.
                    head = GitSha.TryParse(value, out var sha) && !value.All(c => c == '0') ? sha : null;
                    break;
                case "branch":
                    branch = value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value;
                    break;
                case "bare": bare = true; break;
                case "detached": detached = true; break;
                case "locked": locked = value; break;
                case "prunable": prunable = value; break;
            }
        }

        Flush();
        return worktrees;
    }

    public async Task<GitResult> AddWorktreeAsync(string repositoryPath, AddWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Path.Length == 0) return new Failure(Loc.Get("GitError.WorktreeFolderRequired"));
        List<string> arguments = ["worktree", "add"];
        if (request.NewBranch is { } created)
        {
            if (RefNames.Check(created) is { } invalid) return new Failure(invalid);
            arguments.AddRange(["-b", created]);
        }
        else if (request.Branch is null)
        {
            if (request.Commit is null) return new Failure(Loc.Get("GitError.ChooseBranchOrCommit"));
            arguments.Add("--detach");
        }

        arguments.AddRange(["--", request.Path]);
        if (request.Branch is { } existing)
        {
            if (existing.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidBranchName"));
            arguments.Add(existing);
        }
        else if (request.Commit is { } commit)
        {
            arguments.Add(commit.ToString());
        }

        return ExplainWorktreeFailure(ToResult(await Cli.RunAsync(repositoryPath, arguments, cancellationToken)), request.Path);
    }

    public async Task<GitResult> RemoveWorktreeAsync(string repositoryPath, string worktreePath, bool force, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["worktree", "remove"];
        // Twice for a locked worktree (a single --force does not remove it).
        if (force) arguments.AddRange(["--force", "--force"]);
        arguments.AddRange(["--", worktreePath]);
        return ExplainWorktreeFailure(ToResult(await Cli.RunAsync(repositoryPath, arguments, cancellationToken)), worktreePath);
    }

    public async Task<GitResult> PruneWorktreesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        ToResult(await Cli.RunAsync(repositoryPath, ["worktree", "prune"], cancellationToken));

    /// <summary>Rewrites the usual <c>git worktree</c> refusals in French.</summary>
    private static GitResult ExplainWorktreeFailure(GitResult result, string path)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        var message = text switch
        {
            _ when text.Contains("is already used by worktree", StringComparison.Ordinal)
                   || text.Contains("is already checked out", StringComparison.Ordinal) =>
                Loc.Get("GitError.WorktreeBranchUsed"),
            _ when text.Contains("already exists", StringComparison.Ordinal) && text.Contains("branch", StringComparison.OrdinalIgnoreCase) =>
                Loc.Get("GitError.BranchNameExists"),
            _ when text.Contains("already exists", StringComparison.Ordinal) =>
                Loc.Format("GitError.WorktreeFolderNotEmpty", path),
            _ when text.Contains("contains modified or untracked files", StringComparison.Ordinal) =>
                Loc.Get("GitError.WorktreeDirty"),
            _ when text.Contains("locked working tree", StringComparison.Ordinal) || text.Contains("is locked", StringComparison.Ordinal) =>
                Loc.Get("GitError.WorktreeLocked"),
            _ when text.Contains("is a main working tree", StringComparison.Ordinal) => Loc.Get("GitError.MainWorktreeRemove"),
            _ when text.Contains("invalid reference", StringComparison.Ordinal) || text.Contains("not a valid reference", StringComparison.Ordinal) =>
                Loc.Get("GitError.BranchOrCommitNotFound"),
            _ => null,
        };
        if (message is null) return result;
        var needsForce = text.Contains("contains modified or untracked files", StringComparison.Ordinal)
                         || text.Contains("locked working tree", StringComparison.Ordinal)
                         || text.Contains("is locked", StringComparison.Ordinal);
        return failure with { Message = message, Details = text, Reason = needsForce ? FailureReason.WorktreeNeedsForce : FailureReason.Unknown };
    }
}
