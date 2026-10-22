using System.Runtime.InteropServices;
using Vigo.Graph;

namespace Vigo.Core.Models;

/// <summary>
/// Commits loaded so far, in log order, with their lanes. Fed page by page from
/// <see cref="Git.IGitService.ReadLogAsync"/>; not thread-safe (owned by the graph view model).
/// </summary>
public sealed class CommitGraph : IDisposable
{
    private const int NotLoaded = -1;

    // Dense id per object id, assigned the first time a commit is seen as a row or as a parent.
    private readonly Dictionary<GitSha, int> _ids = [];
    private readonly List<int> _rowById = [];
    private readonly List<CommitInfo> _commits = [];
    private readonly LaneEngine _lanes = new();
    private int[] _parentIds = new int[4];

    // Stashes still to place (newest first): each one goes above the first commit that is not newer, and always above its own parent.
    private List<CommitInfo> _pendingStashes = [];

    /// <summary>Stashes the graph was asked to show (placed or not: one whose base commit is not in the history is left out).</summary>
    public IReadOnlySet<GitSha> WantedStashes { get; private set; } = new HashSet<GitSha>();

    /// <summary>
    /// Stashes to draw among the commits (J7.16), each a <see cref="CommitInfo.IsStash"/> row with its base commit as only parent. Call
    /// before the first <see cref="Append"/>.
    /// </summary>
    public void SetStashes(IEnumerable<CommitInfo> stashes)
    {
        _pendingStashes = [.. stashes.Where(s => s.Parents.Count > 0).OrderByDescending(s => s.AuthorDate)];
        WantedStashes = _pendingStashes.Select(s => s.Sha).ToHashSet();
    }

    /// <summary>Commits by row (row 0 = newest).</summary>
    public IReadOnlyList<CommitInfo> Commits => _commits;

    /// <summary>Lane layout of the rows, one row per entry of <see cref="Commits"/>.</summary>
    public LaneEngine Lanes => _lanes;

    /// <summary>Appends a page of commits in topological order (children before parents).</summary>
    public void Append(IReadOnlyList<CommitInfo> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        foreach (var commit in page)
        {
            PlaceStashes(commit);
            AppendOne(commit);
        }
    }

    // The stashes that belong above `next`: newer than it, or made on it (a skewed clock must not put a stash under its base).
    // A stash whose base was passed already is not shown: its line would never end.
    private void PlaceStashes(CommitInfo next)
    {
        for (var i = 0; i < _pendingStashes.Count;)
        {
            var stash = _pendingStashes[i];
            if (TryGetRow(stash.Parents[0], out _))
            {
                _pendingStashes.RemoveAt(i);
            }
            else if (stash.Parents[0] == next.Sha || stash.AuthorDate >= next.AuthorDate)
            {
                _pendingStashes.RemoveAt(i);
                AppendOne(stash);
            }
            else
            {
                i++;
            }
        }
    }

    private void AppendOne(CommitInfo commit)
    {
        var id = IdOf(commit.Sha);
        if (_rowById[id] != NotLoaded)
        {
            throw new InvalidOperationException($"Commit {commit.Sha} is already in the graph.");
        }

        var parents = commit.Parents;
        if (parents.Count > _parentIds.Length)
        {
            _parentIds = new int[parents.Count];
        }

        for (var i = 0; i < parents.Count; i++)
        {
            _parentIds[i] = IdOf(parents[i]);
        }

        _rowById[id] = _lanes.Append(id, _parentIds.AsSpan(0, parents.Count), commit.IsStash);
        _commits.Add(commit);
    }

    /// <summary>Row of a loaded commit.</summary>
    public bool TryGetRow(GitSha sha, out int row)
    {
        row = _ids.TryGetValue(sha, out var id) ? _rowById[id] : NotLoaded;
        return row != NotLoaded;
    }

    public void Dispose() => _lanes.Dispose();

    private int IdOf(GitSha sha)
    {
        ref var id = ref CollectionsMarshal.GetValueRefOrAddDefault(_ids, sha, out var exists);
        if (!exists)
        {
            id = _rowById.Count;
            _rowById.Add(NotLoaded);
        }

        return id;
    }
}
