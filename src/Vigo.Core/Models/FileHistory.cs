namespace Vigo.Core.Models;

/// <summary>Commit of a file's history and what it did to the file (J5.6).</summary>
/// <param name="File">
/// The file in that commit: its path then (renames followed), the previous one for a rename, the kind of change and the
/// lines added / deleted.
/// </param>
public sealed record FileHistoryEntry(CommitInfo Commit, ChangedFile File);

/// <summary>Receives one page of a file's history, newest first.</summary>
public delegate ValueTask FileHistoryPageHandler(IReadOnlyList<FileHistoryEntry> page, CancellationToken cancellationToken);

/// <summary>Commit that last changed some lines of a blamed file.</summary>
/// <param name="IsBoundary">Root commit (or edge of the blamed range): there is no blame before it.</param>
/// <param name="Previous">Parent the lines came from before this commit, and the file's path there; null when none.</param>
public sealed record BlameCommit(
    GitSha Sha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string Summary,
    bool IsBoundary,
    BlameOrigin? Previous)
{
    /// <summary>Lines changed on disk and not committed yet (git gives them the null SHA).</summary>
    public bool IsUncommitted => Sha.ToString().All(c => c == '0');
}

/// <summary>Version of a file before a commit: <c>previous</c> of <c>git blame --incremental</c>.</summary>
public sealed record BlameOrigin(GitSha Sha, string Path);

/// <summary>Consecutive lines of the blamed file last changed by the same commit.</summary>
/// <param name="Line">First line in the blamed version (1-based).</param>
/// <param name="Path">Path of the file in <paramref name="Commit"/> (differs after a rename).</param>
public sealed record BlameHunk(int Line, int LineCount, BlameCommit Commit, string Path);

/// <summary>Receives the hunks of a blame as git finds them (in no particular order).</summary>
public delegate ValueTask BlameHunkHandler(IReadOnlyList<BlameHunk> hunks, CancellationToken cancellationToken);
