namespace Vigo.Core.Models;

/// <summary>A local branch or a tag as stored in the repository (Undo / Redo, J5.2).</summary>
/// <param name="Target">
/// Object the reference points to: a commit, or the tag object of an annotated tag (not peeled, so that a deleted
/// annotated tag comes back annotated).
/// </param>
/// <param name="UpstreamRemote">Remote of the upstream branch (<c>branch.&lt;name&gt;.remote</c>); local branches only.</param>
/// <param name="UpstreamMerge">Upstream branch on that remote (<c>branch.&lt;name&gt;.merge</c>, <c>refs/heads/main</c>).</param>
public sealed record RefState(GitSha Target, string? UpstreamRemote = null, string? UpstreamMerge = null);

/// <summary>
/// HEAD, the local branches and the tags of a repository at one moment: what Undo / Redo puts back. Remote-tracking
/// branches and stashes are not part of it (a fetch or a stash is not undone).
/// </summary>
/// <param name="HeadBranch">Short name of the checked-out branch (also when it is unborn); null when HEAD is detached.</param>
/// <param name="Head">Commit checked out; null when HEAD is unborn.</param>
/// <param name="Refs">By full name: <c>refs/heads/main</c>, <c>refs/tags/v1</c>.</param>
public sealed record RefSnapshot(string? HeadBranch, GitSha? Head, IReadOnlyDictionary<string, RefState> Refs)
{
    public const string BranchPrefix = "refs/heads/";
    public const string TagPrefix = "refs/tags/";

    /// <summary>Full names whose state differs between the two snapshots (added, removed or moved; upstream ignored).</summary>
    public static IEnumerable<string> ChangedRefs(RefSnapshot a, RefSnapshot b) =>
        a.Refs.Keys.Union(b.Refs.Keys, StringComparer.Ordinal)
            .Where(name => a.Refs.GetValueOrDefault(name)?.Target != b.Refs.GetValueOrDefault(name)?.Target);

    /// <summary>Same checked-out branch (or same detached commit), same commit.</summary>
    public bool SameHead(RefSnapshot other) => HeadBranch == other.HeadBranch && Head == other.Head;
}
