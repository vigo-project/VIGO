using System.Buffers;

namespace Vigo.Graph;

/// <summary>
/// Assigns graph lanes to commits, one row at a time, in topological order (children before parents).
/// </summary>
/// <remarks>
/// <para>
/// Commits are identified by dense non-negative ids chosen by the caller (the caller maps object ids to these
/// ints, a parent getting its id before its own row is appended). The state carried between rows is the list of
/// active lanes, each one waiting for a given commit id, so pages can be appended as they are read.
/// </para>
/// <para>
/// Lanes never move: a lane keeps its column from the row that opens it to the row of the commit it leads to,
/// then becomes free and is reused by the next lane to open (leftmost free column first). The first parent always
/// continues in the node's lane, even when another lane already leads to it: both lanes converge on the parent's
/// row, which keeps the fork point of a branch visible. Other parents join the lane already leading to them, or
/// open a new one. Colors are per column (<see cref="LanePalette.ColorIndexFor"/>).
/// </para>
/// <para>
/// Per row, only the node's edges are stored (a couple of ints). Vertical pass-through lines are stored as
/// spans of rows per lane, indexed by blocks of rows, so memory stays proportional to the number of commits and
/// lanes rather than commits × graph width.
/// </para>
/// </remarks>
public sealed class LaneEngine : IDisposable
{
    private const int NoCommit = -1;
    private const int BlockShift = 8;

    // Current state, per column: id of the commit the lane leads to (NoCommit when free) and the row it opened at.
    private int[] _expected = CreateLanes(16);
    private int[] _openedAt = new int[16];

    // Per column: the lane leads from a dotted node (a stash) to its parent, and is drawn dotted.
    private bool[] _dotted = new bool[16];
    private int _laneCount;

    private Row[] _rows = ArrayPool<Row>.Shared.Rent(1024);
    private int[] _edgeLanes = ArrayPool<int>.Shared.Rent(2048);
    private bool[] _edgeDotted = ArrayPool<bool>.Shared.Rent(2048);
    private int _rowCount;
    private int _edgeCount;

    // Closed vertical lines, by block of 2^BlockShift rows: each line is listed in every block it crosses.
    private readonly List<List<LaneLine>> _blocks = [];

    /// <summary>Number of rows appended so far.</summary>
    public int RowCount => _rowCount;

    /// <summary>Largest number of columns used by any row so far (width of the graph).</summary>
    public int MaxLaneCount { get; private set; }

    /// <summary>Number of columns in use after the last row (lanes still waiting for a commit, plus gaps).</summary>
    public int OpenLaneCount => _laneCount;

    /// <summary>Appends the next commit in topological order and computes its row.</summary>
    /// <param name="commitId">Id of the commit (non-negative).</param>
    /// <param name="parentIds">Ids of its parents, first parent first; empty for a root commit.</param>
    /// <param name="dotted">The commit is a stash: the line from it to its first parent is dotted.</param>
    /// <returns>Index of the new row.</returns>
    public int Append(int commitId, ReadOnlySpan<int> parentIds, bool dotted = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(commitId);
        ObjectDisposedException.ThrowIf(_rows.Length == 0, this);

        var row = _rowCount;
        EnsureRowCapacity(row + 1);
        EnsureEdgeCapacity(_edgeCount + _laneCount + parentIds.Length);
        var edgeStart = _edgeCount;

        // Incoming: every lane leading to this commit ends here; the leftmost one carries the node.
        var nodeLane = NoCommit;
        var lanes = _expected.AsSpan(0, _laneCount);
        for (var from = 0; ;)
        {
            var i = lanes[from..].IndexOf(commitId);
            if (i < 0)
            {
                break;
            }

            var lane = from + i;
            if (nodeLane == NoCommit)
            {
                nodeLane = lane;
            }

            _edgeDotted[_edgeCount] = _dotted[lane];
            CloseLane(lane, row);
            _edgeLanes[_edgeCount++] = lane;
            from = lane + 1;
        }

        var incomingCount = _edgeCount - edgeStart;

        // The node leaves the lanes of stashes alone: a commit whose children are stashes only (HEAD often is) must not sit in a stash's
        // column, where the stash icon (and the work-in-progress link) would be drawn over its line.
        if (incomingCount > 0 && _edgeDotted.AsSpan(edgeStart, incomingCount).Contains(true))
        {
            nodeLane = FirstSolidLane(edgeStart, incomingCount);
            if (nodeLane == NoCommit)
            {
                nodeLane = FreeLaneApartFrom(_edgeLanes.AsSpan(edgeStart, incomingCount));
            }
        }

        // Branch tip: no lane leads to it yet.
        if (nodeLane == NoCommit)
        {
            // A stash sits right of the lane that leads to its base commit, like a short branch: the dotted line joins it from the side.
            nodeLane = dotted && parentIds.Length > 0 ? FreeLaneRightOf(parentIds[0]) : FirstFreeLane();
        }

        // Outgoing: first parent continues in the node's lane; others join an existing lane or open a new one.
        for (var p = 0; p < parentIds.Length; p++)
        {
            var parent = parentIds[p];
            ArgumentOutOfRangeException.ThrowIfNegative(parent, nameof(parentIds));
            int lane;
            if (p == 0)
            {
                lane = nodeLane;
                OpenLane(lane, parent, row, dotted);
            }
            else if (_expected.AsSpan(0, _laneCount).IndexOf(parent) is var existing and >= 0)
            {
                lane = existing;
            }
            else
            {
                lane = FirstFreeLane();
                OpenLane(lane, parent, row);
            }

            _edgeDotted[_edgeCount] = _dotted[lane];
            _edgeLanes[_edgeCount++] = lane;
        }

        _rows[row] = new Row(nodeLane, edgeStart, incomingCount, _edgeCount - edgeStart - incomingCount);
        _rowCount = row + 1;

        MaxLaneCount = Math.Max(MaxLaneCount, Math.Max(_laneCount, nodeLane + 1));
        while (_laneCount > 0 && _expected[_laneCount - 1] == NoCommit)
        {
            _laneCount--;
        }

        return row;
    }

