using Vigo.TestUtilities;

namespace Vigo.Graph.Tests;

public sealed class LaneEngineTests
{
    // Commit ids used by the scenarios; values only need to be distinct and non-negative.
    private const int A = 0, B = 1, C = 2, D = 3, F = 4, M = 5, X = 6, Y = 7, Z = 8, X0 = 9, Y0 = 10, Z0 = 11;

    [Fact]
    public void Linear_history_stays_in_the_first_lane()
    {
        using var engine = Build((C, [B]), (B, [A]), (A, []));

        Assert.Equal(1, engine.MaxLaneCount);
        Assert.Equal(0, engine.OpenLaneCount);
        AssertRow(engine, 0, node: 0, incoming: [], outgoing: [0], passThrough: []);
        AssertRow(engine, 1, node: 0, incoming: [0], outgoing: [0], passThrough: []);
        AssertRow(engine, 2, node: 0, incoming: [0], outgoing: [], passThrough: []);
    }

    [Fact]
    public void Merged_branch_gets_its_own_lane_until_its_fork_point()
    {
        // M merges F (forked from A) into B.
        using var engine = Build((M, [B, F]), (B, [A]), (F, [A]), (A, []));

        Assert.Equal(2, engine.MaxLaneCount);
        AssertRow(engine, 0, node: 0, incoming: [], outgoing: [0, 1], passThrough: []);
        AssertRow(engine, 1, node: 0, incoming: [0], outgoing: [0], passThrough: [1]);
        // F's first parent stays in lane 1 although lane 0 already leads to A.
        AssertRow(engine, 2, node: 1, incoming: [1], outgoing: [1], passThrough: [0]);
        AssertRow(engine, 3, node: 0, incoming: [0, 1], outgoing: [], passThrough: []);
        Assert.Equal(0, engine.OpenLaneCount);
    }

    [Fact]
    public void Octopus_merge_opens_one_lane_per_extra_parent()
    {
        using var engine = Build((M, [B, C, D]), (B, [A]), (C, [A]), (D, [A]), (A, []));

        Assert.Equal(3, engine.MaxLaneCount);
        AssertRow(engine, 0, node: 0, incoming: [], outgoing: [0, 1, 2], passThrough: []);
        AssertRow(engine, 1, node: 0, incoming: [0], outgoing: [0], passThrough: [1, 2]);
        AssertRow(engine, 2, node: 1, incoming: [1], outgoing: [1], passThrough: [0, 2]);
        AssertRow(engine, 3, node: 2, incoming: [2], outgoing: [2], passThrough: [0, 1]);
        AssertRow(engine, 4, node: 0, incoming: [0, 1, 2], outgoing: [], passThrough: []);
    }

    [Fact]
    public void Merge_parent_already_expected_joins_the_existing_lane()
    {
        // B is a branch tip whose lane already leads to A when M merges A.
        using var engine = Build((B, [A]), (M, [C, A]), (C, [A]), (A, []));

        AssertRow(engine, 1, node: 1, incoming: [], outgoing: [1, 0], passThrough: [0]);
        AssertRow(engine, 2, node: 1, incoming: [1], outgoing: [1], passThrough: [0]);
        AssertRow(engine, 3, node: 0, incoming: [0, 1], outgoing: [], passThrough: []);
    }

    [Fact]
    public void Unrelated_histories_run_in_parallel_lanes()
    {
        using var engine = Build((X, [X0]), (Y, [Y0]), (X0, []), (Y0, []));

        AssertRow(engine, 0, node: 0, incoming: [], outgoing: [0], passThrough: []);
        AssertRow(engine, 1, node: 1, incoming: [], outgoing: [1], passThrough: [0]);
        AssertRow(engine, 2, node: 0, incoming: [0], outgoing: [], passThrough: [1]);
        AssertRow(engine, 3, node: 1, incoming: [1], outgoing: [], passThrough: []);
    }

    [Fact]
    public void Freed_lane_is_reused_by_the_next_branch()
    {
        using var engine = Build((X, [X0]), (Y, [Y0]), (X0, []), (Z, [Z0]), (Y0, []), (Z0, []));

        // Lane 0 is free after X0: Z takes it rather than opening lane 2.
        AssertRow(engine, 3, node: 0, incoming: [], outgoing: [0], passThrough: [1]);
        Assert.Equal(2, engine.MaxLaneCount);
        AssertRow(engine, 4, node: 1, incoming: [1], outgoing: [], passThrough: [0]);
        AssertRow(engine, 5, node: 0, incoming: [0], outgoing: [], passThrough: []);
    }

    [Fact]
    public void Trailing_free_lanes_are_trimmed()
    {
        using var engine = Build((X, [X0]), (Y, [Y0]), (Y0, []));

        Assert.Equal(1, engine.OpenLaneCount);
        Assert.Equal(2, engine.MaxLaneCount);
    }

    [Fact]
    public void Long_branch_keeps_its_lane_across_row_blocks_while_open_and_once_closed()
    {
        // Main line of 600 commits; a branch tip on row 1 whose parent is the root (last row).
        const int length = 600;
        const int tip = length;
        using var engine = new LaneEngine();
        engine.Append(length - 1, [length - 2]);
        engine.Append(tip, [0]);
        for (var id = length - 2; id > 0; id--)
        {
            engine.Append(id, [id - 1]);
        }

        // Rows 2..599 exist; the branch lane is still open.
        var lanes = new int[engine.MaxLaneCount];
        foreach (var row in (int[])[2, 255, 256, 257, 511, 512, engine.RowCount - 1])
        {
            Assert.Equal([1], lanes.AsSpan(0, engine.GetPassThroughLanes(row, lanes)).ToArray());
        }

        engine.Append(0, []);

        Assert.Equal(0, engine.OpenLaneCount);
        AssertRow(engine, length, node: 0, incoming: [0, 1], outgoing: [], passThrough: []);
        foreach (var row in (int[])[2, 255, 256, 257, 511, 512, length - 1])
        {
            Assert.Equal([1], lanes.AsSpan(0, engine.GetPassThroughLanes(row, lanes)).ToArray());
        }

        // The tip row is crossed by the main line.
        Assert.Equal([0], lanes.AsSpan(0, engine.GetPassThroughLanes(1, lanes)).ToArray());
    }

