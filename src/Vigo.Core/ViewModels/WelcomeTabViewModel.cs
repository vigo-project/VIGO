using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>"Start" screen: open, clone, initialize, recent repositories, active profile.</summary>
public sealed partial class WelcomeTabViewModel : TabViewModel, IDisposable
{
    private readonly IGitService _git;
    private readonly IFolderPicker _folderPicker;
    private readonly RecentRepositories _recent;
    private readonly TimeProvider _time;
    private readonly Func<string, CancellationToken, Task> _openRepository;
    private readonly Func<CancellationToken, Task>? _clone;
    private readonly HostAccounts? _accounts;
    private readonly Action? _openIntegrations;
    private readonly GitProfiles? _profiles;
    private readonly Action? _openProfiles;
    private GitIdentity? _globalIdentity;

    public WelcomeTabViewModel(
        IGitService git,
        IFolderPicker folderPicker,
        RecentRepositories recent,
        TimeProvider time,
        Func<string, CancellationToken, Task> openRepository,
        Func<CancellationToken, Task>? clone = null,
        HostAccounts? accounts = null,
        Action? openIntegrations = null,
        GitProfiles? profiles = null,
        Action? openProfiles = null,
        Func<string, Task>? switchProfile = null)
    {
        _switchProfile = switchProfile;
        _accounts = accounts;
        _openIntegrations = openIntegrations;
        _profiles = profiles;
        _openProfiles = openProfiles;
        _git = git;
        _folderPicker = folderPicker;
        _recent = recent;
        _time = time;
        _openRepository = openRepository;
        _clone = clone;
        Title = Loc.Get("Shell.Tab.Welcome");
        _recent.Changed += OnRecentChanged;
        RefreshRecents();
        if (_accounts is not null) _accounts.Changed += OnAccountsChanged;
        if (_profiles is not null) _profiles.Changed += OnProfilesChanged;
        RefreshHosts();
    }

    public override string IconName => "home";

    [ObservableProperty]
    public partial GitIdentity? Identity { get; set; }

    [ObservableProperty]
    public partial string RecentFilter { get; set; } = string.Empty;

    /// <summary>Recent repositories matching <see cref="RecentFilter"/>, in display order.</summary>
    public ObservableCollection<RecentRepositoryItem> Recents { get; } = [];

    /// <summary>No repository opened yet (empty state of the list).</summary>
    [ObservableProperty]
    public partial bool HasNoRecents { get; private set; }

    /// <summary>Some recent repositories exist but none matches the filter.</summary>
    [ObservableProperty]
    public partial bool HasNoMatch { get; private set; }

