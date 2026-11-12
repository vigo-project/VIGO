namespace Vigo.Core.Models;

/// <summary>Where a file stands when two branches A and B are compared through their common ancestor.</summary>
public enum BranchCompareCategory
{
    /// <summary>The file exists in A and not in B.</summary>
    OnlyInA,

    /// <summary>The file exists in B and not in A.</summary>
    OnlyInB,

    /// <summary>Changed since the common ancestor on A only: B is behind.</summary>
    ModifiedInA,

    /// <summary>Changed since the common ancestor on B only: A is behind.</summary>
    ModifiedInB,

    /// <summary>Changed on both sides; the merge combines the changes without conflict.</summary>
    ModifiedBothMerges,

    /// <summary>Changed on both sides; the merge conflicts.</summary>
    ModifiedBothConflicts,

    /// <summary>Changed on both sides the same way: the two versions are identical.</summary>
    Identical,
}

/// <summary>One file of a branch comparison.</summary>
/// <param name="Path">Path in B (in A for a file that only exists there).</param>
/// <param name="OldPath">Path in A when the file was renamed between A and B; null otherwise.</param>
/// <param name="Kind">How B differs from A (a file only in A is <see cref="FileChangeKind.Deleted"/>); null when the versions are identical.</param>
public sealed record BranchCompareEntry(string Path, string? OldPath, BranchCompareCategory Category, FileChangeKind? Kind)
{
    /// <summary>The A → B change to show as a diff; null for identical files.</summary>
    public ChangedFile? ToChangedFile() => Kind is { } kind ? new ChangedFile(Path, OldPath, kind) : null;
}

/// <summary>Three-point comparison of two commits (<c>A</c> and <c>B</c>) through their merge base.</summary>
/// <param name="ConflictsKnown">False when the dry-run merge was unavailable (old git): no file is classified as conflicting.</param>
public sealed record BranchComparison(GitSha MergeBase, GitSha ShaA, GitSha ShaB, IReadOnlyList<BranchCompareEntry> Entries, bool ConflictsKnown);
