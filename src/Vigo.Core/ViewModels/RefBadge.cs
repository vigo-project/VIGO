using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Style of a ref badge on a graph row (mockup: head, branch, remote, tag).</summary>
public enum RefBadgeKind
{
    /// <summary>Checked-out branch, or detached HEAD.</summary>
    Head,
    Branch,
    Remote,
    Tag,
}

/// <summary>Badge drawn before the message of a graph row.</summary>
/// <param name="IsOnRemote">
/// Local branch whose upstream points at the same commit: the remote badge is folded into this one (cloud icon).
/// </param>
public sealed record RefBadge(string Label, RefBadgeKind Kind, bool IsOnRemote = false)
{
    /// <summary>Badges by commit, in display order: head, local branches, remote branches, tags.</summary>
    /// <param name="hidden">Refs the user hid from the graph get no badge.</param>
    public static Dictionary<GitSha, RefBadge[]> FromRefs(RepositoryRefs refs, HiddenRefs? hidden = null)
    {
        IEnumerable<GitRef> Visible(GitRefKind kind) => refs.OfKind(kind).Where(r => hidden?.IsHidden(r) != true);

        var remoteTargets = Visible(GitRefKind.RemoteBranch).ToDictionary(r => r.Name, r => r.Target, StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.Ordinal);
        var badges = new List<(GitSha Target, RefBadge Badge)>();

        if (refs.CurrentBranch is null && refs.Head is { } detached)
        {
            badges.Add((detached, new RefBadge("HEAD", RefBadgeKind.Head)));
        }

        foreach (var branch in Visible(GitRefKind.LocalBranch))
        {
            var synced = branch.Upstream is { } upstream
                && remoteTargets.TryGetValue(upstream, out var remoteTarget)
                && remoteTarget == branch.Target;
            if (synced)
            {
                folded.Add(branch.Upstream!);
            }

            badges.Add((branch.Target, new RefBadge(branch.Name, branch.IsCurrent ? RefBadgeKind.Head : RefBadgeKind.Branch, synced)));
        }

        badges.AddRange(Visible(GitRefKind.RemoteBranch)
            .Where(r => !folded.Contains(r.Name))
            .Select(r => (r.Target, new RefBadge(r.Name, RefBadgeKind.Remote))));
        badges.AddRange(Visible(GitRefKind.Tag).Select(r => (r.Target, new RefBadge(r.Name, RefBadgeKind.Tag))));

        // Stable sort: the kind order wins, the order of the refs is kept within a kind.
        return badges
            .OrderBy(b => b.Badge.Kind)
            .GroupBy(b => b.Target)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Badge).ToArray());
    }
}

/// <summary>Lane palette color picked from a name, stable across sessions (badges, avatars).</summary>
public static class StableColor
{
    public static int IndexFor(string name)
    {
        // FNV-1a: string.GetHashCode is randomized per process.
        var hash = 2166136261;
        foreach (var c in name)
        {
            hash = (hash ^ char.ToUpperInvariant(c)) * 16777619;
        }

        return Graph.LanePalette.ColorIndexFor((int)(hash & int.MaxValue));
    }
}
