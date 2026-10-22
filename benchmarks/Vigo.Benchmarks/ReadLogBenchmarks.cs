using BenchmarkDotNet.Attributes;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git;
using Vigo.Git.Cli;

namespace Vigo.Benchmarks;

/// <summary>
/// Streams the whole log of a real repository given by the <c>VIGO_BENCH_REPO</c> environment variable
/// (e.g. a clone of the Linux kernel, kept outside this repository).
/// </summary>
[MemoryDiagnoser]
public class ReadLogBenchmarks
{
    private const string RepositoryVariable = "VIGO_BENCH_REPO";

    private readonly GitService _git = new(new GitCli());
    private string _repository = "";

    [GlobalSetup]
    public void Setup()
    {
        _repository = Environment.GetEnvironmentVariable(RepositoryVariable) is { Length: > 0 } path
            ? path
            : throw new InvalidOperationException($"Set {RepositoryVariable} to the path of a Git repository.");
    }

    /// <summary>git.exe + parsing only.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> ReadLog()
    {
        var count = 0;
        var result = await _git.ReadLogAsync(_repository, CommitLogOptions.Default, (page, _) =>
        {
            count += page.Count;
            return ValueTask.CompletedTask;
        });

        return Check(result, count);
    }

    /// <summary>Read and lay out, as when opening a repository.</summary>
    [Benchmark]
    public async Task<int> ReadLogIntoGraph()
    {
        using var graph = new CommitGraph();
        var result = await _git.ReadLogAsync(_repository, CommitLogOptions.Default, (page, _) =>
        {
            graph.Append(page);
            return ValueTask.CompletedTask;
        });

        return Check(result, graph.Commits.Count);
    }

    private static int Check(GitResult result, int count) => result.Value switch
    {
        Done => count,
        var other => throw new InvalidOperationException($"Log failed: {other}"),
    };
}
