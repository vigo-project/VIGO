using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Stats;

namespace Vigo.Git;

/// <summary>Statistics of the history for the "Statistiques" page (J7.14, ADR-080).</summary>
public sealed partial class GitService
{
    public async Task<GitResult<RepositoryStats>> ReadCommitStatsAsync(string repositoryPath, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        // An unborn HEAD has no log: git would fail on it.
        var head = await ReadOptionalAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
        switch (head.Value)
        {
            case Success<string?> { Value: null }:
                return new Success<RepositoryStats>(RepositoryStats.Empty);
            case Success<string?>:
                break;
            case Canceled c:
                return c;
            case Failure f:
                return f;
            default:
                return new Failure(Loc.Get("Common.UnexpectedResult"));
        }

        var reader = new CommitStatsReader(progress);
        try
        {
            var result = await Cli.RunStreamingAsync(repositoryPath, CommitStatsReader.BuildArguments(), reader.ReadAsync, cancellationToken);
            return result.Value switch
            {
                Done => new Success<RepositoryStats>(reader.Build()),
                Canceled c => c,
                Failure f => f,
                _ => new Failure(Loc.Get("Common.UnexpectedResult")),
            };
        }
        catch (FormatException ex)
        {
            return new Failure($"git log output is unreadable: {ex.Message}", Exception: ex);
        }
    }
}
