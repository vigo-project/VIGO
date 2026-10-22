using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class HistoryOperationTests
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

    private async Task<WorkingTreeStatus> StatusAsync(TestRepoBuilder repo) => AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct));

    /// <summary>main: base (a.txt) → main-2 (a.txt); feature (from base): feature-1 (b.txt), feature-2 (a.txt: conflicts with main).</summary>
    private static TestRepoBuilder Repo(out string pick, out string conflicting)
    {
        var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "base\n"));
        repo.Branch("feature");
        pick = repo.Commit("feature-1", ("b.txt", "feature\n"));
        conflicting = repo.Commit("feature-2", ("a.txt", "feature\n"));
        repo.Checkout("main");
        repo.Commit("main-2", ("a.txt", "main\n"));
        return repo;
    }

    [Fact]
    public async Task Cherry_picks_a_commit_on_head()
    {
        using var repo = Repo(out var pick, out _);
        var before = repo.Head();

        var head = AssertSuccess(await _git.CherryPickAsync(repo.Path, GitSha.Parse(pick), Ct));

        Assert.Equal(GitSha.Parse(repo.Head()), head);
        Assert.Equal(before, repo.Git("rev-parse", "HEAD~1").Trim());
        Assert.Equal("feature-1", repo.Git("log", "-1", "--format=%s").Trim());
        Assert.Equal("feature\n", File.ReadAllText(Path.Combine(repo.Path, "b.txt")).ReplaceLineEndings("\n"));
        Assert.Null((await StatusAsync(repo)).Operation);
    }

    [Fact]
    public async Task A_conflicting_cherry_pick_stays_in_progress_and_continues_once_resolved()
    {
        using var repo = Repo(out _, out var conflicting);

        var conflict = Assert.IsType<Conflict>((await _git.CherryPickAsync(repo.Path, GitSha.Parse(conflicting), Ct)).Value);

        Assert.Equal(["a.txt"], conflict.Paths);
        var operation = (await StatusAsync(repo)).Operation;
        Assert.Equal(RepositoryOperationKind.CherryPick, operation?.Kind);
        Assert.Equal(conflicting, operation?.Source);
        Assert.Equal("feature-2", operation?.Message);

        repo.WriteFile("a.txt", "resolved\n").Stage("a.txt");
        AssertDone(await _git.ContinueOperationAsync(repo.Path, RepositoryOperationKind.CherryPick, Ct));

        Assert.Null((await StatusAsync(repo)).Operation);
        Assert.Equal("feature-2", repo.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task A_conflicting_cherry_pick_can_be_aborted_or_skipped()
    {
        using var repo = Repo(out _, out var conflicting);
        var before = repo.Head();

        Assert.IsType<Conflict>((await _git.CherryPickAsync(repo.Path, GitSha.Parse(conflicting), Ct)).Value);
        AssertDone(await _git.AbortOperationAsync(repo.Path, RepositoryOperationKind.CherryPick, Ct));
        Assert.Equal(before, repo.Head());
        Assert.True((await StatusAsync(repo)).IsClean);

        Assert.IsType<Conflict>((await _git.CherryPickAsync(repo.Path, GitSha.Parse(conflicting), Ct)).Value);
        AssertDone(await _git.SkipOperationAsync(repo.Path, RepositoryOperationKind.CherryPick, Ct));
        Assert.Equal(before, repo.Head());
        Assert.Null((await StatusAsync(repo)).Operation);
    }

    [Fact]
    public async Task Cherry_picking_changes_already_there_fails_and_changes_nothing()
    {
        using var repo = Repo(out var pick, out _);
        AssertSuccess(await _git.CherryPickAsync(repo.Path, GitSha.Parse(pick), Ct));
        var before = repo.Head();

        var failure = Assert.IsType<Failure>((await _git.CherryPickAsync(repo.Path, GitSha.Parse(pick), Ct)).Value);

        Assert.Contains("n'apporte aucune modification", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, repo.Head());
        var status = await StatusAsync(repo);
        Assert.Null(status.Operation);
        Assert.True(status.IsClean);
    }

    [Fact]
    public async Task A_merge_commit_is_cherry_picked_against_its_first_parent()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "base\n"));
        repo.Branch("topic");
        repo.Commit("topic-1", ("t.txt", "topic\n"));
        repo.Checkout("main");
        repo.Commit("main-2", ("m.txt", "main\n"));
        repo.Branch("integration");
        var merge = repo.Merge("topic");
        repo.Checkout("main");
        repo.Git("reset", "--hard", "HEAD~1");

        AssertSuccess(await _git.CherryPickAsync(repo.Path, GitSha.Parse(merge), Ct));

        Assert.True(File.Exists(Path.Combine(repo.Path, "t.txt")));
        Assert.Single(repo.Git("log", "-1", "--format=%P").Trim().Split(' '));
    }

    [Fact]
    public async Task Cherry_pick_refuses_to_overwrite_local_changes()
    {
        using var repo = Repo(out _, out var conflicting);
        repo.WriteFile("a.txt", "local\n");

        var failure = Assert.IsType<Failure>((await _git.CherryPickAsync(repo.Path, GitSha.Parse(conflicting), Ct)).Value);

        Assert.StartsWith("Des modifications locales seraient écrasées", failure.Message, StringComparison.Ordinal);
        Assert.Null((await StatusAsync(repo)).Operation);
    }

    [Fact]
    public async Task Reverts_a_commit_with_the_default_message()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("one", ("a.txt", "1\n"));
        var two = repo.Commit("two", ("b.txt", "2\n"));
        repo.Commit("three", ("c.txt", "3\n"));

        var head = AssertSuccess(await _git.RevertAsync(repo.Path, GitSha.Parse(two), Ct));

        Assert.Equal(GitSha.Parse(repo.Head()), head);
        Assert.Equal("Revert \"two\"", repo.Git("log", "-1", "--format=%s").Trim());
        Assert.False(File.Exists(Path.Combine(repo.Path, "b.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "c.txt")));
    }

    [Fact]
    public async Task A_conflicting_revert_stays_in_progress_with_its_message()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("one", ("a.txt", "1\n"));
        var two = repo.Commit("two", ("a.txt", "2\n"));
        repo.Commit("three", ("a.txt", "3\n"));

        var conflict = Assert.IsType<Conflict>((await _git.RevertAsync(repo.Path, GitSha.Parse(two), Ct)).Value);

        Assert.Equal(["a.txt"], conflict.Paths);
        var operation = (await StatusAsync(repo)).Operation;
        Assert.Equal(RepositoryOperationKind.Revert, operation?.Kind);
        Assert.Equal(two, operation?.Source);
        Assert.StartsWith("Revert \"two\"", operation?.Message, StringComparison.Ordinal);

        repo.WriteFile("a.txt", "resolved\n").Stage("a.txt");
        AssertDone(await _git.ContinueOperationAsync(repo.Path, RepositoryOperationKind.Revert, Ct));
        Assert.Null((await StatusAsync(repo)).Operation);
        Assert.Equal("Revert \"two\"", repo.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Soft_reset_keeps_the_changes_staged()
    {
        using var repo = TestRepoBuilder.Create();
        var one = repo.Commit("one", ("a.txt", "1\n"));
        repo.Commit("two", ("a.txt", "2\n"));

        AssertDone(await _git.ResetAsync(repo.Path, GitSha.Parse(one), ResetMode.Soft, Ct));

        Assert.Equal(one, repo.Head());
        var entry = Assert.Single((await StatusAsync(repo)).Entries);
        Assert.True(entry is { Path: "a.txt", IsStaged: true, IsUnstaged: false });
    }

    [Fact]
    public async Task Mixed_reset_keeps_the_changes_unstaged()
    {
        using var repo = TestRepoBuilder.Create();
        var one = repo.Commit("one", ("a.txt", "1\n"));
        repo.Commit("two", ("a.txt", "2\n"));

        AssertDone(await _git.ResetAsync(repo.Path, GitSha.Parse(one), ResetMode.Mixed, Ct));

        Assert.Equal(one, repo.Head());
        var entry = Assert.Single((await StatusAsync(repo)).Entries);
        Assert.True(entry is { Path: "a.txt", IsStaged: false, IsUnstaged: true });
    }

    [Fact]
    public async Task Hard_reset_drops_tracked_changes_and_keeps_untracked_files()
    {
        using var repo = TestRepoBuilder.Create();
        var one = repo.Commit("one", ("a.txt", "1\n"));
        repo.Commit("two", ("a.txt", "2\n"));
        repo.WriteFile("a.txt", "local\n").WriteFile("new.txt", "untracked\n");

        AssertDone(await _git.ResetAsync(repo.Path, GitSha.Parse(one), ResetMode.Hard, Ct));

        Assert.Equal(one, repo.Head());
        Assert.Equal("1\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        var entry = Assert.Single((await StatusAsync(repo)).Entries);
        Assert.True(entry is { Path: "new.txt", IsUntracked: true });
    }

    [Fact]
    public async Task Counts_the_commits_between_two_revisions()
    {
        using var repo = TestRepoBuilder.Create();
        var one = GitSha.Parse(repo.Commit("one", ("a.txt", "1\n")));
        repo.Commit("two", ("a.txt", "2\n"));
        var three = GitSha.Parse(repo.Commit("three", ("a.txt", "3\n")));

        Assert.Equal(2, AssertSuccess(await _git.CountCommitsAsync(repo.Path, one, three, Ct)));
        Assert.Equal(0, AssertSuccess(await _git.CountCommitsAsync(repo.Path, three, one, Ct)));
    }

    [Fact]
    public async Task A_merge_cannot_be_skipped()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("one", ("a.txt", "1\n"));

        Assert.IsType<Failure>((await _git.SkipOperationAsync(repo.Path, RepositoryOperationKind.Merge, Ct)).Value);
    }
}
