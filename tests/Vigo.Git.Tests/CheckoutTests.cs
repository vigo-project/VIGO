using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class CheckoutTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private static string CurrentBranch(TestRepoBuilder repo) => repo.Git("branch", "--show-current").Trim();

    [Fact]
    public async Task Switches_to_a_local_branch_and_carries_non_conflicting_changes()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"), ("b.txt", "b\n"));
        repo.Branch("feature");
        repo.Commit("feature", ("b.txt", "feature\n"));
        repo.Checkout("main");
        repo.WriteFile("a.txt", "local\n");

        AssertDone(await _git.CheckoutAsync(repo.Path, new CheckoutBranch("feature"), Ct));

        Assert.Equal("feature", CurrentBranch(repo));
        Assert.Equal("local\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [Fact]
    public async Task Conflicting_local_changes_are_refused_with_an_explanation()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.Branch("feature");
        repo.Commit("feature", ("a.txt", "feature\n"));
        repo.Checkout("main");
        repo.WriteFile("a.txt", "local\n");

        var result = await _git.CheckoutAsync(repo.Path, new CheckoutBranch("feature"), Ct);

        var failure = Assert.IsType<Failure>(result.Value);
        Assert.StartsWith("Des modifications locales seraient écrasées", failure.Message, StringComparison.Ordinal);
        Assert.Contains("a.txt", failure.Details, StringComparison.Ordinal);
        Assert.Equal(FailureReason.LocalChangesWouldBeOverwritten, failure.Reason);
        Assert.Equal("main", CurrentBranch(repo));
    }

    [Fact]
    public async Task Remote_branch_creates_a_tracking_local_branch()
    {
        using var remote = TestRepoBuilder.Create();
        remote.Commit("init", ("a.txt", "a\n"));
        remote.Branch("feature/x");
        var tip = remote.Commit("x", ("a.txt", "x\n"));
        remote.Checkout("main");
        using var repo = TestRepoBuilder.Clone(remote.Path);

        AssertDone(await _git.CheckoutAsync(repo.Path, new CheckoutRemoteBranch("origin/feature/x", "feature/x"), Ct));

        Assert.Equal("feature/x", CurrentBranch(repo));
        Assert.Equal(tip, repo.Head());
        Assert.Equal("origin/feature/x", repo.Git("rev-parse", "--abbrev-ref", "@{upstream}").Trim());

        // Second time: the local branch exists.
        repo.Checkout("main");
        var again = await _git.CheckoutAsync(repo.Path, new CheckoutRemoteBranch("origin/feature/x", "feature/x"), Ct);
        Assert.Equal("Une branche locale feature/x existe déjà.", Assert.IsType<Failure>(again.Value).Message);
    }

    [Fact]
    public async Task Commit_checkout_detaches_head()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("one", ("a.txt", "1\n"));
        repo.Commit("two", ("a.txt", "2\n"));

        AssertDone(await _git.CheckoutAsync(repo.Path, new CheckoutCommit(GitSha.Parse(first)), Ct));

        Assert.Equal(first, repo.Head());
        Assert.Equal(string.Empty, CurrentBranch(repo));
    }

    [Fact]
    public async Task Option_like_names_are_refused()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));

        var result = await _git.CheckoutAsync(repo.Path, new CheckoutBranch("--orphan"), Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.Equal("main", CurrentBranch(repo));
    }
}
