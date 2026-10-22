using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Vigo.Core.Models;
using Vigo.TestUtilities;

namespace Vigo.Benchmarks;

/// <summary>
/// Builds a <see cref="CommitGraph"/> from pages of commits, as the graph view model does while the log streams:
/// object id → dense id mapping plus lane layout.
/// </summary>
[MemoryDiagnoser]
public class CommitGraphBenchmarks
{
    private const int PageSize = 2000;

    private CommitInfo[][] _pages = [];

    [Params(100_000, 1_000_000)]
    public int Commits { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var history = SyntheticHistory.Generate(Commits);
        var shas = new GitSha[history.Count];
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        for (var id = 0; id < shas.Length; id++)
        {
            BitConverter.TryWriteBytes(buffer, id);
            shas[id] = GitSha.Parse(Convert.ToHexString(SHA1.HashData(buffer)));
        }

        var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var commits = new CommitInfo[history.Count];
        for (var row = 0; row < commits.Length; row++)
        {
            var parentIds = history.ParentsAt(row);
            var parents = new GitSha[parentIds.Length];
            for (var i = 0; i < parents.Length; i++)
            {
                parents[i] = shas[parentIds[i]];
            }

            commits[row] = new CommitInfo(shas[history.IdAt(row)], parents, "Author", "author@example.com", date, "Subject");
        }

        _pages = [.. commits.Chunk(PageSize)];
    }

    [Benchmark]
    public int AppendAllPages()
    {
        using var graph = new CommitGraph();
        foreach (var page in _pages)
        {
            graph.Append(page);
        }

        return graph.Lanes.MaxLaneCount;
    }
}
