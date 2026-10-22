using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>One hosting service of the "Intégrations" section: connected account or sign-in actions.</summary>
public sealed partial class HostConnectionItem(HostingProvider provider, string name, string initials, string color, bool isSupported) : ObservableObject
{
    public HostingProvider Provider { get; } = provider;

    public string Name { get; } = name;

    public string Initials { get; } = initials;

    /// <summary>Brand color of the badge, "#AARRGGBB".</summary>
    public string Color { get; } = color;

    /// <summary>Sign-in is implemented for this service.</summary>
    public bool IsSupported { get; } = isSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsDisconnected), nameof(Status), nameof(AvatarUri), nameof(HasAvatar), nameof(CanSignIn), nameof(CanSignInWithBrowser))]
    public partial HostAccount? Account { get; set; }

    /// <summary>The OAuth device flow is available (an OAuth application is registered); the token entry always is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignInWithBrowser))]
    public partial bool CanUseBrowser { get; set; }

    public bool IsConnected => Account is not null;

    public bool IsDisconnected => Account is null && IsSupported;

    public bool CanSignIn => IsDisconnected;

    /// <summary>The browser button is shown only for services that offer the OAuth device flow (Azure DevOps: token only).</summary>
    public bool CanSignInWithBrowser => CanSignIn && CanUseBrowser;

    public Uri? AvatarUri => Account?.AvatarUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    public bool HasAvatar => AvatarUri is not null;

    public string Status => Account is { } account
        ? (account.Title == account.Login ? Loc.Format("Host.Status.Connected", account.Title) : Loc.Format("Host.Status.ConnectedAs", account.Title, account.Login))
        : Loc.Get(IsSupported ? "Host.Status.NotConnected" : "Host.Status.ComingSoon");

    public override string ToString() => Name;
}

/// <summary>"Intégrations" section (J6.1, ADR-044): sign in to hosting services with OAuth or a personal access token.</summary>
public sealed partial class PreferencesTabViewModel
{
    private readonly HostAccounts? _accounts;
    private CancellationTokenSource? _signIn;

    public bool IsIntegrations => SelectedSection.Section == PreferencesSection.Integrations;

    public ObservableCollection<HostConnectionItem> Hosts { get; } = [];

    /// <summary>Code to type on the service's page while the OAuth device flow waits; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingCode), nameof(IsBusy))]
    public partial DeviceCode? PendingCode { get; private set; }

    public bool HasPendingCode => PendingCode is not null;

    /// <summary>Service the pending code or the token entry is for.</summary>
    [ObservableProperty]
    public partial HostConnectionItem? SigningInTo { get; private set; }

    /// <summary>Service whose personal access token is being entered; null when the entry is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnteringToken), nameof(IsBusy), nameof(IsTokenTargetAzureDevOps), nameof(IsTokenTargetOther))]
    public partial HostConnectionItem? TokenTarget { get; private set; }

    public bool IsEnteringToken => TokenTarget is not null;

    public bool IsTokenTargetAzureDevOps => TokenTarget?.Provider == HostingProvider.AzureDevOps;

    /// <summary>Token entry for a service other than Azure DevOps (GitHub for now): the generic instructions.</summary>
    public bool IsTokenTargetOther => TokenTarget is not null && !IsTokenTargetAzureDevOps;

    /// <summary>The token being typed; cleared as soon as it was used or the entry closed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectWithTokenCommand))]
    public partial string TokenInput { get; set; } = string.Empty;

