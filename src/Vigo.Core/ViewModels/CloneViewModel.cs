using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Shows the clone dialog (<see cref="CloneViewModel"/>) until it closes. Returns false when the user closed it
/// (Annuler, Échap), true when the view model asked it to close (<see cref="CloneViewModel.CloseRequested"/>).
/// </summary>
public interface ICloneDialog
{
    Task<bool> ShowAsync(CloneViewModel clone, CancellationToken cancellationToken = default);
}

/// <summary>
/// "Cloner un dépôt" dialog (mockup): URL, destination folder (the last parent used, plus the name taken from the URL),
/// recursive submodules, shallow clone; the progress is shown in the dialog with the network operations' wording.
/// A refused clone closes the dialog for the authentication dialog (one dialog at a time), then reopens it and starts
/// again with the user's choice applied.
/// </summary>
public sealed partial class CloneViewModel : ObservableObject, IDisposable
{
    /// <summary>Dialogs shown in a row for one clone before giving up.</summary>
    private const int MaxAuthenticationAttempts = 5;

    private readonly IGitService _git;
    private readonly IFolderPicker _folderPicker;
    private readonly IAuthenticationPrompt? _authentication;
    private readonly Preferences? _preferences;
    private readonly HostAccounts? _accounts;
    private readonly TimeProvider _time;
    private readonly Dictionary<HostAccount, HostedRepositoryList> _listed = [];
    private IReadOnlyList<HostedRepository> _all = [];
    private string _parent;
    private bool _destinationEdited;
    private bool _settingDestination;
    private CancellationTokenSource? _clone;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _filtering;
    private Task _running = Task.CompletedTask;
    private Task _loading = Task.CompletedTask;

    /// <summary>Pause after the last keystroke of the repository filter before the list is filtered.</summary>
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(200);

    public CloneViewModel(
        IGitService git, IFolderPicker folderPicker, IAuthenticationPrompt? authentication = null, Preferences? preferences = null,
        HostAccounts? accounts = null, TimeProvider? time = null)
    {
        _git = git;
        _folderPicker = folderPicker;
        _authentication = authentication;
        _preferences = preferences;
        _accounts = accounts;
        _time = time ?? TimeProvider.System;
        Sources.Add(CloneSource.FromUrl);
        if (accounts is not null)
        {
            foreach (var account in accounts.Accounts.Where(a => a.ProfileId == accounts.ActiveProfileId && accounts.SupportsRepositoryListing(a.Provider)))
                Sources.Add(new CloneSource(account));
        }
        HasHostAccounts = Sources.Count > 1;
        _selectedSource = Sources[0];

        _parent = preferences?.Current.LastCloneParent ?? DefaultParent;
        UseSsh = preferences?.Current.CloneUseSsh ?? false;
        SetDestination(_parent);
    }

    public void Dispose()
    {
        _load?.Cancel();
        _load?.Dispose();
        _filtering?.Cancel();
        _filtering?.Dispose();
    }

    /// <summary>"URL", then one entry per account of the active profile whose service lists its repositories (J6.13).</summary>
    public ObservableCollection<CloneSource> Sources { get; } = [];

    /// <summary>At least one account can supply repositories; otherwise the dialog only offers the URL and a link to the integrations.</summary>
    public bool HasHostAccounts { get; }

    public bool ShowConnectHost => !HasHostAccounts && _accounts is not null;

    private CloneSource _selectedSource;

    public CloneSource SelectedSource
    {
        get => _selectedSource;
        set
        {
            // The list control clears its selection while its items refresh: keep the current source then.
            if (value is null || !SetProperty(ref _selectedSource, value)) return;
            OnPropertyChanged(nameof(IsUrlSource));
            OnPropertyChanged(nameof(IsHostSource));
            OnPropertyChanged(nameof(CanClone));
            CloneCommand.NotifyCanExecuteChanged();
            RefreshRepositoriesCommand.NotifyCanExecuteChanged();
            if (value.Account is { } account) _loading = LoadRepositoriesAsync(account, force: false);
        }
    }

    public bool IsUrlSource => SelectedSource.Account is null;

    public bool IsHostSource => !IsUrlSource;

    /// <summary>Repositories of the selected account, filtered by <see cref="RepositoryFilter"/>.</summary>
    public ObservableCollection<CloneRepositoryItem> Repositories { get; } = [];

    /// <summary>
    /// What the list draws: the repositories of <see cref="Repositories"/> under one header row per project (the part of the name before
    /// the repository's own: "organization/project"). A header is not selectable.
    /// </summary>
    public ObservableCollection<CloneRepositoryItem> Rows { get; } = [];

