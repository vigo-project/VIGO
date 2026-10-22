using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class MergeRebaseTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private static T AssertSuccess<T>(GitResult<T> result)
    {
        Assert.True(result.Value is Success<T>, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());
        return ((Success<T>)result.Value!).Value;
    }

    private static string CurrentBranch(TestRepoBuilder repo) => repo.Git("branch", "--show-current").Trim();

    private async Task<WorkingTreeStatus> StatusAsync(TestRepoBuilder repo) => AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct));

    /// <summary>
    /// main: base → main-2 (a.txt changed); feature (from base): feature-1 (b.txt). Diverged, no conflict.
    /// The user's global merge / rebase settings must not change the outcome.
    /// </summary>
    private static TestRepoBuilder Diverged(out string mainTip, out string featureTip, string featureFile = "b.txt", string featureContent = "feature\n")
    {
        var repo = TestRepoBuilder.Create();
        repo.Git("config", "merge.ff", "true");
        repo.Git("config", "rebase.autoStash", "false");
        repo.Commit("base", ("a.txt", "base\n"));
        repo.Branch("feature");
        featureTip = repo.Commit("feature-1", (featureFile, featureContent));
        repo.Checkout("main");
        mainTip = repo.Commit("main-2", ("a.txt", "main\n"));
        return repo;
    }

    [Fact]
    public async Task Tells_whether_a_commit_is_an_ancestor()
    {
        using var repo = TestRepoBuilder.Create();
        var first = GitSha.Parse(repo.Commit("one", ("a.txt", "1\n")));
        var second = GitSha.Parse(repo.Commit("two", ("a.txt", "2\n")));

        Assert.True(AssertSuccess(await _git.IsAncestorAsync(repo.Path, first, second, Ct)));
        Assert.True(AssertSuccess(await _git.IsAncestorAsync(repo.Path, second, second, Ct)));
        Assert.False(AssertSuccess(await _git.IsAncestorAsync(repo.Path, second, first, Ct)));
    }

    [Fact]
    public async Task Merges_with_a_fast_forward_when_possible()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Git("config", "merge.ff", "true");
        repo.Commit("base", ("a.txt", "1\n"));
        repo.Branch("feature");
        var tip = repo.Commit("feature", ("a.txt", "2\n"));
        repo.Checkout("main");

        Assert.Equal(MergeOutcome.FastForward, AssertSuccess(await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)));
        Assert.Equal(tip, repo.Head());
        Assert.Equal("main", CurrentBranch(repo));
    }

    [Fact]
    public async Task Creates_a_merge_commit_when_the_branches_diverged()
    {
        using var repo = Diverged(out var mainTip, out var featureTip);

        Assert.Equal(MergeOutcome.MergeCommit, AssertSuccess(await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)));
        Assert.Equal($"{mainTip} {featureTip}", repo.Git("log", "-1", "--format=%P").Trim());
        Assert.Equal("Merge branch 'feature'", repo.Git("log", "-1", "--format=%s").Trim());
        Assert.Null((await StatusAsync(repo)).Operation);
    }

    [Fact]
    public async Task Follows_the_merge_ff_setting_of_the_user()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Git("config", "merge.ff", "false");
        var main = repo.Commit("base", ("a.txt", "1\n"));
        repo.Branch("feature");
        repo.Commit("feature", ("a.txt", "2\n"));
        repo.Checkout("main");

        Assert.Equal(MergeOutcome.MergeCommit, AssertSuccess(await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)));
        Assert.StartsWith(main, repo.Git("log", "-1", "--format=%P").Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Says_when_there_is_nothing_to_merge()
    {
        using var repo = Diverged(out var mainTip, out _);
        repo.Git("merge", "--no-edit", "--quiet", "feature");
        var head = repo.Head();

        Assert.Equal(MergeOutcome.UpToDate, AssertSuccess(await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)));
        Assert.Equal(head, repo.Head());
    }

    [Fact]
    public async Task A_conflicting_merge_stays_in_progress_until_aborted()
    {
        using var repo = Diverged(out var mainTip, out _, featureFile: "a.txt", featureContent: "feature\n");

        var result = await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Equal(["a.txt"], conflict.Paths);
        var status = await StatusAsync(repo);
        Assert.Equal(RepositoryOperationKind.Merge, status.Operation?.Kind);
        Assert.Equal("feature", status.Operation?.Source);
        Assert.Equal("Merge branch 'feature'", status.Operation?.Message);

        AssertDone(await _git.AbortOperationAsync(repo.Path, RepositoryOperationKind.Merge, Ct));
        status = await StatusAsync(repo);
        Assert.Null(status.Operation);
        Assert.True(status.IsClean);
        Assert.Equal(mainTip, repo.Head());
    }

    [Fact]
    public async Task A_modify_delete_conflict_lists_the_path()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Git("config", "merge.ff", "true");
        repo.Commit("base", ("a.txt", "base\n"), ("b.txt", "b\n"));
        repo.Branch("feature");
        repo.DeleteFile("a.txt");
        repo.Commit("delete a");
        repo.Checkout("main");
        repo.Commit("change a", ("a.txt", "main\n"));

        var conflict = Assert.IsType<Conflict>((await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)).Value);

        Assert.Equal(["a.txt"], conflict.Paths);
    }

    [Fact]
    public async Task A_resolved_merge_is_concluded_by_a_commit()
    {
        using var repo = Diverged(out var mainTip, out var featureTip, featureFile: "a.txt");
        await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct);
        repo.WriteFile("a.txt", "both\n");
        AssertDone(await _git.StageAsync(repo.Path, ["a.txt"], Ct));

        var status = await StatusAsync(repo);
        AssertSuccess(await _git.CommitAsync(repo.Path, new CommitRequest(status.Operation!.Message!), Ct));

        Assert.Equal($"{mainTip} {featureTip}", repo.Git("log", "-1", "--format=%P").Trim());
        Assert.Null((await StatusAsync(repo)).Operation);
    }

    [Fact]
    public async Task Refuses_a_merge_that_would_overwrite_local_changes()
    {
        using var repo = Diverged(out _, out _);
        repo.WriteFile("b.txt", "local\n");

        var failure = Assert.IsType<Failure>((await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)).Value);

        Assert.StartsWith("Des modifications locales seraient écrasées", failure.Message, StringComparison.Ordinal);
        Assert.Null((await StatusAsync(repo)).Operation);
    }

    [Fact]
    public async Task Refuses_a_merge_while_another_one_is_in_progress()
    {
        using var repo = Diverged(out _, out _, featureFile: "a.txt");
        repo.Branch("other", checkout: false);
        await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct);

        var failure = Assert.IsType<Failure>((await _git.MergeAsync(repo.Path, "refs/heads/other", Ct)).Value);

        Assert.StartsWith("Une opération (fusion, rebase, cherry-pick ou revert) est déjà en cours", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fast_forwards_a_branch_that_is_not_checked_out()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "1\n"));
        repo.Branch("feature", checkout: false);
        var tip = repo.Commit("main", ("a.txt", "2\n"));

        AssertDone(await _git.FastForwardBranchAsync(repo.Path, "feature", GitSha.Parse(tip), Ct));

        Assert.Equal(tip, repo.Git("rev-parse", "feature").Trim());
        Assert.Equal("main", CurrentBranch(repo));
        Assert.Equal("2\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [Fact]
    public async Task Refuses_to_fast_forward_a_diverged_or_checked_out_branch()
    {
        using var repo = Diverged(out var mainTip, out var featureTip);

        var diverged = Assert.IsType<Failure>((await _git.FastForwardBranchAsync(repo.Path, "feature", GitSha.Parse(mainTip), Ct)).Value);
        Assert.Contains("ont divergé", diverged.Message, StringComparison.Ordinal);
        Assert.Equal(featureTip, repo.Git("rev-parse", "feature").Trim());

        repo.Git("merge", "--no-edit", "--quiet", "feature");
        var merged = repo.Head();
        repo.Checkout("feature");
        var checkedOut = Assert.IsType<Failure>((await _git.FastForwardBranchAsync(repo.Path, "feature", GitSha.Parse(merged), Ct)).Value);
        Assert.Contains("est extraite", checkedOut.Message, StringComparison.Ordinal);
        Assert.Equal(featureTip, repo.Head());
    }

    [Fact]
    public async Task Rebases_the_current_branch()
    {
        using var repo = Diverged(out var mainTip, out _);
        repo.Checkout("feature");

        Assert.Equal(RebaseOutcome.Rebased, AssertSuccess(await _git.RebaseAsync(repo.Path, "refs/heads/main", null, Ct)));

        Assert.Equal(mainTip, repo.Git("rev-parse", "HEAD~1").Trim());
        Assert.Equal("feature", CurrentBranch(repo));
        Assert.Equal("feature-1", repo.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Rebases_another_branch_after_switching_to_it()
    {
        using var repo = Diverged(out var mainTip, out _);

        Assert.Equal(RebaseOutcome.Rebased, AssertSuccess(await _git.RebaseAsync(repo.Path, "refs/heads/main", "feature", Ct)));

        Assert.Equal("feature", CurrentBranch(repo));
        Assert.Equal(mainTip, repo.Git("rev-parse", "feature~1").Trim());
    }

    [Fact]
    public async Task Says_when_the_branch_is_already_on_top()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "1\n"));
        repo.Branch("feature");
        var tip = repo.Commit("feature", ("a.txt", "2\n"));

        Assert.Equal(RebaseOutcome.UpToDate, AssertSuccess(await _git.RebaseAsync(repo.Path, "refs/heads/main", null, Ct)));
        Assert.Equal(tip, repo.Head());
    }

    [Fact]
    public async Task A_conflicting_rebase_reports_its_step_and_continues_once_resolved()
    {
        using var repo = Diverged(out var mainTip, out _, featureFile: "a.txt");
        repo.Checkout("feature");
        repo.Commit("feature-2", ("c.txt", "c\n"));

        var conflict = Assert.IsType<Conflict>((await _git.RebaseAsync(repo.Path, "refs/heads/main", null, Ct)).Value);

        Assert.Equal(["a.txt"], conflict.Paths);
        var operation = (await StatusAsync(repo)).Operation;
        Assert.Equal(RepositoryOperationKind.Rebase, operation?.Kind);
        Assert.Equal("feature", operation?.Branch);
        Assert.Equal(GitSha.Parse(mainTip), operation?.Onto);
        Assert.Equal((1, 2), (operation?.Step, operation?.StepCount));

        var refused = Assert.IsType<Failure>((await _git.ContinueOperationAsync(repo.Path, RepositoryOperationKind.Rebase, Ct)).Value);
        Assert.StartsWith("Des fichiers sont encore en conflit", refused.Message, StringComparison.Ordinal);

        repo.WriteFile("a.txt", "both\n");
        AssertDone(await _git.StageAsync(repo.Path, ["a.txt"], Ct));
        AssertDone(await _git.ContinueOperationAsync(repo.Path, RepositoryOperationKind.Rebase, Ct));

        Assert.Null((await StatusAsync(repo)).Operation);
        Assert.Equal("feature", CurrentBranch(repo));
        Assert.Equal(["feature-2", "feature-1", "main-2"], repo.Git("log", "-3", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("feature-1", repo.Git("log", "-1", "--format=%s", "HEAD~1").Trim());
    }

    [Fact]
    public async Task A_stopped_rebase_can_skip_the_commit_or_be_aborted()
    {
        using var repo = Diverged(out var mainTip, out var featureTip, featureFile: "a.txt");
        repo.Checkout("feature");
        repo.Commit("feature-2", ("c.txt", "c\n"));
        var featureHead = repo.Head();

        await _git.RebaseAsync(repo.Path, "refs/heads/main", null, Ct);
        AssertDone(await _git.AbortOperationAsync(repo.Path, RepositoryOperationKind.Rebase, Ct));
        Assert.Null((await StatusAsync(repo)).Operation);
        Assert.Equal(featureHead, repo.Head());

        await _git.RebaseAsync(repo.Path, "refs/heads/main", null, Ct);
        AssertDone(await _git.SkipOperationAsync(repo.Path, RepositoryOperationKind.Rebase, Ct));
        Assert.Null((await StatusAsync(repo)).Operation);
        Assert.Equal(["feature-2", "main-2"], repo.Git("log", "-2", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Refuses_to_rebase_with_local_changes()
    {
        using var repo = Diverged(out _, out _);
        repo.Checkout("feature");
        repo.WriteFile("b.txt", "local\n");

        var failure = Assert.IsType<Failure>((await _git.RebaseAsync(repo.Path, "refs/heads/main", null, Ct)).Value);

        Assert.StartsWith("Le rebase demande un répertoire de travail propre", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aborting_when_nothing_is_in_progress_is_done()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "1\n"));

        AssertDone(await _git.AbortOperationAsync(repo.Path, RepositoryOperationKind.Merge, Ct));
        AssertDone(await _git.AbortOperationAsync(repo.Path, RepositoryOperationKind.Rebase, Ct));
    }
}
