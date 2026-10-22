using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class GitCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Already_canceled_token_returns_Canceled_without_starting_git()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await new GitCli().RunAsync(null, ["--version"], cts.Token);

        Assert.IsType<Canceled>(result.Value);
    }

    [Fact]
    public async Task Failing_command_returns_Failure_with_exit_code_and_message()
    {
        using var repo = TestRepoBuilder.Create();

        var result = await new GitCli().RunAsync(repo.Path, ["rev-parse", "--verify", "does-not-exist"], Ct);

        var failure = Assert.IsType<Failure>(result.Value);
        Assert.NotEqual(0, failure.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }

    [Fact]
    public async Task Merge_conflict_is_classified_as_Conflict_with_paths()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "base\n"));
        repo.Branch("feature");
        repo.Commit("feature change", ("a.txt", "feature\n"));
        repo.Checkout("main");
        repo.Commit("main change", ("a.txt", "main\n"));

        var result = await new GitCli().RunAsync(repo.Path, ["-c", "user.name=T", "-c", "user.email=t@t", "merge", "feature"], Ct);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Equal(["a.txt"], conflict.Paths);
        // Global options (-c key=value) are skipped when naming the operation.
        Assert.Equal("merge", conflict.Operation);
    }

    [Fact]
    public void Remote_is_found_after_global_options()
    {
        var result = GitCli.Classify(new CliOutput(128, "", "fatal: Authentication failed"), ["-c", "credential.helper=", "push", "--force", "upstream"]);

        Assert.Equal("upstream", Assert.IsType<AuthenticationFailed>(result.Value).Remote);
    }

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://example.com/repo.git/'")]
    [InlineData("git@github.com: Permission denied (publickey).")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    public void Authentication_errors_are_classified(string stderr)
    {
        var result = GitCli.Classify(new CliOutput(128, "", stderr), ["fetch", "origin"]);

        var auth = Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.Equal("origin", auth.Remote);
    }
}
