using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

/// <summary>"Résumé" page (J7.13): reading, rows, README, navigation back to the graph.</summary>
public sealed class RepositorySummaryViewModelTests
{
    private static readonly GitSha Sha1 = GitSha.Parse(new string('1', 40));
    private static readonly GitSha Sha2 = GitSha.Parse(new string('2', 40));
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeGit _git = new();
    private readonly FakeTimeProvider _time = new(Now);

    private RepositoryTabViewModel CreateTab()
    {
        var info = new RepositoryInfo("C:/repo", "C:/repo/.git", "repo", "main", null, false, false, false);
        return new RepositoryTabViewModel(info, _git, _time, NullLogger.Instance);
    }

    [Fact]
    public async Task The_page_takes_the_place_of_the_graph_and_of_the_right_panel()
    {
        using var tab = CreateTab();
        Assert.True(tab.ShowGraph);
        Assert.True(tab.ShowRightPanel);

        await tab.ShowSummaryAsync();

        Assert.False(tab.ShowGraph);
        Assert.False(tab.ShowRightPanel);

        tab.Summary.Close();

        Assert.True(tab.ShowGraph);
        Assert.True(tab.ShowRightPanel);
    }

    [Fact]
    public async Task Choosing_a_commit_brings_the_graph_back()
    {
        using var tab = CreateTab();
        await tab.ShowSummaryAsync();

        tab.Summary.SelectCommand.Execute(Sha1);

        Assert.False(tab.Summary.IsOpen);
        Assert.True(tab.ShowGraph);
    }

    [Fact]
    public async Task Another_main_area_screen_closes_the_page()
    {
        using var tab = CreateTab();
        await tab.ShowSummaryAsync();

        await tab.InspectAsync(new FileInspection(FileInspectionKind.History, "a.txt"));

        Assert.False(tab.Summary.IsOpen);
    }

    private RepositorySummaryViewModel Create()
    {
        var info = new RepositoryInfo("C:/repo", "C:/repo/.git", "repo", "main", null, false, false, false);
        var tree = new WorkingTreeViewModel(_git, info, null, NullLogger.Instance, null, null, null, null);
        return new RepositorySummaryViewModel(_git, "C:/repo", "repo", tree, null, _time, CancellationToken.None);
    }

    [Fact]
    public async Task Opening_reads_the_data_and_shows_the_page()
    {
        _git.Summary = new RepositorySummary(
            120, 4, 2,
            [new SummaryBranch("main", Sha1, Now.AddHours(-3), "Fix", "Ada", 2, 1)],
            [new SummaryTag("v1", Sha2, Now.AddDays(-2))],
            [new SummaryCommit(Sha1, "Fix", "Ada", Now.AddHours(-3))],
            null);
        _git.Remotes.Add(new GitRemote("origin", "https://example.com/repo.git"));
        var summary = Create();

        await summary.OpenAsync();

        Assert.True(summary.IsOpen);
        Assert.False(summary.IsLoading);
        Assert.Equal(1, _git.SummaryCalls);
        Assert.Equal("https://example.com/repo.git", summary.RemoteUrl);
        Assert.Equal(["main"], summary.Branches.Select(b => b.Name));
        Assert.Equal("↑2 ↓1", summary.Branches[0].TrackText);
        Assert.Equal([Sha2], summary.Tags.Select(t => t.Target));
        Assert.Equal("Fix", Assert.Single(summary.Commits).Subject);
        Assert.True(summary.HasBranchRows && summary.HasTagRows && summary.HasCommitRows);
        Assert.Null(summary.Readme);
        Assert.True(summary.NoReadme);
    }

    [Fact]
    public async Task The_origin_remote_is_preferred_to_the_first_one()
    {
        _git.Remotes.Add(new GitRemote("backup", "https://example.com/backup.git"));
        _git.Remotes.Add(new GitRemote("origin", "https://example.com/origin.git"));
        var summary = Create();

        await summary.OpenAsync();

        Assert.Equal("https://example.com/origin.git", summary.RemoteUrl);
    }

    [Fact]
    public async Task The_readme_is_parsed_as_markdown()
    {
        _git.Summary = new RepositorySummary(1, 1, 0, [], [], [], new SummaryReadme("README.md", "# Hello\n\nWorld", false));
        var summary = Create();

        await summary.OpenAsync();

        Assert.True(summary.HasReadme);
        Assert.False(summary.NoReadme);
        Assert.Equal("README.md", summary.ReadmeName);
        Assert.Equal(2, summary.Readme!.Count);
    }

    [Fact]
    public async Task A_failed_read_shows_its_message_and_keeps_the_page_open()
    {
        _git.SummaryFailure = "boom";
        var summary = Create();

        await summary.OpenAsync();

        Assert.True(summary.IsOpen);
        Assert.Equal("boom", summary.Message);
        Assert.True(summary.HasMessage);
    }

    [Fact]
    public async Task A_new_read_replaces_the_rows_and_clears_the_message()
    {
        _git.SummaryFailure = "boom";
        var summary = Create();
        await summary.OpenAsync();
        _git.SummaryFailure = null;
        _git.Summary = new RepositorySummary(1, 1, 0, [], [], [new SummaryCommit(Sha1, "One", "Ada", Now)], null);

        await summary.RefreshCommand.ExecuteAsync(null);

        Assert.Null(summary.Message);
        Assert.Equal("One", Assert.Single(summary.Commits).Subject);
    }

    [Fact]
    public async Task Choosing_a_row_asks_for_that_commit()
    {
        var summary = Create();
        await summary.OpenAsync();
        GitSha? requested = null;
        summary.ShaRequested += (_, sha) => requested = sha;

        summary.SelectCommand.Execute(Sha2);

        Assert.Equal(Sha2, requested);
    }

    [Fact]
    public async Task Closing_hides_the_page()
    {
        var summary = Create();
        await summary.OpenAsync();

        summary.Close();

        Assert.False(summary.IsOpen);
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData(0, 0, "")]
    [InlineData(3, 0, "↑3")]
    [InlineData(0, 4, "↓4")]
    [InlineData(3, 4, "↑3 ↓4")]
    public void Tracking_is_shown_only_when_the_branch_is_not_level(int? ahead, int? behind, string expected) =>
        Assert.Equal(expected, RepositorySummaryViewModel.TrackText(new SummaryBranch("b", Sha1, Now, "s", "a", ahead, behind)));
}
