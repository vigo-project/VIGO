namespace Vigo.Core.Models;

/// <summary>A working directory of the repository (<c>git worktree list</c>): the main one or a linked one.</summary>
/// <param name="Path">Absolute path of the working directory (forward slashes, as git prints it).</param>
/// <param name="Head">Checked-out commit; null in a repository without commit.</param>
/// <param name="Branch">Short name of the checked-out branch; null for a detached HEAD or a bare repository.</param>
/// <param name="IsMain">The first one listed: the working directory the repository was opened from.</param>
/// <param name="LockReason">Set when the worktree is locked (<c>git worktree lock</c>); empty when locked without a reason.</param>
/// <param name="PruneReason">Set when git would prune it: its directory no longer exists.</param>
public sealed record Worktree(
    string Path,
    GitSha? Head,
    string? Branch,
    bool IsMain,
    bool IsBare = false,
    bool IsDetached = false,
    string? LockReason = null,
    string? PruneReason = null)
{
    public bool IsLocked => LockReason is not null;

    public bool IsPrunable => PruneReason is not null;

    /// <summary>Folder name, shown in the left panel.</summary>
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('/', '\\');
            var cut = trimmed.LastIndexOfAny(['/', '\\']);
            return cut < 0 ? trimmed : trimmed[(cut + 1)..];
        }
    }
}

/// <summary>What a new worktree checks out.</summary>
/// <param name="Path">Directory to create (absent or empty).</param>
/// <param name="Branch">Existing local branch to check out (must not be checked out elsewhere); null with <paramref name="NewBranch"/> or <paramref name="Commit"/>.</param>
/// <param name="NewBranch">Branch to create at <paramref name="Commit"/> (or HEAD) and check out.</param>
/// <param name="Commit">Starting point of a new branch, or the commit to check out detached when there is no branch at all.</param>
public sealed record AddWorktreeRequest(string Path, string? Branch = null, string? NewBranch = null, GitSha? Commit = null);
