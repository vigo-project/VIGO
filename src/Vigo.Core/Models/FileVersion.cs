namespace Vigo.Core.Models;

/// <summary>Where a version of a file is read from.</summary>
public enum FileVersionSource
{
    /// <summary>A commit (any revision git understands: SHA, <c>HEAD</c>…).</summary>
    Commit,

    /// <summary>The index (stage 0).</summary>
    Index,

    /// <summary>The file on disk.</summary>
    WorkingTree,
}

/// <summary>One version of a file, path relative to the top-level directory (forward slashes, as git prints it).</summary>
public sealed record FileVersion(FileVersionSource Source, string Path, string? Revision = null)
{
    public static FileVersion AtCommit(string revision, string path) => new(FileVersionSource.Commit, path, revision);

    public static FileVersion InIndex(string path) => new(FileVersionSource.Index, path);

    public static FileVersion OnDisk(string path) => new(FileVersionSource.WorkingTree, path);
}
