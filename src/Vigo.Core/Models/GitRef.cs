namespace Vigo.Core.Models;

/// <summary>Kind of reference shown in the left panel and as graph badges.</summary>
public enum GitRefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
    Stash,
}

/// <summary>A branch, tag or stash entry pointing at a commit.</summary>
/// <param name="CanonicalName">Full name (<c>refs/heads/main</c>, <c>refs/tags/v1</c>, <c>stash@{0}</c>).</param>
/// <param name="Name">Display name (<c>main</c>, <c>origin/main</c>, <c>v1</c>, <c>stash@{0}</c>).</param>
/// <param name="Target">Commit pointed to (annotated tags are peeled).</param>
public sealed record GitRef(string CanonicalName, string Name, GitRefKind Kind, GitSha Target)
{
    /// <summary>What the graph needs to draw a stash (its base commit, author, date); null for other refs and when git did not say.</summary>
    public StashInfo? Stash { get; init; }

    /// <summary>True for the branch checked out in the working directory.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>Display name of the upstream branch (<c>origin/main</c>), for local branches only.</summary>
    public string? Upstream { get; init; }

    /// <summary>Commits on this branch that are not on its upstream.</summary>
    public int Ahead { get; init; }

    /// <summary>Commits on the upstream that are not on this branch.</summary>
    public int Behind { get; init; }

    /// <summary>Remote name (<c>origin</c>) for remote branches.</summary>
    public string? Remote { get; init; }

    /// <summary>Stash message or annotated tag message; null otherwise.</summary>
    public string? Message { get; init; }
}

/// <summary>All references of a repository plus the HEAD state.</summary>
/// <param name="Head">Commit checked out; null when HEAD is unborn (empty repository).</param>
/// <param name="CurrentBranch">Short name of the checked-out branch; null when HEAD is detached.</param>
public sealed record RepositoryRefs(IReadOnlyList<GitRef> Refs, GitSha? Head, string? CurrentBranch)
{
    public IEnumerable<GitRef> OfKind(GitRefKind kind) => Refs.Where(r => r.Kind == kind);
}

/// <summary>The commit a stash was made on and who made it when, to place it in the graph (J7.16).</summary>
public sealed record StashInfo(GitSha Parent, string AuthorName, string AuthorEmail, DateTimeOffset When);
