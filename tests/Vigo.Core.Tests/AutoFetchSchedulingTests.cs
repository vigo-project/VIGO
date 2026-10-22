using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

/// <summary>Automatic fetch of the visible repository tab (<see cref="ShellViewModel.RunAutoFetchAsync"/>).</summary>
public sealed class AutoFetchSchedulingTests : IDisposable
{
    private readonly FakeGit _git = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly Preferences _preferences = new(new InMemoryPreferencesStore(), new UserPreferences());

    public AutoFetchSchedulingTests()
    {
        _git.Remotes.Add(new GitRemote("origin", "https://example.com/team/api.git"));
        foreach (var name in new[] { "atlas", "docs" })
        {
            _git.Repos[$@"C:\src\{name}"] = new Success<RepositoryInfo>(
                new RepositoryInfo($@"C:\src\{name}", $@"C:\src\{name}\.git", name, "main", new string('a', 40), false, false, false));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _preferences.Dispose();

    private ShellViewModel CreateSut() => new(_git, new NoPicker(), new NoNotifier(),
        new RecentRepositories(new InMemoryRecentRepositoryStore(), _time), _time, NullLogger<ShellViewModel>.Instance, preferences: _preferences);

    /// <summary>Starts the scheduler: its first check runs at once, then the canceled token stops the loop.</summary>
    private static async Task StartAsync(ShellViewModel sut)
    {
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        await sut.RunAutoFetchAsync(stopped.Token);
    }

    private static Task WhenFetchedAsync(ShellViewModel sut) =>
        ((RepositoryTabViewModel)sut.SelectedTab!).Remote.WhenAutoFetchedAsync();

    [Fact]
    public async Task Visible_tab_is_fetched_once_per_interval_after_startup()
    {
        var sut = CreateSut();
        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        Assert.Equal(0, _git.BackgroundFetches);

        await StartAsync(sut);
        await WhenFetchedAsync(sut);
        Assert.Equal(1, _git.BackgroundFetches);

        sut.AutoFetchIfDue();
        _time.Advance(TimeSpan.FromSeconds(30));
        sut.AutoFetchIfDue();
        Assert.Equal(1, _git.BackgroundFetches);

        _time.Advance(TimeSpan.FromSeconds(30));
        sut.AutoFetchIfDue();
        await WhenFetchedAsync(sut);
        Assert.Equal(2, _git.BackgroundFetches);
    }

    [Fact]
    public async Task Showing_a_tab_fetches_it_when_due_but_hidden_tabs_wait()
    {
        var sut = CreateSut();
        await StartAsync(sut);
        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        await WhenFetchedAsync(sut);
        var atlas = sut.SelectedTab;
        sut.NewTabCommand.Execute(null);
        await sut.OpenRepositoryAsync(@"C:\src\docs", Ct);
        await WhenFetchedAsync(sut);
        Assert.Equal(2, _git.BackgroundFetches);

        _time.Advance(TimeSpan.FromMinutes(5));
        sut.SelectedTab = atlas;
        await WhenFetchedAsync(sut);

        Assert.Equal(3, _git.BackgroundFetches);
    }

    [Fact]
    public async Task Interval_zero_disables_the_automatic_fetch()
    {
        await _preferences.UpdateAsync(p => p with { AutoFetchIntervalMinutes = 0 }, Ct);
        var sut = CreateSut();
        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);

        await StartAsync(sut);
        await WhenFetchedAsync(sut);

        Assert.Equal(0, _git.BackgroundFetches);
    }

    [Fact]
    public async Task Repository_without_remote_is_not_fetched()
    {
        _git.Remotes.Clear();
        var sut = CreateSut();
        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);

        await StartAsync(sut);
        await WhenFetchedAsync(sut);

        Assert.Equal(0, _git.BackgroundFetches);
    }

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class NoNotifier : IUserNotifier
    {
        public void Notify(NotificationSeverity severity, string title, string? message = null)
        {
        }
    }
}
