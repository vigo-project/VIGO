using LibGit2Sharp;
using Vigo.Core.Models;

namespace Vigo.Git.Refs;

/// <summary>Builds <see cref="RepositoryRefs"/> from LibGit2Sharp, or from git.exe output (fallback).</summary>
internal static class RefReader
{
    private const string HeadsPrefix = "refs/heads/";
    private const string RemotesPrefix = "refs/remotes/";
    private const string TagsPrefix = "refs/tags/";

    public static RepositoryRefs FromLibGit2(Repository repo)
    {
        var refs = new List<GitRef>();
        var detached = repo.Info.IsHeadDetached;
        var currentCanonical = detached ? null : repo.Head.CanonicalName;

        foreach (var branch in repo.Branches)
        {
            var reference = repo.Refs[branch.CanonicalName];
            // Symbolic refs such as refs/remotes/origin/HEAD are not shown as branches.
            if (reference is SymbolicReference || branch.Tip is null) continue;
            var target = ToSha(branch.Tip.Id);

            if (branch.IsRemote)
            {
                var remote = RemoteOf(branch.CanonicalName);
                refs.Add(new GitRef(branch.CanonicalName, branch.CanonicalName[RemotesPrefix.Length..], GitRefKind.RemoteBranch, target) { Remote = remote });
                continue;
            }

            var tracked = branch.IsTracking ? branch.TrackedBranch : null;
            refs.Add(new GitRef(branch.CanonicalName, branch.FriendlyName, GitRefKind.LocalBranch, target)
            {
                IsCurrent = branch.CanonicalName == currentCanonical,
                Upstream = tracked?.CanonicalName is { } up && up.StartsWith(RemotesPrefix, StringComparison.Ordinal)
                    ? up[RemotesPrefix.Length..]
                    : tracked?.FriendlyName,
                Ahead = branch.TrackingDetails.AheadBy ?? 0,
                Behind = branch.TrackingDetails.BehindBy ?? 0,
            });
        }

        foreach (var tag in repo.Tags)
        {
            if (tag.PeeledTarget is not Commit commit) continue;
            refs.Add(new GitRef(tag.CanonicalName, tag.FriendlyName, GitRefKind.Tag, ToSha(commit.Id))
            {
                // Subject line only, as %(contents:subject) gives in the git.exe fallback.
                Message = tag.IsAnnotated ? FirstLine(tag.Annotation.Message) : null,
            });
        }

        var index = 0;
        foreach (var stash in repo.Stashes)
        {
            var name = $"stash@{{{index++}}}";
            refs.Add(new GitRef(name, name, GitRefKind.Stash, ToSha(stash.WorkTree.Id))
            {
                Message = stash.WorkTree.MessageShort,
                Stash = stash.WorkTree.Parents.FirstOrDefault() is { } parent
                    ? new StashInfo(ToSha(parent.Id), stash.WorkTree.Author.Name, stash.WorkTree.Author.Email, stash.WorkTree.Author.When)
                    : null,
            });
        }

        var head = repo.Info.IsHeadUnborn ? (GitSha?)null : ToSha(repo.Head.Tip.Id);
        return new RepositoryRefs(refs, head, detached ? null : repo.Head.FriendlyName);
    }

    /// <summary>Format passed to <c>git stash list</c>: commit, subject, parents, author time, author name and email, NUL-separated.</summary>
    public const string StashListFormat = "--format=%H%x00%s%x00%P%x00%at%x00%an%x00%ae";

    /// <summary>Format passed to <c>git for-each-ref</c>; fields are NUL-separated, one ref per line.</summary>
    public const string ForEachRefFormat =
        "--format=%(refname)%00%(objecttype)%00%(objectname)%00%(*objecttype)%00%(*objectname)%00%(symref)"
        + "%00%(HEAD)%00%(upstream)%00%(upstream:track,nobracket)%00%(contents:subject)";

