using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

public sealed class AppUpdatesTests
{
    private sealed class FakeUpdater : IAppUpdater
    {
        public bool IsInstalled { get; set; } = true;
        public AppUpdate? Available { get; set; }
        public Exception? Failure { get; set; }
        public int Downloads { get; private set; }
        public bool Restarted { get; private set; }

        public Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken) =>
            Failure is { } failure ? Task.FromException<AppUpdate?>(failure) : Task.FromResult(Available);

        public Task DownloadAsync(AppUpdate update, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(AppUpdate update) => Restarted = true;

    }

    private static (AppUpdates Updates, List<UpdateNotice> Notices) Create(FakeUpdater updater)
    {
        var updates = new AppUpdates(updater, TimeProvider.System, NullLogger<AppUpdates>.Instance);
        var notices = new List<UpdateNotice>();
        updates.Notice += (_, n) => notices.Add(n);
        return (updates, notices);
    }

    [Fact]
    public void Version_has_no_build_metadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
        Assert.DoesNotContain('+', AppVersion.Current);
    }

    [Fact]
    public async Task New_version_is_downloaded_then_ready()
    {
        var updater = new FakeUpdater { Available = new AppUpdate("0.2.0-beta.1") };
        var (updates, notices) = Create(updater);

        await updates.CheckNowAsync();

        Assert.Equal(1, updater.Downloads);
        Assert.Equal("0.2.0-beta.1", updates.ReadyVersion);
        Assert.Contains(notices, n => n.Title.Contains("0.2.0-beta.1", StringComparison.Ordinal) && n.Severity == NotificationSeverity.Success);
    }

    [Fact]
    public async Task Ready_version_is_applied_on_restart_only()
    {
        var updater = new FakeUpdater { Available = new AppUpdate("0.2.0") };
        var (updates, _) = Create(updater);
        await updates.CheckNowAsync();

        updates.RestartToUpdate();

        Assert.True(updater.Restarted);
    }

    [Fact]
    public async Task Status_bar_shows_the_restart_button_once_an_update_is_ready()
    {
        var updater = new FakeUpdater { Available = new AppUpdate("0.2.0") };
        var (updates, _) = Create(updater);
        var bar = new Vigo.Core.ViewModels.StatusBarViewModel(TimeProvider.System, preferences: null, updates: updates);
        Assert.False(bar.IsUpdateReady);

        await updates.CheckNowAsync();

        Assert.True(bar.IsUpdateReady);
        Assert.Contains("0.2.0", bar.UpdateTooltip, StringComparison.Ordinal);
        bar.RestartToUpdateCommand.Execute(null);
        Assert.True(updater.Restarted);
    }

    [Fact]
    public async Task Up_to_date_only_tells_the_user_on_a_manual_check()
    {
        var (updates, notices) = Create(new FakeUpdater());

        await updates.CheckNowAsync();

        Assert.Single(notices);
        Assert.Null(updates.ReadyVersion);
    }

    [Fact]
    public async Task Failed_check_is_reported_without_throwing()
    {
        var (updates, notices) = Create(new FakeUpdater { Failure = new HttpRequestException("offline") });

        await updates.CheckNowAsync();

        Assert.Equal(NotificationSeverity.Warning, Assert.Single(notices).Severity);
    }

    [Fact]
    public async Task Run_from_a_folder_cannot_update()
    {
        var updater = new FakeUpdater { IsInstalled = false, Available = new AppUpdate("9.9.9") };
        var (updates, notices) = Create(updater);

        await updates.CheckNowAsync();

        Assert.False(updates.CanUpdate);
        Assert.Equal(0, updater.Downloads);
        Assert.Single(notices);
    }
}
