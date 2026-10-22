using BenchmarkDotNet.Attributes;
using Vigo.Graph;
using Vigo.TestUtilities;

namespace Vigo.Benchmarks;

/// <summary>Lane layout of a synthetic history, without any Git object id (pure engine cost).</summary>
[MemoryDiagnoser]
public class LaneEngineBenchmarks
{
    private SyntheticHistory _history = null!;
    private LaneEngine _built = null!;
    private int[] _passThrough = [];

    [Params(100_000, 1_000_000)]
    public int Commits { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _history = SyntheticHistory.Generate(Commits);
        _built = Layout(_history);
        _passThrough = new int[_built.MaxLaneCount];
    }

    [GlobalCleanup]
    public void Cleanup() => _built.Dispose();

    /// <summary>Lays out the whole history, row by row.</summary>
    [Benchmark]
    public int AppendAll()
    {
        using var engine = Layout(_history);
        return engine.MaxLaneCount;
    }

    /// <summary>Reads back everything a renderer needs for every row (a viewport only reads ~50 rows).</summary>
    [Benchmark]
    public int ReadAllRows()
    {
        var total = 0;
        for (var row = 0; row < _built.RowCount; row++)
        {
            total += _built.GetNodeLane(row)
                + _built.GetIncomingLanes(row).Length
                + _built.GetOutgoingLanes(row).Length
                + _built.GetPassThroughLanes(row, _passThrough);
        }

        return total;
    }

    private static LaneEngine Layout(SyntheticHistory history)
    {
        var engine = new LaneEngine();
        for (var row = 0; row < history.Count; row++)
        {
            engine.Append(history.IdAt(row), history.ParentsAt(row));
        }

        return engine;
    }
}
