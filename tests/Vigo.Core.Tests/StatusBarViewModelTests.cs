using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class StatusBarViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryPreferencesStore _store = new();
    private readonly TestRepoBuilder _remote;
    private readonly TestRepoBuilder _mine;
    private readonly TestRepoBuilder _theirs;

    public StatusBarViewModelTests()
    {
        _remote = TestRepoBuilder.Create(bare: true);
        _mine = TestRepoBuilder.Create();
        _mine.Commit("init", ("a.txt", "1\n"));
        _mine.Git("remote", "add", "origin", _remote.Path);
        _mine.Git("push", "--quiet", "--set-upstream", "origin", "main");
        _theirs = TestRepoBuilder.Clone(_remote.Path);
    }

    public void Dispose()
    {
        _theirs.Dispose();
        _mine.Dispose();
        _remote.Dispose();
    }

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var tab = new RepositoryTabViewModel(
            new RepositoryInfo(_mine.Path, Path.Combine(_mine.Path, ".git"), "repo", "main", null, false, false, false),
            _git, _time, NullLogger.Instance);
        await tab.LoadAsync();
        return tab;
    }

    [Fact]
    public async Task Follows_the_branch_the_upstream_and_the_last_fetch_of_the_attached_tab()
    {
        using var tab = await OpenAsync();
        var bar = new StatusBarViewModel(_time, preferences: null);

        bar.Attach(tab);

        Assert.True(bar.IsRepository);
        Assert.Equal("main", bar.BranchText);
        Assert.Equal("↑0 ↓0 · origin", bar.SyncText);
        Assert.Equal("1 commit affiché", bar.InfoText);
        Assert.False(bar.IsOperationRunning);

        _theirs.Commit("theirs", ("b.txt", "b\n"));
        _theirs.Git("push", "--quiet");
        await tab.Remote.FetchCommand.ExecuteAsync(null);
        await tab.Graph.WhenReloadedAsync();

        Assert.Equal("↑0 ↓1 · origin", bar.SyncText);
        Assert.Equal("2 commits affichés · fetch à l'instant", bar.InfoText);

        _time.Advance(TimeSpan.FromMinutes(3));
        bar.Refresh();
        Assert.EndsWith("fetch il y a 3 min", bar.InfoText, StringComparison.Ordinal);

        bar.Attach(null);
        Assert.False(bar.IsRepository);
        Assert.Equal(string.Empty, bar.SyncText);
    }

    [Fact]
    public async Task Density_and_theme_labels_follow_the_preferences()
    {
        using var preferences = new Preferences(_store, new UserPreferences());
        var bar = new StatusBarViewModel(_time, preferences);
        var changed = new List<string?>();
        bar.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("Normal", bar.DensityLabel);
        Assert.Equal("Système", bar.ThemeLabel);

        await preferences.UpdateAsync(p => p with { Density = Density.Compact, Theme = AppTheme.Dark }, TestContext.Current.CancellationToken);

        Assert.Equal("Compact", bar.DensityLabel);
        Assert.Equal("Sombre", bar.ThemeLabel);
        Assert.Contains(nameof(StatusBarViewModel.DensityLabel), changed);
        Assert.Contains(nameof(StatusBarViewModel.ThemeLabel), changed);
        bar.Detach();
    }

    [Fact]
    public async Task The_last_fetch_uses_the_shared_relative_date_format()
    {
        using var tab = await OpenAsync();
        var bar = new StatusBarViewModel(_time, preferences: null);
        bar.Attach(tab);
        await tab.Remote.FetchCommand.ExecuteAsync(null);

        _time.Advance(TimeSpan.FromHours(2));
        bar.Refresh();
        Assert.EndsWith("fetch il y a 2 h", bar.InfoText, StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromDays(3));
        bar.Refresh();
        Assert.EndsWith("fetch il y a 3 j", bar.InfoText, StringComparison.Ordinal);
    }
}
