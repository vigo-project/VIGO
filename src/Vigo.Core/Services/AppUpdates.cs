using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Vigo.Core.Services;

/// <summary>Version of the running VIGO, from the assembly (<c>Directory.Build.props</c>, "0.1.0-beta.1").</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // "0.1.0-beta.1+<commit>": the build metadata is noise for the user.
        var version = informational?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" : version;
    }
}

/// <summary>A newer version found by <see cref="IAppUpdater.CheckAsync"/>; <paramref name="Token"/> belongs to the updater.</summary>
public sealed record AppUpdate(string Version, object? Token = null);

/// <summary>Installer-side update mechanism (Velopack in the app, a fake in tests).</summary>
public interface IAppUpdater
{
    /// <summary>VIGO runs from an installation that can update itself (false for a build run from a folder).</summary>
    bool IsInstalled { get; }

    /// <summary>Newer version, null when up to date.</summary>
    Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken);

    Task DownloadAsync(AppUpdate update, CancellationToken cancellationToken);

    /// <summary>Closes VIGO, installs the downloaded version and starts it again.</summary>
    void ApplyAndRestart(AppUpdate update);
}

/// <summary>What an update check or download reports to the user.</summary>
public sealed record UpdateNotice(NotificationSeverity Severity, string Title, string? Message = null);

/// <summary>
/// Automatic updates (beta deployment): checks shortly after launch and every hour, downloads the new version in
/// the background, then waits for the user to restart (command palette) otherwise the next launch installs it (Program).
/// </summary>
public sealed partial class AppUpdates(IAppUpdater updater, TimeProvider time, ILogger<AppUpdates> logger) : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly CancellationTokenSource _disposed = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private AppUpdate? _ready;

    public void Dispose()
    {
        // Cancelled, not disposed: a check still running reads the token and releases the semaphore afterwards.
        _disposed.Cancel();
    }

    /// <summary>Raised when something is worth telling the user (a version is ready, a manual check ended).</summary>
    public event EventHandler<UpdateNotice>? Notice;

    /// <summary>Raised when <see cref="ReadyVersion"/> or <see cref="IsChecking"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>A check (and the download it starts) is running.</summary>
    public bool IsChecking { get; private set; }

    public static string CurrentVersion => AppVersion.Current;

    public bool CanUpdate => updater.IsInstalled;

    /// <summary>Version downloaded and waiting for a restart; null when none.</summary>
    public string? ReadyVersion => _ready?.Version;

    /// <summary>Starts the periodic checks (fire and forget; stops with <see cref="Dispose"/>).</summary>
    public void StartBackgroundChecks()
    {
        if (!CanUpdate) return;
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await Task.Delay(FirstCheckDelay, time, _disposed.Token);
                while (!_disposed.IsCancellationRequested)
                {
                    await CheckAsync(manual: false);
                    await Task.Delay(CheckInterval, time, _disposed.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Closing.
            }
        }
    }

    /// <summary>Checks now ("Check for updates"): always ends with a notice.</summary>
    public Task CheckNowAsync() => CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        if (!CanUpdate)
        {
            if (manual) Notice?.Invoke(this, new(NotificationSeverity.Info, Loc.Get("Updates.Unavailable.Title"), Loc.Get("Updates.Unavailable.Message")));
            return;
        }
        if (_ready is { } waiting)
        {
            if (manual) Notice?.Invoke(this, new(NotificationSeverity.Info, Loc.Format("Updates.Ready.Title", waiting.Version), Loc.Get("Updates.Ready.Waiting")));
            return;
        }
        if (!await _busy.WaitAsync(0, _disposed.Token)) return;
        IsChecking = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var update = await updater.CheckAsync(_disposed.Token);
            if (update is null)
            {
                if (manual) Notice?.Invoke(this, new(NotificationSeverity.Success, Loc.Get("Updates.UpToDate.Title"), Loc.Format("Updates.UpToDate.Message", CurrentVersion)));
                return;
            }

            LogDownloading(logger, CurrentVersion, update.Version);
            if (manual) Notice?.Invoke(this, new(NotificationSeverity.Info, Loc.Format("Updates.Downloading", update.Version)));
            await updater.DownloadAsync(update, _disposed.Token);
            _ready = update;
            Changed?.Invoke(this, EventArgs.Empty);
            Notice?.Invoke(this, new(NotificationSeverity.Success, Loc.Format("Updates.Ready.Title", update.Version),
                Loc.Get("Updates.Ready.Message")));
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            // Closing.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LogFailed(logger, e);
            if (manual) Notice?.Invoke(this, new(NotificationSeverity.Warning, Loc.Get("Updates.CheckFailed"), e.Message));
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke(this, EventArgs.Empty);
            _busy.Release();
        }
    }

    /// <summary>Installs the downloaded version and restarts VIGO.</summary>
    public void RestartToUpdate()
    {
        if (_ready is { } update) updater.ApplyAndRestart(update);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update found ({Current} -> {Next}): downloading")]
    private static partial void LogDownloading(ILogger logger, string current, string next);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update check or download failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