    [Fact]
    public void Lines_are_continuous_between_consecutive_rows()
    {
        var history = SyntheticHistory.Generate(5_000);
        using var engine = new LaneEngine();
        for (var row = 0; row < history.Count; row++)
        {
            engine.Append(history.IdAt(row), history.ParentsAt(row));
        }

        Assert.True(engine.MaxLaneCount > 3, $"Synthetic history too narrow ({engine.MaxLaneCount} lanes).");
        Assert.Equal(0, engine.OpenLaneCount);

        var buffer = new int[engine.MaxLaneCount];
        var below = new SortedSet<int>();
        for (var row = 0; row < engine.RowCount; row++)
        {
            var passThrough = buffer.AsSpan(0, engine.GetPassThroughLanes(row, buffer));
            var incoming = engine.GetIncomingLanes(row);

            // What enters the row from above is exactly what left the row above.
            var above = new SortedSet<int>(passThrough.ToArray());
            above.UnionWith(incoming.ToArray());
            Assert.Equal(below, above);

            // A pass-through line never crosses the node.
            Assert.DoesNotContain(engine.GetNodeLane(row), passThrough.ToArray());
            Assert.Equal(history.ParentsAt(row).Length, engine.GetOutgoingLanes(row).Length);

            below = new SortedSet<int>(passThrough.ToArray());
            below.UnionWith(engine.GetOutgoingLanes(row).ToArray());
        }

        Assert.Empty(below);
    }

    [Fact]
    public void Disposed_engine_rejects_appends()
    {
        var engine = new LaneEngine();
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => engine.Append(A, []));
    }

    [Fact]
    public void A_stash_lane_is_dotted_from_the_stash_to_its_parent()
    {
        // S is a stash made on B; C is a later commit on B.
        using var engine = new LaneEngine();
        engine.Append(A, [B], dotted: true);
        engine.Append(C, [B]);
        engine.Append(B, [Y]);
        engine.Append(Y, []);

        // The stash opens lane 0 and its line is dotted; C takes lane 1, solid; both reach B.
        Assert.Equal([true], engine.GetOutgoingDotted(0).ToArray());
        Assert.Equal([false], engine.GetOutgoingDotted(1).ToArray());
        var lanes = new int[engine.MaxLaneCount];
        var dotted = new bool[engine.MaxLaneCount];
        var count = engine.GetPassThroughLanes(1, lanes, dotted);
        Assert.Equal([0], lanes[..count]);
        Assert.Equal([true], dotted[..count]);
        Assert.Equal([0, 1], engine.GetIncomingLanes(2).ToArray());
        Assert.Equal([true, false], engine.GetIncomingDotted(2).ToArray());
        // The lane is solid again once reused.
        Assert.Equal([false], engine.GetOutgoingDotted(2).ToArray());
    }

    [Fact]
    public void A_stash_takes_the_column_right_of_the_lane_leading_to_its_base_even_when_one_is_free_on_the_left()
    {
        using var engine = new LaneEngine();
        engine.Append(X0, [Y0]);   // lane 0, waits for Y0
        engine.Append(C, [B]);     // lane 1, waits for B
        engine.Append(Y0, []);     // lane 0 is free again

        engine.Append(A, [B], dotted: true);

        Assert.Equal(2, engine.GetNodeLane(3));
        Assert.Equal([2], engine.GetOutgoingLanes(3).ToArray());
    }

    [Fact]
    public void A_commit_whose_children_are_stashes_does_not_take_the_column_of_a_stash()
    {
        using var engine = new LaneEngine();
        engine.Append(A, [B], dotted: true);   // stash in lane 0
        engine.Append(C, [B], dotted: true);   // stash in lane 1

        engine.Append(B, []);

        Assert.Equal([0, 1], engine.GetIncomingLanes(2).ToArray());
        Assert.Equal(2, engine.GetNodeLane(2));
    }

    [Fact]
    public void A_commit_with_a_solid_child_keeps_the_leftmost_solid_lane_even_when_a_stash_is_further_left()
    {
        using var engine = new LaneEngine();
        engine.Append(A, [B], dotted: true);   // stash in lane 0
        engine.Append(C, [B]);                 // commit in lane 1

        engine.Append(B, []);

        Assert.Equal(1, engine.GetNodeLane(2));
    }

    private static LaneEngine Build(params (int Id, int[] Parents)[] commits)
    {
        var engine = new LaneEngine();
        foreach (var (id, parents) in commits)
        {
            engine.Append(id, parents);
        }

        return engine;
    }

    private static void AssertRow(LaneEngine engine, int row, int node, int[] incoming, int[] outgoing, int[] passThrough)
    {
        var lanes = new int[Math.Max(1, engine.MaxLaneCount)];
        Assert.Equal(node, engine.GetNodeLane(row));
        Assert.Equal(incoming, engine.GetIncomingLanes(row).ToArray());
        Assert.Equal(outgoing, engine.GetOutgoingLanes(row).ToArray());
        Assert.Equal(passThrough, lanes.AsSpan(0, engine.GetPassThroughLanes(row, lanes)).ToArray());
    }
}