    /// <summary>Column of the commit's node on a row.</summary>
    public int GetNodeLane(int row) => GetRow(row).NodeLane;

    /// <summary>
    /// Columns of the lines ending at the node from the row above (top half of the row), left to right.
    /// Contains the node's own column when the commit is not a branch tip.
    /// </summary>
    public ReadOnlySpan<int> GetIncomingLanes(int row)
    {
        var r = GetRow(row);
        return _edgeLanes.AsSpan(r.EdgeStart, r.IncomingCount);
    }

    /// <summary>
    /// Columns of the lines leaving the node towards the row below (bottom half of the row), one per parent,
    /// in parent order. The first one is the node's own column.
    /// </summary>
    public ReadOnlySpan<int> GetOutgoingLanes(int row)
    {
        var r = GetRow(row);
        return _edgeLanes.AsSpan(r.EdgeStart + r.IncomingCount, r.OutgoingCount);
    }

    /// <summary>For each line of <see cref="GetIncomingLanes"/>: it comes from a stash and is drawn dotted.</summary>
    public ReadOnlySpan<bool> GetIncomingDotted(int row)
    {
        var r = GetRow(row);
        return _edgeDotted.AsSpan(r.EdgeStart, r.IncomingCount);
    }

    /// <summary>For each line of <see cref="GetOutgoingLanes"/>: it leaves a stash (or joins a stash lane) and is drawn dotted.</summary>
    public ReadOnlySpan<bool> GetOutgoingDotted(int row)
    {
        var r = GetRow(row);
        return _edgeDotted.AsSpan(r.EdgeStart + r.IncomingCount, r.OutgoingCount);
    }

    /// <summary>
    /// Writes the columns of the vertical lines crossing a row without touching its node, in ascending order.
    /// </summary>
    /// <param name="destination">At least <see cref="MaxLaneCount"/> long.</param>
    /// <returns>Number of columns written.</returns>
    public int GetPassThroughLanes(int row, Span<int> destination) => GetPassThroughLanes(row, destination, []);

    /// <summary>Same, also telling for each column written whether the line is dotted (<paramref name="dotted"/> as long as <paramref name="destination"/>, or empty).</summary>
    public int GetPassThroughLanes(int row, Span<int> destination, Span<bool> dotted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rowCount);

        var count = 0;
        var block = row >> BlockShift;
        if (block < _blocks.Count)
        {
            foreach (var line in _blocks[block])
            {
                if (line.FirstRow <= row && row <= line.LastRow)
                {
                    if (!dotted.IsEmpty) dotted[count] = line.Dotted;
                    destination[count++] = line.Lane;
                }
            }
        }