    /// <summary>Completes when the repositories asked by the last source selection are in (tests wait on it).</summary>
    public Task WhenRepositoriesLoaded => _loading;

    [ObservableProperty]
    public partial string RepositoryFilter { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial CloneRepositoryItem? SelectedRepository { get; set; }

    /// <summary>Picking a repository fills the SSH address instead of the HTTPS one (kept for the next clone).</summary>
    [ObservableProperty]
    public partial bool UseSsh { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingRepositories { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepositoriesError))]
    public partial string RepositoriesError { get; private set; } = string.Empty;

    public bool HasRepositoriesError => RepositoriesError.Length > 0;

    /// <summary>The service has more repositories than the pages read: the list says to filter or to use the URL.</summary>
    [ObservableProperty]
    public partial bool IsRepositoryListTruncated { get; private set; }

    /// <summary>The list holds no repository (nothing matches, or the account has none) and is not loading.</summary>
    [ObservableProperty]
    public partial bool IsRepositoryListEmpty { get; private set; }

    /// <summary>The user chose "Connecter un hébergeur…": the dialog closed so that the caller opens Preferences › Intégrations.</summary>
    public bool OpenIntegrationsRequested { get; private set; }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ConnectHost()
    {
        OpenIntegrationsRequested = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(IsHostSource))]
    private Task RefreshRepositoriesAsync()
    {
        if (SelectedSource.Account is { } account) _loading = LoadRepositoriesAsync(account, force: true);
        return _loading;
    }

    private async Task LoadRepositoriesAsync(HostAccount account, bool force)
    {
        _load?.Cancel();
        if (_accounts is null) return;
        var cts = _load = new CancellationTokenSource();
        RepositoriesError = string.Empty;
        if (!force && _listed.TryGetValue(account, out var known))
        {
            IsLoadingRepositories = false;
            Show(known);
            return;
        }

        IsLoadingRepositories = true;
        _all = [];
        ApplyFilter();
        GitResult<HostedRepositoryList> result;
        try
        {
            result = await _accounts.ListRepositoriesAsync(account, cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_load, cts)) IsLoadingRepositories = false;
        }
        // A newer request (another source, "Actualiser") owns the list now.
        if (!ReferenceEquals(_load, cts)) return;

        switch (result)
        {
            case Success<HostedRepositoryList> { Value: var list }:
                _listed[account] = list;
                Show(list);
                break;
            case AuthenticationFailed a:
                RepositoriesError = a.Message;
                break;
            case Failure f:
                RepositoriesError = f.Message;
                break;
            case Conflict c:
                RepositoriesError = Loc.Format("Clone.Failed", c.Operation);
                break;
            case Canceled:
                break;
        }
    }

    private void Show(HostedRepositoryList list)
    {
        _all = list.Items;
        IsRepositoryListTruncated = list.IsTruncated;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = RepositoryFilter.Trim();
        var now = _time.GetUtcNow();
        var selected = SelectedRepository?.Repository.FullName;
        Repositories.Clear();
        foreach (var repository in _all)
        {
            if (filter.Length > 0
                && !repository.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && repository.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) != true) continue;
            Repositories.Add(new CloneRepositoryItem(repository, repository.UpdatedAt is { } at ? RelativeTime.Format(at, now, _time.LocalTimeZone) : string.Empty));
        }
        Rows.Clear();
        foreach (var group in Repositories.GroupBy(r => r.ParentName))
        {
            if (group.Key.Length > 0) Rows.Add(CloneRepositoryItem.Header(group.Key, group.Count()));
            foreach (var item in group) Rows.Add(item);
        }
        IsRepositoryListEmpty = Repositories.Count == 0 && !IsLoadingRepositories && !HasRepositoriesError;
        SelectedRepository = selected is null ? null : Repositories.FirstOrDefault(r => r.Repository.FullName == selected);
    }

    partial void OnRepositoryFilterChanged(string value) => _ = FilterAfterPauseAsync();

    private async Task FilterAfterPauseAsync()
    {
        _filtering?.Cancel();
        var cts = _filtering = new CancellationTokenSource();
        try
        {
            await Task.Delay(FilterDelay, _time, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        ApplyFilter();
    }

    partial void OnSelectedRepositoryChanged(CloneRepositoryItem? value)
    {
        // A header row cannot be picked (the arrow keys can land on it): the selection goes back to nothing.
        if (value is { IsHeader: true })
        {
            SelectedRepository = null;
            return;
        }
        FillUrl(value);
    }

    partial void OnUseSshChanged(bool value) => FillUrl(SelectedRepository);

    private void FillUrl(CloneRepositoryItem? item)
    {
        if (item is null || IsUrlSource) return;
        Url = UseSsh && item.Repository.SshUrl is { Length: > 0 } ssh ? ssh : item.Repository.HttpsUrl;
    }

    /// <summary>Proposed parent folder when no clone was made yet: <c>%USERPROFILE%\source\repos</c> (Visual Studio).</summary>
    public static string DefaultParent =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    public partial string Destination { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RecurseSubmodules { get; set; } = true;

    [ObservableProperty]
    public partial bool Shallow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone), nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand), nameof(BrowseCommand))]
    public partial bool IsCloning { get; private set; }

    /// <summary>"Réception des objets : 42 % (123 / 456) · 4.20 MiB/s".</summary>
    [ObservableProperty]
    public partial string ProgressDetail { get; private set; } = string.Empty;

    /// <summary>Overall completion, 0–100 (meaningful when <see cref="IsIndeterminate"/> is false).</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; } = true;

    /// <summary>Why the last attempt failed, shown under the fields; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; private set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    public bool CanEdit => !IsCloning;

    public bool CanClone =>
        !IsCloning && Url.Trim().Length > 0 && IsFullPath(Destination.Trim()) && (IsUrlSource || SelectedRepository is not null);

    /// <summary>The dialog starts cloning as soon as it opens (reopened after the authentication dialog).</summary>
    public bool StartAutomatically { get; private set; }

    /// <summary>Repository cloned by the last attempt.</summary>
    public RepositoryInfo? ClonedRepository { get; private set; }

    /// <summary>The view model asks its dialog to close (cloned, or authentication needed).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Shows the dialog until the repository is cloned (returned) or the user gives up (null). Between two showings,
    /// the authentication dialog resolves a refusal.
    /// </summary>
    public async Task<RepositoryInfo?> RunAsync(ICloneDialog dialog, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            ClonedRepository = null;
            _pendingAuthentication = null;
            var closedByViewModel = await dialog.ShowAsync(this, cancellationToken);
            StartAutomatically = false;
            if (!closedByViewModel || cancellationToken.IsCancellationRequested)
            {
                _load?.Cancel();
                // Closed while cloning: stop git and let the service remove what it wrote.
                _clone?.Cancel();
                await _running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                return null;
            }

            if (ClonedRepository is { } cloned)
            {
                if (_preferences is not null)
                {
                    var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Destination.Trim()));
                    var useSsh = UseSsh;
                    await _preferences.UpdateAsync(
                        p => p with { LastCloneParent = parent is { Length: > 0 } ? parent : p.LastCloneParent, CloneUseSsh = useSsh }, CancellationToken.None);
                }
                return cloned;
            }

            if (_pendingAuthentication is not { } refused) return null;
            if (_authentication is null || attempt >= MaxAuthenticationAttempts)
            {
                ErrorMessage = refused.Message;
                continue;
            }
            StartAutomatically = await ResolveAuthenticationAsync(refused, cancellationToken);
        }
    }

    private AuthenticationFailed? _pendingAuthentication;

    /// <summary>Chooses another parent folder; the destination becomes that folder plus the repository name again.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task BrowseAsync(CancellationToken cancellationToken)
    {
        if (await _folderPicker.PickFolderAsync(cancellationToken) is not { } folder) return;
        _parent = folder;
        _destinationEdited = false;
        UpdateDestination();
    }

    [RelayCommand(CanExecute = nameof(CanClone))]
    private Task CloneAsync()
    {
        if (!CanClone) return Task.CompletedTask;
        _running = CloneCoreAsync();
        return _running;
    }

    private async Task CloneCoreAsync()
    {
        using var cts = new CancellationTokenSource();
        _clone = cts;
        ErrorMessage = string.Empty;
        ProgressDetail = Loc.Get("Remote.Progress.Connecting");
        ProgressValue = 0;
        IsIndeterminate = true;
        IsCloning = true;
        GitResult<RepositoryInfo> result;
        try
        {
            var request = new CloneRequest(Url.Trim(), Destination.Trim(), RecurseSubmodules, Shallow);
            result = await _git.CloneAsync(request, new ContextProgress(SynchronizationContext.Current, OnProgress), cts.Token);
        }
        finally
        {
            _clone = null;
            IsCloning = false;
        }

        switch (result)
        {
            case Success<RepositoryInfo> s:
                ClonedRepository = s.Value;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case AuthenticationFailed a:
                _pendingAuthentication = a;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Failure f:
                ErrorMessage = f.Message;
                break;
            case Conflict c:
                ErrorMessage = Loc.Format("Clone.Failed", c.Operation);
                break;
            case Canceled:
                break;
        }
    }

    /// <summary>
    /// Shows the authentication dialog for a refused clone and applies the choice (store the token, use the other
    /// protocol's URL). True when the clone should start again.
    /// </summary>
    private async Task<bool> ResolveAuthenticationAsync(AuthenticationFailed refused, CancellationToken cancellationToken)
    {
        var requested = RemoteUrl.TryParse(Url.Trim());
        var failed = RemoteUrl.TryParse(refused.Url) is { Protocol: not RemoteProtocol.Local } parsed ? parsed : null;
        // git's URL may lack the path (SSH: "git@github.com"): prefer the requested one then.
        var url = requested is not null && (failed is null || failed.DisplayPath.Length == 0) ? requested : failed ?? requested;
        var helper = (await _git.GetConfigValueAsync(null, "credential.helper", cancellationToken)).Value is Success<string?> h ? h.Value : null;
        var details = refused.Details ?? refused.Message;
        var problem = new AuthenticationProblem("git clone", "origin", url, AuthenticationProblem.CauseOf(details), refused.Message, details, helper);

        if (await _authentication!.ResolveAsync(problem, cancellationToken) is not { } chosen)
        {
            ErrorMessage = Loc.Format("Clone.AuthenticationCanceled", refused.Message);
            return false;
        }
        switch (chosen)
        {
            case RetryAuthentication:
                return true;
            case UseAccessToken token when url is not null:
                var stored = await _git.StoreCredentialAsync(null, url, token.UserName, token.Token, cancellationToken);
                if (stored.Value is Failure f)
                {
                    ErrorMessage = Loc.Format("Clone.TokenNotStored", f.Message);
                    return false;
                }
                return stored.Value is Done;
            case UseAccessToken:
                ErrorMessage = refused.Message;
                return false;
            case SwitchRemoteUrl switched:
                Url = switched.NewUrl;
                return true;
        }
        return false;
    }

    /// <summary>Repository name taken from a clone URL ("https://host/team/api.git" → "api"); empty when there is none.</summary>
    public static string RepositoryName(string url)
    {
        var text = url.Trim().TrimEnd('/', '\\');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        var start = text.LastIndexOfAny(['/', '\\', ':']);
        var name = text[(start + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        if (name.AsSpan().IndexOfAny(invalid) >= 0) return string.Empty;
        // "%20" → " " like git; an invalid escape ("100%") keeps the raw name, and characters a Windows folder cannot hold ("%3A") are dropped.
        var decoded = Uri.UnescapeDataString(name);
        return decoded == name ? name : string.Concat(decoded.Where(c => Array.IndexOf(invalid, c) < 0)).Trim();
    }

    partial void OnUrlChanged(string value)
    {
        UpdateDestination();
        ErrorMessage = string.Empty;
    }

    partial void OnDestinationChanged(string value)
    {
        if (!_settingDestination) _destinationEdited = true;
    }

    private void UpdateDestination()
    {
        if (_destinationEdited) return;
        var name = RepositoryName(Url);
        SetDestination(name.Length > 0 ? Path.Combine(_parent, name) : _parent);
    }

    private void SetDestination(string value)
    {
        _settingDestination = true;
        Destination = value;
        _settingDestination = false;
    }

    private static bool IsFullPath(string path)
    {
        try
        {
            return path.Length > 0 && Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void OnProgress(GitProgress progress)
    {
        if (!IsCloning) return;
        ProgressDetail = RemoteActionsViewModel.Describe(progress);
        // Clone: receiving 0–70 %, resolving deltas 70–85 %, writing the files 85–100 %.
        double? fraction = (progress.Phase, progress.Percent, progress.IsRemote) switch
        {
            (GitProgressPhase.Receiving or GitProgressPhase.Unpacking, { } p, false) => 0.70 * p / 100,
            (GitProgressPhase.Resolving, { } p, false) => 0.70 + 0.15 * p / 100,
            (GitProgressPhase.CheckingOut, { } p, false) => 0.85 + 0.15 * p / 100,
            _ => null,
        };
        if (fraction is { } f)
        {
            IsIndeterminate = false;
            ProgressValue = Math.Round(f * 100, 1);
        }
    }
}
