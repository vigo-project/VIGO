using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

/// <summary>Lazy reading of the graph history (J7.28) against a synthetic linear history streamed by pages.</summary>
public sealed class GraphLazyLoadTests
{
    private const int Total = 1300;
    private const int Page = 500;
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Commit i (0 = newest) has the parent i + 1.
    private static GitSha Sha(int i) => GitSha.Parse(i.ToString("x40", System.Globalization.CultureInfo.InvariantCulture));

    private static FakeGit History(List<int>? startedPages = null)
    {
        var git = new FakeGit();
        git.ReadLog = async (options, onPage, ct) =>
        {
            for (var start = 0; start < Total; start += options.PageSize)
            {
                ct.ThrowIfCancellationRequested();
                startedPages?.Add(start);
                var page = Enumerable.Range(start, Math.Min(options.PageSize, Total - start))
                    .Select(i => new CommitInfo(Sha(i), i + 1 < Total ? [Sha(i + 1)] : [], "a", "a@x", Now, "c" + i))
                    .ToList();
                await onPage(page, ct);
            }
            return Done.Instance;
        };
        return git;
    }

    private static GraphViewModel Create(FakeGit git, bool lazy) => new(git, "repo", new FakeTimeProvider(Now), NullLogger.Instance,
        preferences: new Preferences(new InMemoryPreferencesStore(), new UserPreferences { GraphInitialCommits = Page, GraphLazyLoad = lazy }));

    private static async Task WaitForRowsAsync(GraphViewModel sut, int rows)
    {
        for (var i = 0; i < 400 && sut.RowCount < rows; i++) await Task.Delay(10, Ct);
        Assert.Equal(rows, sut.RowCount);
    }

    [Fact]
    public async Task Without_lazy_loading_the_whole_history_is_read()
    {
        using var sut = Create(History(), lazy: false);

        await sut.LoadAsync(Ct);

        Assert.Equal(Total, sut.RowCount);
        Assert.False(sut.IsPartial);
    }

    [Fact]
    public async Task Lazy_loading_stops_after_the_first_page_and_goes_on_when_the_view_nears_the_end()
    {
        var started = new List<int>();
        using var sut = Create(History(started), lazy: true);

        await sut.LoadAsync(Ct);
        await Task.Delay(100, Ct);

        Assert.Equal(Page, sut.RowCount);
        Assert.False(sut.IsLoading);
        Assert.True(sut.IsPartial);
        Assert.Equal([0], started);

        sut.EnsureLoaded(10); // far from the end: nothing asked
        await Task.Delay(100, Ct);
        Assert.Equal(Page, sut.RowCount);

        sut.EnsureLoaded(Page - 50);
        await WaitForRowsAsync(sut, 2 * Page);
        Assert.True(sut.IsPartial);

        sut.EnsureLoaded(2 * Page - 1);
        await WaitForRowsAsync(sut, Total);
        await Task.Delay(100, Ct);
        Assert.False(sut.IsPartial);
        Assert.Equal([0, 500, 1000], started);
    }

    [Fact]
    public async Task Selecting_a_commit_not_loaded_yet_reads_pages_until_it_shows_up()
    {
        using var sut = Create(History(), lazy: true);
        await sut.LoadAsync(Ct);

        Assert.True(await sut.TrySelectLoadingAsync(Sha(1100)).WaitAsync(TimeSpan.FromSeconds(10), Ct));

        Assert.Equal(Sha(1100), sut.SelectedSha);
        Assert.True(sut.RowCount > 1100);
        Assert.False(await sut.TrySelectLoadingAsync(GitSha.Parse(new string('f', 40))).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(sut.IsPartial);
    }

    [Fact]
    public async Task A_reload_reads_at_least_the_rows_already_shown()
    {
        using var sut = Create(History(), lazy: true);
        await sut.LoadAsync(Ct);
        sut.EnsureLoaded(Page - 1);
        await WaitForRowsAsync(sut, 2 * Page);

        await sut.ReloadAsync();
        await sut.WhenReloadedAsync();

        Assert.True(sut.RowCount >= 2 * Page);
        Assert.True(sut.IsPartial);
    }
}