    /// <summary>Name or URL of the Azure DevOps organization the token is checked against and created for; optional.</summary>
    [ObservableProperty]
    public partial string OrganizationInput { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokenError))]
    public partial string? TokenError { get; private set; }

    public bool HasTokenError => TokenError is not null;

    /// <summary>A service can be signed in to with a token only: no OAuth application is registered for the browser flow.</summary>
    public bool BrowserSignInUnavailable => Hosts.Any(h => h.IsSupported && !h.CanUseBrowser && h.Provider != HostingProvider.AzureDevOps);

    /// <summary>A sign-in is in progress: the other sign-in buttons wait.</summary>
    public bool IsBusy => PendingCode is not null || TokenTarget is not null || _verifying;

    private bool _verifying;

    private void InitializeIntegrations()
    {
        if (_accounts is null) return;
        Hosts.Add(new(HostingProvider.GitHub, "GitHub", "GH", "#FF24292F", _accounts.IsSupported(HostingProvider.GitHub)));
        Hosts.Add(new(HostingProvider.GitLab, "GitLab", "GL", "#FFC6471B", _accounts.IsSupported(HostingProvider.GitLab)));
        Hosts.Add(new(HostingProvider.AzureDevOps, "Azure DevOps", "AZ", "#FF0A63B8", _accounts.IsSupported(HostingProvider.AzureDevOps)));
        Hosts.Add(new(HostingProvider.Bitbucket, "Bitbucket", "BB", "#FF0B52CC", _accounts.IsSupported(HostingProvider.Bitbucket)));
        _accounts.Changed += OnAccountsChanged;
        SyncHosts();
    }

    private void OnAccountsChanged(object? sender, EventArgs e) => SyncHosts();

    private void SyncHosts()
    {
        if (_accounts is null) return;
        foreach (var host in Hosts)
        {
            host.Account = _accounts.AccountOf(host.Provider);
            // Azure DevOps is always signed in to with a personal access token: no browser button.
            host.CanUseBrowser = host.Provider != HostingProvider.AzureDevOps && _accounts.SupportsDeviceFlow(host.Provider);
        }
        OnPropertyChanged(nameof(BrowserSignInUnavailable));
    }

    /// <summary>Signs in with the browser (OAuth device flow); the view shows <see cref="PendingCode"/>.</summary>
    [RelayCommand]
    private async Task SignInWithBrowserAsync(HostConnectionItem host)
    {
        if (_accounts is null || IsBusy) return;
        CancelSignIn();
        var signIn = _signIn = new CancellationTokenSource();
        SigningInTo = host;
        try
        {
            var result = await _accounts.SignInWithDeviceFlowAsync(host.Provider, code => PendingCode = code, signIn.Token);
            Report(host, result);
        }
        finally
        {
            PendingCode = null;
            SigningInTo = null;
            if (ReferenceEquals(_signIn, signIn)) _signIn = null;
            signIn.Dispose();
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand]
    private void BeginTokenEntry(HostConnectionItem host)
    {
        if (IsBusy) return;
        TokenInput = string.Empty;
        OrganizationInput = string.Empty;
        TokenError = null;
        TokenTarget = host;
    }

    [RelayCommand]
    private void CancelTokenEntry()
    {
        TokenInput = string.Empty;
        OrganizationInput = string.Empty;
        TokenError = null;
        TokenTarget = null;
    }

    /// <summary>Opens the page where the token is created: the organization's when one was typed.</summary>
    [RelayCommand]
    private async Task OpenTokenPageAsync()
    {
        if (_launcher is null) return;
        TokenError = null;
        var organization = AzureDevOpsOrganization.Parse(OrganizationInput);
        if (!string.IsNullOrWhiteSpace(OrganizationInput) && organization is null)
        {
            TokenError = OrganizationUnreadable;
            return;
        }
        var url = AzureDevOpsOrganization.TokenPageUrl(organization);
        if (!await _launcher.OpenAsync(url)) _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Issue.Failure.BrowserFailed"), url);
    }

    private static string OrganizationUnreadable => Loc.Get("Integrations.OrganizationUnreadable");

    private bool CanConnectWithToken() => TokenInput.Trim().Length > 0 && !_verifying;

    /// <summary>Checks the typed token with the service, then keeps it in the vault.</summary>
    [RelayCommand(CanExecute = nameof(CanConnectWithToken))]
    private async Task ConnectWithTokenAsync()
    {
        if (_accounts is null || TokenTarget is not { } host) return;
        string? organization = null;
        if (host.Provider == HostingProvider.AzureDevOps && !string.IsNullOrWhiteSpace(OrganizationInput))
        {
            organization = AzureDevOpsOrganization.Parse(OrganizationInput);
            if (organization is null)
            {
                TokenError = OrganizationUnreadable;
                return;
            }
        }
        _verifying = true;
        ConnectWithTokenCommand.NotifyCanExecuteChanged();
        TokenError = null;
        try
        {
            var result = await _accounts.ConnectWithTokenAsync(host.Provider, TokenInput, organization, _lifetime.Token);
            switch (result)
            {
                case Success<HostAccount>:
                    Report(host, result);
                    TokenTarget = null;
                    TokenInput = string.Empty;
                    break;
                case AuthenticationFailed a:
                    TokenError = a.Message;
                    break;
                case Failure f:
                    TokenError = f.Message;
                    break;
                case Canceled or Conflict:
                    break;
            }
        }
        finally
        {
            _verifying = false;
            ConnectWithTokenCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    [RelayCommand]
    private async Task SignOutAsync(HostConnectionItem host)
    {
        if (_accounts is null || host.Account is not { } account) return;
        await _accounts.SignOutAsync(account, _lifetime.Token);
        _notifier?.Notify(NotificationSeverity.Info, Loc.Format("Integrations.SignedOut.Title", host.Name),
            Loc.Get("Integrations.SignedOut.Message"));
    }

    private void Report(HostConnectionItem host, GitResult<HostAccount> result)
    {
        switch (result)
        {
            case Success<HostAccount> { Value: var account }:
                _notifier?.Notify(NotificationSeverity.Success, Loc.Format("Integrations.Connected.Title", host.Name), Loc.Format("Integrations.Connected.Message", account.Login));
                break;
            case AuthenticationFailed a:
                _notifier?.Notify(NotificationSeverity.Error, Loc.Format("Integrations.Refused", host.Name), a.Message);
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Error, Loc.Format("Integrations.Failed", host.Name), f.Message);
                break;
            case Canceled or Conflict:
                break;
        }
    }

    private void DisposeIntegrations()
    {
        if (_accounts is not null) _accounts.Changed -= OnAccountsChanged;
        _signIn?.Cancel();
    }
}
