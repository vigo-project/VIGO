using Vigo.Core.Services;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class GitCliCommandLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Commands_are_published_when_they_start_and_when_they_end()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        var log = new GitCommandLog();
        var started = new List<GitCommandRun>();
        var completed = new List<GitCommandRun>();
        // The config reads GitCli makes to prepare a command are reads too: only the writes are checked here.
        log.Started += r => { if (!r.IsRead) started.Add(r); };
        log.Completed += r => { if (!r.IsRead) completed.Add(r); };
        var cli = new GitCli { CommandLog = log };

        await cli.RunAsync(repo.Path, ["tag", "v1"], Ct);
        await cli.RunAsync(repo.Path, ["checkout", "missing-branch"], Ct);

        Assert.Equal(["git tag v1", "git checkout missing-branch"], started.Select(r => r.CommandLine));
        Assert.Equal(started, completed);
        Assert.Equal(0, completed[0].ExitCode);
        Assert.NotEqual(0, completed[1].ExitCode);
        Assert.NotEmpty(completed[1].Output);
        Assert.All(started, r => Assert.False(r.IsRead));
    }

    [Fact]
    public async Task Reads_are_flagged_so_the_log_can_hide_them()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        var log = new GitCommandLog();
        var runs = new List<GitCommandRun>();
        log.Completed += runs.Add;

        await new GitCli { CommandLog = log }.RunAsync(repo.Path, ["status", "--porcelain=v2"], Ct);

        var run = Assert.Single(runs);
        Assert.True(run.IsRead);
        Assert.Equal(0, run.ExitCode);
    }
}
