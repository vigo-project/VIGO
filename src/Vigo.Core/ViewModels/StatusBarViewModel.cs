using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Status bar along the bottom of a window (mockup): branch, sync with the upstream, loaded commits and last fetch,
/// network operation in progress of the selected repository tab; then the profile, density, theme and palette entries.
/// Follows <see cref="ShellViewModel.SelectedTab"/>. UI thread only.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private readonly TimeProvider _time;
    private readonly Preferences? _preferences;
    private readonly GitProfiles? _profiles;
    private RepositoryTabViewModel? _tab;

    private readonly AppUpdates? _updates;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public StatusBarViewModel(TimeProvider time, Preferences? preferences, GitProfiles? profiles = null, AppUpdates? updates = null)
    {
        _time = time;
        _preferences = preferences;
        _profiles = profiles;
        _updates = updates;
        if (_preferences is not null) _preferences.Changed += OnPreferencesChanged;
        if (_profiles is not null) _profiles.Changed += OnProfilesChanged;
        if (_updates is not null) _updates.Changed += OnUpdatesChanged;
    }

    // Raised from the update check (thread pool): the bar belongs to the UI thread.
    private void OnUpdatesChanged(object? sender, EventArgs e)
    {
        if (_context is null) NotifyUpdateChanged();
        else _context.Post(_ => NotifyUpdateChanged(), null);
    }

    private void NotifyUpdateChanged()
    {
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(UpdateTooltip));
    }

    /// <summary>A newer version is downloaded and waits for a restart: the green button is shown next to the version.</summary>
    public bool IsUpdateReady => _updates?.ReadyVersion is not null || PreviewUpdate;

    public string UpdateTooltip => (_updates?.ReadyVersion ?? (PreviewUpdate ? "9.9.9" : null)) is { } version
        ? Loc.Format("StatusBar.UpdateTooltip", version)
        : string.Empty;

    // Debug builds only: VIGO_PREVIEW_UPDATE=1 shows the button without a real update (the click does nothing).
#if DEBUG
    private static bool PreviewUpdate { get; } = Environment.GetEnvironmentVariable("VIGO_PREVIEW_UPDATE") == "1";
#else
    private const bool PreviewUpdate = false;