    /// <summary>Loads secondary data (recent repositories, identity). Called after the first render.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var recent = _recent.LoadAsync(cancellationToken);
        var result = await _git.GetIdentityAsync(null, cancellationToken);
        _globalIdentity = result switch
        {
            Success<GitIdentity> s => s.Value,
            Conflict or AuthenticationFailed or Canceled or Failure => null,
        };
        ShowIdentity();
        await recent;
        // Relative dates move on while the tab stays open.
        RefreshRecents();
    }

    public void Dispose()
    {
        _recent.Changed -= OnRecentChanged;
        if (_accounts is not null) _accounts.Changed -= OnAccountsChanged;
        if (_profiles is not null) _profiles.Changed -= OnProfilesChanged;
    }

    /// <summary>Sign-in state of GitHub (J6.1): "Non connecté" or the account.</summary>
    [ObservableProperty]
    public partial string GitHubStatus { get; private set; } = Loc.Get("Welcome.Host.NotConnected");

    [ObservableProperty]
    public partial string GitHubActionText { get; private set; } = Loc.Get("Welcome.Host.Connect");

    [ObservableProperty]
    public partial string AzureDevOpsStatus { get; private set; } = Loc.Get("Welcome.Host.NotConnected");

    [ObservableProperty]
    public partial string AzureDevOpsActionText { get; private set; } = Loc.Get("Welcome.Host.Connect");

    /// <summary>Opens Préférences › Intégrations, where accounts are connected and disconnected.</summary>
    [RelayCommand]
    private void OpenIntegrations() => _openIntegrations?.Invoke();

    /// <summary>"Gérer les profils →": Préférences › Profils Git.</summary>
    [RelayCommand]
    private void OpenProfiles() => _openProfiles?.Invoke();

    private readonly Func<string, Task>? _switchProfile;

    /// <summary>"Changer de profil" is offered when there is another profile to switch to.</summary>
    public bool CanSwitchProfile => _switchProfile is not null && _profiles is { Profiles.Count: > 1 };

    /// <summary>Profiles to switch between, the active one flagged.</summary>
    public IReadOnlyList<(string Id, string Label, bool IsActive, string? Icon)> ProfileChoices =>
        _profiles is null ? [] : [.. _profiles.Profiles.Select(p => (p.Id, p.Label, p.Id == _profiles.Active?.Id, p.Icon))];

    public Task SwitchProfileAsync(string id) => _switchProfile?.Invoke(id) ?? Task.CompletedTask;

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        ShowIdentity();
        OnPropertyChanged(nameof(CanSwitchProfile));
    }

    // The active profile gives the identity shown; without profiles, the one of the Git configuration.
    private void ShowIdentity() =>
        Identity = _profiles?.Active is { } active
            ? new GitIdentity(string.IsNullOrWhiteSpace(active.Name) ? _globalIdentity?.Name ?? active.Label : active.Name,
                string.IsNullOrWhiteSpace(active.Email) ? _globalIdentity?.Email : active.Email)
            : _globalIdentity;

    private void OnAccountsChanged(object? sender, EventArgs e) => RefreshHosts();

    private void RefreshHosts()
    {
        var account = _accounts?.AccountOf(HostingProvider.GitHub);
        GitHubStatus = account is null ? Loc.Get("Welcome.Host.NotConnected") : Loc.Format("Welcome.Host.ConnectedAs", account.Title);
        GitHubActionText = Loc.Get(account is null ? "Welcome.Host.Connect" : "Welcome.Host.Manage");
        var azure = _accounts?.AccountOf(HostingProvider.AzureDevOps);
        AzureDevOpsStatus = azure is null ? Loc.Get("Welcome.Host.NotConnected") : Loc.Format("Welcome.Host.ConnectedAs", azure.Title);
        AzureDevOpsActionText = Loc.Get(azure is null ? "Welcome.Host.Connect" : "Welcome.Host.Manage");
    }

    [RelayCommand]
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        var path = await _folderPicker.PickFolderAsync(cancellationToken);
        if (path is not null)
            await _openRepository(path, cancellationToken);
    }

    /// <summary>"Cloner" tile: clone dialog, then the repository opens in place of this tab.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private Task CloneAsync(CancellationToken cancellationToken) => _clone?.Invoke(cancellationToken) ?? Task.CompletedTask;

    public bool CanClone => _clone is not null;

    [RelayCommand]
    private Task OpenRecentAsync(RecentRepositoryItem item, CancellationToken cancellationToken) =>
        _openRepository(item.Path, cancellationToken);

    [RelayCommand]
    private Task TogglePinAsync(RecentRepositoryItem item, CancellationToken cancellationToken) =>
        _recent.SetPinnedAsync(item.Path, !item.IsPinned, cancellationToken);

    [RelayCommand]
    private Task RemoveRecentAsync(RecentRepositoryItem item, CancellationToken cancellationToken) =>
        _recent.RemoveAsync(item.Path, cancellationToken);

    /// <summary>Initializes a repository in a chosen folder, then opens it. (.gitignore/license: J4.)</summary>
    [RelayCommand]
    private async Task InitAsync(CancellationToken cancellationToken)
    {
        var path = await _folderPicker.PickFolderAsync(cancellationToken);
        if (path is null) return;
        if (await _git.InitAsync(path, "main", cancellationToken) is { Value: Success<RepositoryInfo> })
            await _openRepository(path, cancellationToken);
    }

    partial void OnRecentFilterChanged(string value) => RefreshRecents();

    private void OnRecentChanged(object? sender, EventArgs e) => RefreshRecents();

    private void RefreshRecents()
    {
        var filter = RecentFilter.Trim();
        Recents.Clear();
        foreach (var repository in _recent.Items)
        {
            var item = new RecentRepositoryItem(repository, _time);
            if (item.Matches(filter))
                Recents.Add(item);
        }

        HasNoRecents = _recent.Items.Count == 0;
        HasNoMatch = !HasNoRecents && Recents.Count == 0;
    }
}
