namespace Vigo.Core.Models;

/// <summary>Kind of an unmerged path (<c>git status</c> "both modified", "deleted by us"…).</summary>
public enum ConflictKind
{
    BothModified,
    BothAdded,
    BothDeleted,
    AddedByUs,
    AddedByThem,
    DeletedByUs,
    DeletedByThem,
}

/// <summary>One path reported by <c>git status</c>.</summary>
/// <param name="Path">Repository-relative path, '/' separators.</param>
/// <param name="OldPath">Source of a rename or copy recorded in the index; null otherwise.</param>
/// <param name="IndexChange">Change staged in the index (HEAD → index); null when there is none.</param>
/// <param name="WorkTreeChange">Change not staged yet (index → working tree); null when there is none.</param>
/// <param name="IsUntracked">New file unknown to the index (its <see cref="WorkTreeChange"/> is Added).</param>
/// <param name="Conflict">Unmerged path; both changes are null then.</param>
public sealed record StatusEntry(
    string Path,
    string? OldPath,
    FileChangeKind? IndexChange,
    FileChangeKind? WorkTreeChange,
    bool IsUntracked = false,
    ConflictKind? Conflict = null)
{
    public bool IsStaged => IndexChange is not null;

    public bool IsUnstaged => WorkTreeChange is not null;

    public bool IsConflicted => Conflict is not null;

    /// <summary>Part of the file is staged and part is not.</summary>
    public bool IsPartiallyStaged => IsStaged && IsUnstaged;
}

/// <summary>State of the working directory and index (<c>git status --porcelain=v2 --branch</c>).</summary>
/// <param name="Head">Commit checked out; null in a repository without commits.</param>
/// <param name="Branch">Current branch; null for a detached HEAD.</param>
/// <param name="Upstream">Upstream branch (e.g. "origin/main"); null when there is none.</param>
/// <param name="Entries">Changed, untracked and unmerged paths, sorted by path (ordinal).</param>
/// <param name="Operation">Merge, rebase, cherry-pick or revert stopped half-way; null when there is none.</param>
public sealed record WorkingTreeStatus(
    GitSha? Head,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<StatusEntry> Entries,
    RepositoryOperation? Operation = null)
{
    public static WorkingTreeStatus Empty { get; } = new(null, null, null, 0, 0, []);

    public bool IsClean => Entries.Count == 0;
}

/// <summary>Which side of a working-directory change a diff shows.</summary>
public enum WorkingDiffSide
{
    /// <summary>Index → working tree (untracked files: empty file → working tree).</summary>
    Unstaged,

    /// <summary>HEAD → index.</summary>
    Staged,
}