        for (var lane = 0; lane < _laneCount; lane++)
        {
            if (_expected[lane] != NoCommit && _openedAt[lane] < row)
            {
                if (!dotted.IsEmpty) dotted[count] = _dotted[lane];
                destination[count++] = lane;
            }
        }

        if (dotted.IsEmpty)
        {
            destination[..count].Sort();
        }
        else
        {
            destination[..count].Sort(dotted[..count]);
        }

        return count;
    }

    public void Dispose()
    {
        if (_rows.Length == 0)
        {
            return;
        }

        ArrayPool<Row>.Shared.Return(_rows);
        ArrayPool<int>.Shared.Return(_edgeLanes);
        ArrayPool<bool>.Shared.Return(_edgeDotted);
        _rows = [];
        _edgeLanes = [];
        _edgeDotted = [];
        _blocks.Clear();
    }

    private Row GetRow(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rowCount);
        return _rows[row];
    }

    // First free column right of the lane waiting for `commitId`; the first free one when no lane waits for it.
    private int FreeLaneRightOf(int commitId)
    {
        var waiting = _expected.AsSpan(0, _laneCount).IndexOf(commitId);
        if (waiting < 0)
        {
            return FirstFreeLane();
        }

        for (var lane = waiting + 1; lane < _laneCount; lane++)
        {
            if (_expected[lane] == NoCommit)
            {
                return lane;
            }
        }

        return _laneCount;
    }

    // Leftmost incoming lane that does not come from a stash; NoCommit when they all do.
    private int FirstSolidLane(int edgeStart, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!_edgeDotted[edgeStart + i])
            {
                return _edgeLanes[edgeStart + i];
            }
        }

        return NoCommit;
    }

    // First free column that is none of `taken` (lanes just closed by this row count as free).
    private int FreeLaneApartFrom(ReadOnlySpan<int> taken)
    {
        for (var lane = 0; lane < _laneCount; lane++)
        {
            if (_expected[lane] == NoCommit && !taken.Contains(lane))
            {
                return lane;
            }
        }

        return _laneCount;
    }

    private int FirstFreeLane()
    {
        var free = _expected.AsSpan(0, _laneCount).IndexOf(NoCommit);
        return free >= 0 ? free : _laneCount;
    }

    private void OpenLane(int lane, int commitId, int row, bool dotted = false)
    {
        if (lane >= _expected.Length)
        {
            var length = _expected.Length * 2;
            var expected = CreateLanes(length);
            _expected.CopyTo(expected, 0);
            _expected = expected;
            Array.Resize(ref _openedAt, length);
            Array.Resize(ref _dotted, length);
        }

        _dotted[lane] = dotted;
        _expected[lane] = commitId;
        _openedAt[lane] = row;
        _laneCount = Math.Max(_laneCount, lane + 1);
    }

    private void CloseLane(int lane, int row)
    {
        var dotted = _dotted[lane];
        _dotted[lane] = false;
        _expected[lane] = NoCommit;

        // The line fills the rows strictly between the one that opened it and this one.
        var first = _openedAt[lane] + 1;
        var last = row - 1;
        if (first > last)
        {
            return;
        }

        var line = new LaneLine(lane, first, last, dotted);
        for (var block = first >> BlockShift; block <= last >> BlockShift; block++)
        {
            while (_blocks.Count <= block)
            {
                _blocks.Add([]);
            }

            _blocks[block].Add(line);
        }
    }

    private void EnsureRowCapacity(int required)
    {
        if (required > _rows.Length)
        {
            _rows = Grow(_rows, required);
        }
    }

    private void EnsureEdgeCapacity(int required)
    {
        if (required > _edgeLanes.Length)
        {
            _edgeLanes = Grow(_edgeLanes, required);
            _edgeDotted = Grow(_edgeDotted, required);
        }
    }

    private static T[] Grow<T>(T[] array, int required)
    {
        var grown = ArrayPool<T>.Shared.Rent(Math.Max(required, array.Length * 2));
        array.CopyTo(grown, 0);
        ArrayPool<T>.Shared.Return(array);
        return grown;
    }

    private static int[] CreateLanes(int length)
    {
        var lanes = new int[length];
        lanes.AsSpan().Fill(NoCommit);
        return lanes;
    }

    private readonly record struct Row(int NodeLane, int EdgeStart, int IncomingCount, int OutgoingCount);

    private readonly record struct LaneLine(int Lane, int FirstRow, int LastRow, bool Dotted);
}