    /// <summary>
    /// Parses the output of <c>git for-each-ref</c> (with <see cref="ForEachRefFormat"/>), of
    /// <c>git stash list</c> with <see cref="StashListFormat"/> and the HEAD state resolved by the caller.
    /// </summary>
    public static RepositoryRefs FromCli(string forEachRefOutput, string stashListOutput, GitSha? head, string? currentBranch)
    {
        var refs = new List<GitRef>();
        foreach (var line in forEachRefOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\0');
            if (f.Length < 10 || f[5].Length > 0) continue;
            string refName = f[0], type = f[1], peeledType = f[3];

            if (refName.StartsWith(HeadsPrefix, StringComparison.Ordinal))
            {
                var (ahead, behind) = ParseTrack(f[8]);
                refs.Add(new GitRef(refName, refName[HeadsPrefix.Length..], GitRefKind.LocalBranch, GitSha.Parse(f[2]))
                {
                    IsCurrent = f[6] == "*",
                    Upstream = f[7].StartsWith(RemotesPrefix, StringComparison.Ordinal) ? f[7][RemotesPrefix.Length..]
                        : f[7].StartsWith(HeadsPrefix, StringComparison.Ordinal) ? f[7][HeadsPrefix.Length..]
                        : null,
                    Ahead = ahead,
                    Behind = behind,
                });
            }
            else if (refName.StartsWith(RemotesPrefix, StringComparison.Ordinal))
            {
                refs.Add(new GitRef(refName, refName[RemotesPrefix.Length..], GitRefKind.RemoteBranch, GitSha.Parse(f[2]))
                {
                    Remote = RemoteOf(refName),
                });
            }
            else if (refName.StartsWith(TagsPrefix, StringComparison.Ordinal))
            {
                var annotated = type == "tag";
                if (annotated ? peeledType != "commit" : type != "commit") continue;
                refs.Add(new GitRef(refName, refName[TagsPrefix.Length..], GitRefKind.Tag, GitSha.Parse(annotated ? f[4] : f[2]))
                {
                    Message = annotated ? f[9] : null,
                });
            }
        }

        var index = 0;
        foreach (var line in stashListOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\0');
            if (f.Length < 2 || !GitSha.TryParse(f[0], out var sha)) continue;
            var name = $"stash@{{{index++}}}";
            // Fields 3 to 6 (parents, author time, author name and email) came with the graph: older output has only two.
            StashInfo? info = f.Length >= 6
                && f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] && GitSha.TryParse(first, out var parent)
                && long.TryParse(f[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? new StashInfo(parent, f[4], f[5], DateTimeOffset.FromUnixTimeSeconds(seconds))
                : null;
            refs.Add(new GitRef(name, name, GitRefKind.Stash, sha) { Message = f[1], Stash = info });
        }

        return new RepositoryRefs(refs, head, currentBranch);
    }

    /// <summary>"ahead 2, behind 1", "ahead 2", "behind 1", "gone" or empty.</summary>
    private static (int Ahead, int Behind) ParseTrack(string track)
    {
        int ahead = 0, behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("ahead ", StringComparison.Ordinal)) _ = int.TryParse(part.AsSpan(6), out ahead);
            else if (part.StartsWith("behind ", StringComparison.Ordinal)) _ = int.TryParse(part.AsSpan(7), out behind);
        }
        return (ahead, behind);
    }

    /// <summary>First path segment after refs/remotes/ (remote names cannot contain '/' in practice).</summary>
    private static string RemoteOf(string canonicalName)
    {
        var rest = canonicalName.AsSpan(RemotesPrefix.Length);
        var slash = rest.IndexOf('/');
        return (slash < 0 ? rest : rest[..slash]).ToString();
    }

    private static string FirstLine(string text) =>
        text.AsSpan().IndexOfAny('\r', '\n') is var i and >= 0 ? text[..i] : text;

    internal static GitSha ToSha(ObjectId id) => GitSha.Parse(id.Sha);
}
