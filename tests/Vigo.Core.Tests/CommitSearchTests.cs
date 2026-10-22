using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class CommitSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GraphViewModel CreateGraph(string path) => new(_git, path, new FakeTimeProvider(Now), NullLogger.Instance);

    private static CommitFilter Words(params string[] words) => new(words, [], null, []);

    [Fact]
    public async Task A_filter_narrows_the_graph_and_clearing_it_brings_the_history_back()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Fix crash", ("a.txt", "1"));
        repo.Commit("Add logo", ("a.txt", "2"));
        repo.Commit("Fix typo", ("a.txt", "3"));
        using var sut = CreateGraph(repo.Path);
        await sut.LoadAsync(Ct);
        Assert.Equal(3, sut.RowCount);

        await sut.SetFilterAsync(Words("fix"));
        await sut.WhenReloadedAsync();

        Assert.True(sut.IsFiltered);
        Assert.False(sut.IsSearching);
        Assert.Equal(2, sut.RowCount);
        Assert.Equal(2, sut.FilterResultCount);
        Assert.Equal(["Fix typo", "Fix crash"], [sut.GetCommit(0).Subject, sut.GetCommit(1).Subject]);

        await sut.SetFilterAsync(null);
        await sut.WhenReloadedAsync();

        Assert.False(sut.IsFiltered);
        Assert.Equal(3, sut.RowCount);
        Assert.Equal(0, sut.FilterResultCount);
    }

    [Fact]
    public async Task Parents_outside_the_result_are_dropped_so_no_lane_runs_to_the_bottom()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("keep one", ("a.txt", "1"));
        repo.Commit("other", ("a.txt", "2"));
        repo.Commit("keep two", ("a.txt", "3"));
        using var sut = CreateGraph(repo.Path);
        await sut.LoadAsync(Ct);

        await sut.SetFilterAsync(Words("keep"));
        await sut.WhenReloadedAsync();

        Assert.Equal(2, sut.RowCount);
        Assert.All(Enumerable.Range(0, sut.RowCount), row => Assert.Empty(sut.GetCommit(row).Parents));
    }

    [Fact]
    public async Task The_work_in_progress_row_is_hidden_while_filtering_and_comes_back()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Fix crash", ("a.txt", "1"));
        using var sut = CreateGraph(repo.Path);
        await sut.LoadAsync(Ct);
        sut.SetWorkInProgress(2);
        Assert.True(sut.HasWorkInProgress);
        Assert.Equal(2, sut.RowCount);

        await sut.SetFilterAsync(Words("fix"));
        await sut.WhenReloadedAsync();

        Assert.False(sut.HasWorkInProgress);
        Assert.Equal(1, sut.RowCount);
        Assert.Equal("Fix crash", sut.GetCommit(0).Subject);

        await sut.SetFilterAsync(null);
        await sut.WhenReloadedAsync();

        Assert.True(sut.HasWorkInProgress);
        Assert.Equal(2, sut.RowCount);
    }

    [Fact]
    public async Task No_match_leaves_an_empty_filtered_graph()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Fix crash", ("a.txt", "1"));
        using var sut = CreateGraph(repo.Path);
        await sut.LoadAsync(Ct);

        await sut.SetFilterAsync(Words("zzz"));
        await sut.WhenReloadedAsync();

        Assert.True(sut.IsFiltered);
        Assert.True(sut.IsEmpty);
        Assert.Equal(0, sut.RowCount);
    }

    [Fact]
    public async Task An_unknown_sha_shows_no_result_without_an_error()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Fix crash", ("a.txt", "1"));
        using var sut = CreateGraph(repo.Path);
        await sut.LoadAsync(Ct);

        await sut.SetFilterAsync(new CommitFilter([], [], "deadbeef", []));
        await sut.WhenReloadedAsync();

        Assert.True(sut.IsEmpty);
        Assert.Null(sut.FilterError);
    }

    [Fact]
    public async Task Refreshing_refs_while_filtered_keeps_the_search()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Fix crash", ("a.txt", "1"));
        repo.Commit("Add logo", ("a.txt", "2"));
        using var sut = CreateGraph(repo.Path);
        await sut.LoadAsync(Ct);
        await sut.SetFilterAsync(Words("fix"));
        await sut.WhenReloadedAsync();

        await sut.RefreshAsync();
        await sut.WhenReloadedAsync();

        Assert.Equal(1, sut.RowCount);
    }

    [Fact]
    public async Task Typing_waits_for_a_pause_then_filters_and_clearing_is_immediate()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Fix crash", ("a.txt", "1"));
        repo.Commit("Add logo", ("a.txt", "2"));
        using var graph = CreateGraph(repo.Path);
        await graph.LoadAsync(Ct);
        using var sut = new CommitSearchViewModel(graph, TimeProvider.System);

        sut.Text = "f";
        sut.Text = "fix";
        Assert.False(graph.IsFiltered);

        await WaitUntilAsync(() => graph.IsFiltered && !graph.IsSearching);
        Assert.Equal(1, graph.RowCount);
        Assert.Equal("1 commit", sut.Summary);
        Assert.True(sut.IsActive);

        sut.ClearCommand.Execute(null);
        await WaitUntilAsync(() => !graph.IsFiltered && !graph.IsSearching);
        Assert.Equal(2, graph.RowCount);
        Assert.False(sut.HasSummary);
    }

    [Fact]
    public async Task Summary_says_when_nothing_matches()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Fix crash", ("a.txt", "1"));
        using var graph = CreateGraph(repo.Path);
        await graph.LoadAsync(Ct);
        using var sut = new CommitSearchViewModel(graph, TimeProvider.System);

        sut.Text = "zzz";
        await WaitUntilAsync(() => graph.IsFiltered && !graph.IsSearching);

        Assert.Equal("Aucun résultat", sut.Summary);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the search");
            await Task.Delay(20, Ct);
        }
    }
}
