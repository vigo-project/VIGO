using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Data of the "Résumé" page (J7.13): totals, latest branches, tags and commits, README.</summary>
public sealed class SummaryTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<RepositorySummary> SummaryAsync() =>
        Assert.IsType<Success<RepositorySummary>>((await _git.GetSummaryAsync(_repo.Path, Ct)).Value).Value;

    [Fact]
    public async Task An_empty_repository_has_nothing_to_list()
    {
        var summary = await SummaryAsync();

        Assert.Equal(0, summary.CommitCount);
        Assert.Empty(summary.Commits);
        Assert.Empty(summary.Branches);
        Assert.Empty(summary.Tags);
        Assert.Null(summary.Readme);
    }

    [Fact]
    public async Task Totals_count_every_commit_branch_and_tag_but_lists_stop_at_five()
    {
        for (var i = 1; i <= 7; i++) _repo.Commit($"commit {i}", ($"f{i}.txt", $"{i}\n"));
        for (var i = 1; i <= 6; i++) _repo.Branch($"feature/{i}", checkout: false);
        for (var i = 1; i <= 7; i++) _repo.Tag($"v{i}");

        var summary = await SummaryAsync();

        Assert.Equal(7, summary.CommitCount);
        Assert.Equal(7, summary.BranchCount);
        Assert.Equal(7, summary.TagCount);
        Assert.Equal(5, summary.Branches.Count);
        Assert.Equal(5, summary.Tags.Count);
        Assert.Equal(5, summary.Commits.Count);
        Assert.Equal(["commit 7", "commit 6", "commit 5", "commit 4", "commit 3"], summary.Commits.Select(c => c.Subject));
    }

    [Fact]
    public async Task Branches_come_by_latest_commit_date_with_its_subject()
    {
        _repo.Commit("base", ("a.txt", "a\n"));
        _repo.Branch("old", checkout: false);
        _repo.Branch("recent");
        _repo.Commit("on recent", ("b.txt", "b\n"));

        var summary = await SummaryAsync();

        Assert.Equal("recent", summary.Branches[0].Name);
        Assert.Equal("on recent", summary.Branches[0].Subject);
        Assert.Null(summary.Branches[0].Ahead);
        Assert.Equal("old", summary.Branches[^1].Name);
    }

    [Fact]
    public async Task A_branch_knows_how_far_it_is_from_its_upstream()
    {
        _repo.Commit("base", ("a.txt", "a\n"));
        using var clone = TestRepoBuilder.Clone(_repo.Path, firstTick: 50_000);
        clone.Commit("local only", ("c.txt", "c\n"));
        _repo.Commit("remote only", ("d.txt", "d\n"));
        clone.Git("fetch", "origin");

        var result = await _git.GetSummaryAsync(clone.Path, Ct);

        var branch = Assert.Single(Assert.IsType<Success<RepositorySummary>>(result.Value).Value.Branches);
        Assert.Equal(1, branch.Ahead);
        Assert.Equal(1, branch.Behind);
    }

    [Fact]
    public async Task Annotated_tags_point_to_their_commit()
    {
        var first = _repo.Commit("first", ("a.txt", "a\n"));
        _repo.Tag("v1", "release one");
        _repo.Commit("second", ("b.txt", "b\n"));
        _repo.Tag("light");

        var summary = await SummaryAsync();

        var annotated = Assert.Single(summary.Tags, t => t.Name == "v1");
        Assert.Equal(GitSha.Parse(_repo.Git("rev-parse", "v1^{commit}").Trim()), annotated.Target);
        Assert.Equal(GitSha.Parse(_repo.Git("rev-parse", "light").Trim()), Assert.Single(summary.Tags, t => t.Name == "light").Target);
        Assert.NotNull(first);
    }

    [Fact]
    public async Task A_detached_head_still_lists_the_commits_of_head()
    {
        _repo.Commit("one", ("a.txt", "a\n"));
        _repo.Commit("two", ("b.txt", "b\n"));
        _repo.Git("checkout", "--detach", "HEAD~1");

        var summary = await SummaryAsync();

        Assert.Equal(1, summary.CommitCount);
        Assert.Equal("one", Assert.Single(summary.Commits).Subject);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("readme.md")]
    [InlineData("README")]
    public async Task The_readme_is_found_whatever_its_case(string name)
    {
        _repo.Commit("init", (name, "# Title\n\nHello\n"));

        var readme = (await SummaryAsync()).Readme;

        Assert.NotNull(readme);
        Assert.Equal(name, readme.FileName);
        Assert.StartsWith("# Title", readme.Text, StringComparison.Ordinal);
        Assert.False(readme.IsTruncated);
    }

    [Fact]
    public async Task Readme_md_wins_over_the_extensionless_readme()
    {
        _repo.Commit("init", ("README", "plain\n"), ("readme.md", "markdown\n"));

        Assert.Equal("readme.md", (await SummaryAsync()).Readme?.FileName);
    }

    [Fact]
    public async Task A_repository_without_readme_has_none()
    {
        _repo.Commit("init", ("a.txt", "a\n"));

        Assert.Null((await SummaryAsync()).Readme);
    }

    [Fact]
    public async Task A_large_readme_is_cut()
    {
        _repo.Commit("init", ("README.md", new string('x', RepositorySummary.MaxReadmeBytes + 100)));

        var readme = (await SummaryAsync()).Readme;

        Assert.NotNull(readme);
        Assert.True(readme.IsTruncated);
        Assert.Equal(RepositorySummary.MaxReadmeBytes, readme.Text.Length);
    }

    [Theory]
    [InlineData("", "", null, null)]
    [InlineData("refs/remotes/origin/main", "", 0, 0)]
    [InlineData("refs/remotes/origin/main", "ahead 2", 2, 0)]
    [InlineData("refs/remotes/origin/main", "behind 3", 0, 3)]
    [InlineData("refs/remotes/origin/main", "ahead 2, behind 1", 2, 1)]
    [InlineData("refs/remotes/origin/main", "gone", null, null)]
    public void Tracking_text_is_parsed(string upstream, string track, int? ahead, int? behind) =>
        Assert.Equal((ahead, behind), GitService.ParseTrack(upstream, track));
}
