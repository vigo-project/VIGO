using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class BranchAndTagTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private static string CurrentBranch(TestRepoBuilder repo) => repo.Git("branch", "--show-current").Trim();

    private static string[] Branches(TestRepoBuilder repo) =>
        repo.Git("for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Creates_a_branch_on_a_commit_without_switching_or_tracking()
    {
        using var remote = TestRepoBuilder.Create();
        var first = remote.Commit("one", ("a.txt", "1\n"));
        remote.Commit("two", ("a.txt", "2\n"));
        using var repo = TestRepoBuilder.Clone(remote.Path);
        repo.Git("config", "branch.autoSetupMerge", "always");

        AssertDone(await _git.CreateBranchAsync(repo.Path, new CreateBranchRequest("feature/x", GitSha.Parse(first), Checkout: false), Ct));

        Assert.Equal("main", CurrentBranch(repo));
        Assert.Equal(first, repo.Git("rev-parse", "feature/x").Trim());
        Assert.Equal(string.Empty, repo.Git("for-each-ref", "--format=%(upstream)", "refs/heads/feature/x").Trim());
    }

    [Fact]
    public async Task Creates_and_switches_in_one_step_keeping_local_changes()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("one", ("a.txt", "1\n"), ("b.txt", "b\n"));
        repo.Commit("two", ("a.txt", "2\n"));
        repo.WriteFile("b.txt", "local\n");

        AssertDone(await _git.CreateBranchAsync(repo.Path, new CreateBranchRequest("fix", GitSha.Parse(first), Checkout: true), Ct));

        Assert.Equal("fix", CurrentBranch(repo));
        Assert.Equal(first, repo.Head());
        Assert.Equal("local\n", File.ReadAllText(Path.Combine(repo.Path, "b.txt")));
    }

    [Fact]
    public async Task A_refused_switch_does_not_leave_the_branch_behind()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("one", ("a.txt", "1\n"));
        repo.Commit("two", ("a.txt", "2\n"));
        repo.WriteFile("a.txt", "local\n");

        var result = await _git.CreateBranchAsync(repo.Path, new CreateBranchRequest("fix", GitSha.Parse(first), Checkout: true), Ct);

        Assert.StartsWith("Des modifications locales seraient écrasées", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fix", Branches(repo));
        Assert.Equal("main", CurrentBranch(repo));
    }

    [Fact]
    public async Task Existing_names_and_folder_clashes_are_explained()
    {
        using var repo = TestRepoBuilder.Create();
        var head = GitSha.Parse(repo.Commit("one", ("a.txt", "1\n")));
        repo.Branch("feature/x", checkout: false);

        var same = await _git.CreateBranchAsync(repo.Path, new CreateBranchRequest("feature/x", head, false), Ct);
        var folder = await _git.CreateBranchAsync(repo.Path, new CreateBranchRequest("feature", head, false), Ct);

        Assert.Equal("feature/x existe déjà.", Assert.IsType<Failure>(same.Value).Message);
        Assert.Contains("dossier", Assert.IsType<Failure>(folder.Value).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("a..b")]
    [InlineData("with space")]
    [InlineData("x.lock")]
    public async Task Invalid_names_are_refused_before_git(string name)
    {
        using var repo = TestRepoBuilder.Create();
        var head = GitSha.Parse(repo.Commit("one", ("a.txt", "1\n")));

        Assert.IsType<Failure>((await _git.CreateBranchAsync(repo.Path, new CreateBranchRequest(name, head, false), Ct)).Value);
        Assert.IsType<Failure>((await _git.CreateTagAsync(repo.Path, new CreateTagRequest(name, head, null), Ct)).Value);
        Assert.Equal(["main"], Branches(repo));
    }

    [Fact]
    public async Task Renames_a_branch_with_its_upstream()
    {
        using var remote = TestRepoBuilder.Create();
        remote.Commit("one", ("a.txt", "1\n"));
        using var repo = TestRepoBuilder.Clone(remote.Path);

        AssertDone(await _git.RenameBranchAsync(repo.Path, "main", "trunk", Ct));

        Assert.Equal("trunk", CurrentBranch(repo));
        Assert.Equal("origin/main", repo.Git("rev-parse", "--abbrev-ref", "trunk@{upstream}").Trim());
    }

    [Fact]
    public async Task Deletes_an_unmerged_branch_after_counting_its_exclusive_commits()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("one", ("a.txt", "1\n"));
        repo.Branch("feature");
        repo.Commit("f1", ("a.txt", "f1\n"));
        repo.Commit("f2", ("a.txt", "f2\n"));
        repo.Checkout("main");
        repo.Branch("merged", checkout: false);

        Assert.Equal(2, Assert.IsType<Success<int>>((await _git.CountExclusiveCommitsAsync(repo.Path, ["refs/heads/feature"], Ct)).Value).Value);
        Assert.Equal(0, Assert.IsType<Success<int>>((await _git.CountExclusiveCommitsAsync(repo.Path, ["refs/heads/merged"], Ct)).Value).Value);

        AssertDone(await _git.DeleteBranchAsync(repo.Path, "feature", Ct));
        Assert.Equal(["main", "merged"], Branches(repo));
    }

    [Fact]
    public async Task Commits_kept_by_a_tag_or_the_upstream_are_not_exclusive()
    {
        using var remote = TestRepoBuilder.Create();
        remote.Commit("one", ("a.txt", "1\n"));
        remote.Branch("feature");
        remote.Commit("f1", ("a.txt", "f1\n"));
        remote.Checkout("main");
        using var repo = TestRepoBuilder.Clone(remote.Path);
        repo.Git("switch", "--quiet", "feature");
        repo.Commit("local", ("a.txt", "local\n"));
        repo.Checkout("main");

        // origin/feature keeps f1; only "local" is exclusive. Deleting origin/feature too loses both.
        Assert.Equal(1, Count(await _git.CountExclusiveCommitsAsync(repo.Path, ["refs/heads/feature"], Ct)));
        Assert.Equal(2, Count(await _git.CountExclusiveCommitsAsync(repo.Path, ["refs/heads/feature", "refs/remotes/origin/feature"], Ct)));

        repo.Tag("keep", target: "feature");
        Assert.Equal(0, Count(await _git.CountExclusiveCommitsAsync(repo.Path, ["refs/heads/feature"], Ct)));

        static int Count(GitResult<int> result) => Assert.IsType<Success<int>>(result.Value).Value;
    }

    [Fact]
    public async Task The_checked_out_branch_cannot_be_deleted()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("one", ("a.txt", "1\n"));

        var result = await _git.DeleteBranchAsync(repo.Path, "main", Ct);

        Assert.Contains("extraite", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
        Assert.Equal(["main"], Branches(repo));
    }

    [Fact]
    public async Task A_lightweight_tag_is_created_even_when_tag_gpgsign_is_on()
    {
        // A signing profile (or the user's config) sets tag.gpgSign: without --no-sign git asks for a message ("no tag message?").
        using var repo = TestRepoBuilder.Create();
        var head = repo.Commit("init", ("a.txt", "a\n"));
        repo.Git("config", "tag.gpgSign", "true");

        AssertDone(await _git.CreateTagAsync(repo.Path, new CreateTagRequest("v1", GitSha.Parse(head), null), Ct));

        Assert.Equal("commit", repo.Git("cat-file", "-t", "v1").Trim());
    }

    [Fact]
    public async Task Creates_lightweight_and_annotated_tags_and_deletes_them()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("one", ("a.txt", "1\n"));
        repo.Commit("two", ("a.txt", "2\n"));

        AssertDone(await _git.CreateTagAsync(repo.Path, new CreateTagRequest("v1", GitSha.Parse(first), null), Ct));
        AssertDone(await _git.CreateTagAsync(repo.Path, new CreateTagRequest("v2", GitSha.Parse(repo.Head()), "Release 2\n\n- notes"), Ct));

        Assert.Equal("commit", repo.Git("cat-file", "-t", "v1").Trim());
        Assert.Equal("tag", repo.Git("cat-file", "-t", "v2").Trim());
        Assert.Equal(first, repo.Git("rev-parse", "v1^{commit}").Trim());
        Assert.Equal("Release 2\n\n- notes", repo.Git("tag", "-l", "--format=%(contents)", "v2").Trim());

        var again = await _git.CreateTagAsync(repo.Path, new CreateTagRequest("v1", GitSha.Parse(first), null), Ct);
        Assert.Equal("v1 existe déjà.", Assert.IsType<Failure>(again.Value).Message);

        AssertDone(await _git.DeleteTagAsync(repo.Path, "v1", Ct));
        Assert.Equal("v2", repo.Git("tag", "-l").Trim());
    }
}
