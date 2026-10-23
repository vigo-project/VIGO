using Velopack;
using Velopack.Sources;
using Vigo.Core.Services;

namespace Vigo.App.Services;

/// <summary>
/// Updates from the GitHub releases of the public vigo-project/vigo repository with Velopack: stable releases, plus pre-releases when the
/// user asked for them (<c>UpdatePreReleases</c> preference).
/// <c>VIGO_UPDATE_URL</c> overrides the source (a folder or an HTTP URL, to test an update); <c>VIGO_UPDATE_TOKEN</c> is
/// an access token for a private repository.
/// </summary>
public sealed class VelopackUpdater(Func<bool>? includePreReleases = null) : IAppUpdater
{
    private const string RepositoryUrl = "https://github.com/vigo-project/vigo";

    // Rebuilt by a check when the pre-release preference changed since the manager was created.
    private UpdateManager _manager = new(CreateSource(includePreReleases?.Invoke() ?? false));
    private bool _preReleases = includePreReleases?.Invoke() ?? false;

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        var wanted = includePreReleases?.Invoke() ?? false;
        if (wanted != _preReleases)
        {
            _manager = new UpdateManager(CreateSource(wanted));
            _preReleases = wanted;
        }

        var info = await _manager.CheckForUpdatesAsync();
        return info is null ? null : new AppUpdate(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(AppUpdate update, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync((UpdateInfo)update.Token!, cancelToken: cancellationToken);

    public void ApplyAndRestart(AppUpdate update) => _manager.ApplyUpdatesAndRestart(((UpdateInfo)update.Token!).TargetFullRelease);

    /// <summary>
    /// A fully downloaded and verified update left by a previous run is installed now (Velopack's Update.exe shows its own progress window),
    /// and VIGO starts again with the same command line. Returns normally when there is nothing to install or the installation failed.
    /// </summary>
    public void ApplyPendingAtLaunch(string[] args)
    {
        if (!_manager.IsInstalled || _manager.UpdatePendingRestart is not { } pending) return;
        try
        {
            _manager.ApplyUpdatesAndRestart(pending, args);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // No logger yet and no retry loop: the normal start goes on, the update is offered again later.
            System.Diagnostics.Trace.TraceWarning("Applying the pending update failed: " + e.Message);
        }
    }

    private static IUpdateSource CreateSource(bool preReleases) =>
        Environment.GetEnvironmentVariable("VIGO_UPDATE_URL") is { Length: > 0 } custom
            ? new SimpleWebSource(custom)
            : new GithubSource(RepositoryUrl, Environment.GetEnvironmentVariable("VIGO_UPDATE_TOKEN"), preReleases);
}
