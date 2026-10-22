using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Snapshots of HEAD, branches and tags, and their restoration (Undo / Redo, J5.2).</summary>
public sealed class UndoRestoreTests
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

    private async Task<RefSnapshot> SnapshotAsync(TestRepoBuilder repo) => AssertSuccess(await _git.GetRefSnapshotAsync(repo.Path, Ct));

    private async Task RestoreAsync(TestRepoBuilder repo, RefSnapshot target, ResetMode mode = ResetMode.Keep) =>
        AssertDone(await _git.RestoreRefsAsync(repo.Path, await SnapshotAsync(repo), target, mode, Ct));

    private static string Status(TestRepoBuilder repo) => repo.Git("status", "--porcelain").ReplaceLineEndings("\n").TrimEnd();

    [Fact]
    public async Task The_snapshot_keeps_annotated_tags_and_upstreams()
    {
        using var repo = TestRepoBuilder.Create();
        var head = repo.Commit("base", ("a.txt", "a\n"));
        repo.Tag("v1", "Release 1");
        repo.Git("remote", "add", "origin", repo.Path);
        repo.Git("config", "branch.main.remote", "origin");
        repo.Git("config", "branch.main.merge", "refs/heads/main");

        var snapshot = await SnapshotAsync(repo);

        Assert.Equal("main", snapshot.HeadBranch);
        Assert.Equal(GitSha.Parse(head), snapshot.Head);
        Assert.Equal(new RefState(GitSha.Parse(head), "origin", "refs/heads/main"), snapshot.Refs["refs/heads/main"]);
        // The tag object, not the commit.
        Assert.Equal(GitSha.Parse(repo.Git("rev-parse", "v1").Trim()), snapshot.Refs["refs/tags/v1"].Target);
        Assert.NotEqual(GitSha.Parse(head), snapshot.Refs["refs/tags/v1"].Target);
    }

    [Fact]
    public async Task A_detached_or_unborn_head_is_read()
    {
        using var repo = TestRepoBuilder.Create();
        var empty = await SnapshotAsync(repo);
        Assert.Equal(("main", (GitSha?)null), (empty.HeadBranch, empty.Head));

        var head = repo.Commit("base", ("a.txt", "a\n"));
        repo.Git("switch", "--detach", "HEAD");
        var detached = await SnapshotAsync(repo);
        Assert.Equal((null, GitSha.Parse(head)), (detached.HeadBranch, detached.Head));
    }

    [Fact]
    public async Task Undoing_a_commit_softly_brings_its_changes_back_staged_and_redo_recommits()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a\n"));
        var before = await SnapshotAsync(repo);
        repo.Commit("second", ("a.txt", "b\n"));
        var after = await SnapshotAsync(repo);

        await RestoreAsync(repo, before, ResetMode.Soft);
        Assert.Equal(before.Head, GitSha.Parse(repo.Head()));
        Assert.Equal("M  a.txt", Status(repo));

        await RestoreAsync(repo, after, ResetMode.Soft);
        Assert.Equal(after.Head, GitSha.Parse(repo.Head()));
        Assert.Equal(string.Empty, Status(repo));
    }

    [Fact]
    public async Task Undoing_the_first_commit_leaves_an_unborn_branch_with_its_files_staged()
    {
        using var repo = TestRepoBuilder.Create();
        var before = await SnapshotAsync(repo);
        repo.Commit("base", ("a.txt", "a\n"));

        await RestoreAsync(repo, before, ResetMode.Soft);

        var now = await SnapshotAsync(repo);
        Assert.Equal(("main", (GitSha?)null), (now.HeadBranch, now.Head));
        Assert.Equal("A  a.txt", Status(repo));
    }

    [Fact]
    public async Task A_keep_restore_keeps_unrelated_local_changes_and_refuses_conflicting_ones()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a\n"), ("b.txt", "b\n"));
        var before = await SnapshotAsync(repo);
        repo.Commit("second", ("a.txt", "a2\n"));
        var after = await SnapshotAsync(repo);

        repo.WriteFile("a.txt", "local\n");
        var refused = Assert.IsType<Failure>((await _git.RestoreRefsAsync(repo.Path, after, before, ResetMode.Keep, Ct)).Value);
        Assert.Equal(FailureReason.LocalChangesWouldBeOverwritten, refused.Reason);
        Assert.Equal(after.Head, GitSha.Parse(repo.Head()));

        repo.Git("checkout", "--", "a.txt");
        repo.WriteFile("b.txt", "local\n");
        await RestoreAsync(repo, before);
        Assert.Equal(before.Head, GitSha.Parse(repo.Head()));
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Equal(" M b.txt", Status(repo));
    }

    [Fact]
    public async Task A_checkout_and_a_branch_creation_are_taken_back()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a\n"));
        var before = await SnapshotAsync(repo);
        repo.Branch("feature");
        repo.Commit("feature-1", ("b.txt", "b\n"));
        var after = await SnapshotAsync(repo);

        await RestoreAsync(repo, before);
        var now = await SnapshotAsync(repo);
        Assert.Equal("main", now.HeadBranch);
        Assert.False(now.Refs.ContainsKey("refs/heads/feature"));
        Assert.False(File.Exists(Path.Combine(repo.Path, "b.txt")));

        await RestoreAsync(repo, after);
        now = await SnapshotAsync(repo);
        Assert.Equal("feature", now.HeadBranch);
        Assert.Equal(after.Head, now.Head);
    }

    [Fact]
    public async Task A_deleted_branch_and_annotated_tag_come_back_with_upstream_and_message()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a\n"));
        repo.Branch("feature", checkout: false);
        repo.Tag("v1", "Release 1");
        repo.Git("remote", "add", "origin", repo.Path);
        repo.Git("config", "branch.feature.remote", "origin");
        repo.Git("config", "branch.feature.merge", "refs/heads/feature");
        var before = await SnapshotAsync(repo);
        AssertDone(await _git.DeleteBranchAsync(repo.Path, "feature", Ct));
        AssertDone(await _git.DeleteTagAsync(repo.Path, "v1", Ct));

        await RestoreAsync(repo, before);

        var now = await SnapshotAsync(repo);
        Assert.Equal(before.Refs["refs/heads/feature"], now.Refs["refs/heads/feature"]);
        Assert.Equal(before.Refs["refs/tags/v1"], now.Refs["refs/tags/v1"]);
        Assert.Equal("Release 1", repo.Git("tag", "-l", "--format=%(contents:subject)", "v1").Trim());
    }

    [Fact]
    public async Task A_renamed_branch_is_renamed_back()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a\n"));
        repo.Git("remote", "add", "origin", repo.Path);
        repo.Git("config", "branch.main.remote", "origin");
        repo.Git("config", "branch.main.merge", "refs/heads/main");
        var before = await SnapshotAsync(repo);
        AssertDone(await _git.RenameBranchAsync(repo.Path, "main", "trunk", Ct));

        await RestoreAsync(repo, before);

        var now = await SnapshotAsync(repo);
        Assert.Equal("main", now.HeadBranch);
        Assert.False(now.Refs.ContainsKey("refs/heads/trunk"));
        Assert.Equal(before.Refs["refs/heads/main"], now.Refs["refs/heads/main"]);
    }

    [Fact]
    public async Task A_branch_moved_meanwhile_is_not_overwritten()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "a\n"));
        repo.Branch("feature", checkout: false);
        var before = await SnapshotAsync(repo);
        repo.Checkout("feature");
        repo.Commit("feature-1", ("b.txt", "b\n"));
        repo.Checkout("main");
        var after = await SnapshotAsync(repo);
        repo.Checkout("feature");
        var moved = repo.Commit("feature-2", ("c.txt", "c\n"));
        repo.Checkout("main");

        // after (feature on feature-1) is no longer true: the transaction refuses to move feature.
        var refused = Assert.IsType<Failure>((await _git.RestoreRefsAsync(repo.Path, after, before, ResetMode.Keep, Ct)).Value);
        Assert.Contains("entre-temps", refused.Message, StringComparison.Ordinal);
        Assert.Equal(moved, repo.Git("rev-parse", "feature").Trim());
    }
}