#endif

    /// <summary>Installs the downloaded version and starts VIGO again.</summary>
    [RelayCommand]
    private void RestartToUpdate() => _updates?.RestartToUpdate();

    private void OnProfilesChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ProfileText));

    /// <summary>The selected tab is a repository: the left part (branch, sync, info) is shown.</summary>
    public bool IsRepository => _tab is not null;

    public RepositoryTabViewModel? Repository => _tab;

    [ObservableProperty]
    public partial string BranchText { get; private set; } = string.Empty;

    /// <summary>"↑2 ↓5 · origin", "Pas de branche amont", "Aucun distant".</summary>
    [ObservableProperty]
    public partial string SyncText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncTooltip { get; private set; } = string.Empty;

    /// <summary>"26 commits affichés · fetch il y a 3 min".</summary>
    [ObservableProperty]
    public partial string InfoText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOperationRunning { get; private set; }

    /// <summary>"Fetch de tous les distants · 42 %".</summary>
    [ObservableProperty]
    public partial string OperationText { get; private set; } = string.Empty;

    /// <summary>"Profil : Perso", following the active profile ("Par défaut" without profiles).</summary>
    public static string VersionText => "VIGO " + Services.AppVersion.Current;

    public string ProfileText => Loc.Format("StatusBar.Profile", _profiles?.Active?.Label ?? Loc.Get("StatusBar.DefaultProfile"));

    public string DensityLabel => _preferences?.Current.Density == Density.Compact ? Loc.Get("StatusBar.Density.Compact") : Loc.Get("StatusBar.Density.Normal");

    public string ThemeLabel => _preferences?.Current.Theme switch
    {
        AppTheme.Light => Loc.Get("StatusBar.Theme.Light"),
        AppTheme.Dark => Loc.Get("StatusBar.Theme.Dark"),
        _ => Loc.Get("StatusBar.Theme.System"),
    };

    /// <summary>Shows the state of <paramref name="tab"/> (null: not a repository tab).</summary>
    public void Attach(TabViewModel? tab)
    {
        var repository = tab as RepositoryTabViewModel;
        if (ReferenceEquals(repository, _tab)) return;
        if (_tab is { } old)
        {
            old.PropertyChanged -= OnTabPropertyChanged;
            old.WorkingTree.StatusChanged -= OnChanged;
            old.Graph.PropertyChanged -= OnGraphPropertyChanged;
            old.Remote.PropertyChanged -= OnRemotePropertyChanged;
            old.RefActions.RemotesChanged -= OnChanged;
        }
        _tab = repository;
        if (repository is not null)
        {
            repository.PropertyChanged += OnTabPropertyChanged;
            repository.WorkingTree.StatusChanged += OnChanged;
            repository.Graph.PropertyChanged += OnGraphPropertyChanged;
            repository.Remote.PropertyChanged += OnRemotePropertyChanged;
            repository.RefActions.RemotesChanged += OnChanged;
        }
        OnPropertyChanged(nameof(IsRepository));
        OnPropertyChanged(nameof(Repository));
        Refresh();
    }

    /// <summary>Recomputes every text (also called periodically, for the "il y a 3 min" of the last fetch).</summary>
    public void Refresh()
    {
        if (_tab is not { } tab)
        {
            BranchText = SyncText = SyncTooltip = InfoText = OperationText = string.Empty;
            IsOperationRunning = false;
            return;
        }

        BranchText = tab.HeadDisplay;
        var status = tab.WorkingTree.Status;
        (SyncText, SyncTooltip) = status switch
        {
            { Upstream: { } upstream } => (
                string.Create(CultureInfo.CurrentCulture, $"↑{status.Ahead} ↓{status.Behind} · {RemoteOf(upstream)}"),
                Loc.Format("StatusBar.Sync.Tooltip", Commits(status.Ahead), Commits(status.Behind), upstream)),
            _ when tab.RefActions.Remotes.Count == 0 => (Loc.Get("StatusBar.Sync.NoRemoteText"), Loc.Get("StatusBar.Sync.NoRemoteTip")),
            _ => (Loc.Get("StatusBar.Sync.NoUpstreamText"), Loc.Get("StatusBar.Sync.NoUpstreamTip")),
        };

        var count = tab.Graph.RowCount - tab.Graph.CommitRowOffset;
        var commits = count switch
        {
            0 => Loc.Get("StatusBar.Info.Zero"),
            1 => Loc.Get("StatusBar.Info.One"),
            _ => Loc.Format("StatusBar.Info.Many", count),
        };
        InfoText = tab.Remote.LastFetchStarted is { } fetched
            ? Loc.Format("StatusBar.Info.WithFetch", commits, RelativeTime.Format(fetched, _time.GetUtcNow(), _time.LocalTimeZone))
            : commits;

        var remote = tab.Remote;
        IsOperationRunning = remote.IsRunning;
        OperationText = !remote.IsRunning ? string.Empty
            : remote.IsIndeterminate ? remote.OperationTitle
            : Loc.Format("StatusBar.Progress", remote.OperationTitle, remote.ProgressValue);
    }

    /// <summary>"origin" from "origin/main" (a remote name has no slash in practice).</summary>
    private static string RemoteOf(string upstream) =>
        upstream.IndexOf('/', StringComparison.Ordinal) is > 0 and var slash ? upstream[..slash] : upstream;

    private static string Commits(int n) => n == 1 ? Loc.Get("Common.Commits.One") : Loc.Format("Common.Commits.Many", n);

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepositoryTabViewModel.HeadDisplay) or nameof(RepositoryTabViewModel.Info)) Refresh();
    }

    private void OnGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphViewModel.RowCount)) Refresh();
    }

    private void OnRemotePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteActionsViewModel.IsRunning) or nameof(RemoteActionsViewModel.OperationTitle)
            or nameof(RemoteActionsViewModel.ProgressValue) or nameof(RemoteActionsViewModel.IsIndeterminate))
            Refresh();
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(DensityLabel));
        OnPropertyChanged(nameof(ThemeLabel));
    }

    /// <summary>The window is closed: stops following its tab and the preferences.</summary>
    public void Detach()
    {
        Attach(null);
        if (_preferences is not null) _preferences.Changed -= OnPreferencesChanged;
        if (_profiles is not null) _profiles.Changed -= OnProfilesChanged;
        if (_updates is not null) _updates.Changed -= OnUpdatesChanged;
    }
}
