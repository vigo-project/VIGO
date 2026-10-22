namespace Vigo.Core.Models;

/// <summary>
/// Commit as listed in the graph: topology plus the few fields shown on a row.
/// Author names and emails are interned by the log reader (few distinct values across a history).
/// </summary>
/// <param name="Parents">Parent ids in order (first parent first); empty for a root commit.</param>
public sealed record CommitInfo(
    GitSha Sha,
    IReadOnlyList<GitSha> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string Subject)
{
    public bool IsMerge => Parents.Count > 1;

    /// <summary>The row is a stash (J7.16): drawn dotted, outside of the history; its only parent is the commit it was made on.</summary>
    public bool IsStash { get; init; }
}

/// <summary>Options for streaming the commit log.</summary>
/// <param name="PageSize">Number of commits delivered per callback.</param>
/// <param name="MaxCount">Maximum number of commits to read; null reads the whole history.</param>
/// <param name="Filter">Narrows the log to the commits matching the search box; null reads everything.</param>
/// <param name="HiddenRefs">Full names of refs left out of the history (<c>refs/heads/x</c>): the commits only they reach are not read.</param>
public sealed record CommitLogOptions(int PageSize = 2000, int? MaxCount = null, CommitFilter? Filter = null, IReadOnlyList<string>? HiddenRefs = null)
{
    public static CommitLogOptions Default { get; } = new();
}

/// <summary>Receives one page of commits, in topological order (children before parents).</summary>
public delegate ValueTask CommitPageHandler(IReadOnlyList<CommitInfo> page, CancellationToken cancellationToken);

/// <summary>Name, email and timestamp of an author or committer.</summary>
public sealed record GitSignature(string Name, string Email, DateTimeOffset When);

/// <summary>Kind of change applied to a file by a commit.</summary>
public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
}

/// <summary>File changed by a commit, compared with its first parent.</summary>
/// <param name="Path">Path in the commit (repository-relative, '/' separators).</param>
/// <param name="OldPath">Previous path for a rename or copy; null otherwise.</param>
/// <param name="Additions">Lines added (0 for a binary file).</param>
/// <param name="Deletions">Lines deleted (0 for a binary file).</param>
public sealed record ChangedFile(
    string Path,
    string? OldPath,
    FileChangeKind Kind,
    int Additions = 0,
    int Deletions = 0,
    bool IsBinary = false)
{
    /// <summary>File name without its directory.</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>Directory of the file (empty at the root).</summary>
    public string Directory => Path.LastIndexOf('/') is var i and > 0 ? Path[..i] : string.Empty;
}

/// <summary>Result of verifying the cryptographic signature of a commit (<c>%G?</c>).</summary>
public enum SignatureStatus
{
    /// <summary>The commit is not signed.</summary>
    None,
    Good,
    Bad,

    /// <summary>Good signature, but the signer's key is not trusted.</summary>
    UnknownValidity,
    Expired,
    ExpiredKey,
    Revoked,

    /// <summary>The public key (or the allowed signers entry) needed to check the signature is missing.</summary>
    MissingKey,

    /// <summary>The signature could not be checked (no verifier available, bad configuration).</summary>
    Error,
}

/// <summary>Signature of a commit with the signer and key reported by git.</summary>
/// <param name="Signer">Name of the signer (<c>%GS</c>); empty when unknown.</param>
/// <param name="Key">Key id or fingerprint (<c>%GK</c>); empty when unknown.</param>
public sealed record CommitSignature(SignatureStatus Status, string Signer, string Key)
{
    public static CommitSignature NotSigned { get; } = new(SignatureStatus.None, string.Empty, string.Empty);

    public bool IsSigned => Status != SignatureStatus.None;
}

/// <summary>Full commit details shown in the right-hand panel.</summary>
/// <param name="Files">Changes compared with the first parent (with the empty tree for a root commit).</param>
/// <param name="Signature">Verification of the commit signature; unsigned when absent.</param>
public sealed record CommitDetails(
    GitSha Sha,
    IReadOnlyList<GitSha> Parents,
    GitSignature Author,
    GitSignature Committer,
    string Message,
    IReadOnlyList<ChangedFile> Files,
    CommitSignature? Signature = null)
{
    /// <summary>First line of the message.</summary>
    public string Summary => Message.AsSpan().IndexOfAny('\r', '\n') is var i and >= 0 ? Message[..i] : Message;

    /// <summary>Message body after the summary line, trimmed; empty when there is none.</summary>
    public string Body => Message.AsSpan().IndexOf('\n') is var i and >= 0 ? Message[(i + 1)..].Trim() : string.Empty;
}
