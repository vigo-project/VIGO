using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

public enum AuthenticationOptionKind
{
    /// <summary>Try again: Git Credential Manager asks for new credentials, or the SSH agent now has the key.</summary>
    Retry,

    /// <summary>Store a personal access token, then try again (HTTP(S) only).</summary>
    AccessToken,

    /// <summary>Switch the remote to SSH (from HTTPS) or to HTTPS (from SSH), then try again.</summary>
    SwitchProtocol,
}

/// <summary>One choice of the authentication dialog (mockup: radio card with a title and a description).</summary>
public sealed partial class AuthenticationOption(AuthenticationOptionKind kind, string title, string description, bool isAvailable)
    : ObservableObject
{
    public AuthenticationOptionKind Kind { get; } = kind;

    public string Title { get; } = title;

    /// <summary>What happens, or why the option is unavailable.</summary>
    public string Description { get; } = description;

    public bool IsAvailable { get; } = isAvailable;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// "Échec d'authentification" dialog (mockup authOpen): what was refused and why, the ways out (try again through
/// Git Credential Manager, personal access token, switch HTTPS ⇄ SSH), technical details.
/// </summary>
/// <remarks>
/// The token typed by the user only lives in <see cref="Token"/> until <see cref="ToChoice"/>; it is never logged
/// (<see cref="UseAccessToken.ToString"/> hides it).
/// </remarks>
public sealed partial class AuthenticationDialogViewModel : ObservableObject
{
    private readonly AuthenticationProblem _problem;

    public AuthenticationDialogViewModel(AuthenticationProblem problem)
    {
        _problem = problem;
        UserName = problem.Url?.User ?? string.Empty;
        foreach (var option in BuildOptions(problem))
        {
            option.PropertyChanged += OnOptionChanged;
            Options.Add(option);
        }
        // The first available option is preselected (mockup: the recommended one comes first).
        if (Options.FirstOrDefault(o => o.IsAvailable) is { } first) first.IsSelected = true;
    }

    private string Host => _problem.Url is { Host.Length: > 0 } url ? url.Host : _problem.RemoteName ?? Loc.Get("Auth.Host.Fallback");

    private string Repository => _problem.Url is { DisplayPath.Length: > 0 } url ? url.DisplayPath : _problem.RemoteName ?? Loc.Get("Auth.Repository.Fallback");

    /// <summary>"github.com a refusé les identifiants pour atlas/atlas-api (HTTP 401)."</summary>
    public string Explanation => _problem.Cause switch
    {
        AuthenticationCause.CredentialsRejected => Loc.Format("Auth.Explanation.CredentialsRejected", Host, Repository),
        AuthenticationCause.AccessDenied => Loc.Format("Auth.Explanation.AccessDenied", Host, Repository),
        AuthenticationCause.NoCredentials => Loc.Format("Auth.Explanation.NoCredentials", Host, Repository),
        AuthenticationCause.SshKeyRejected => Loc.Format("Auth.Explanation.SshKeyRejected", Host, Repository),
        AuthenticationCause.SshHostUnknown => Loc.Format("Auth.Explanation.SshHostUnknown", Host),
        _ => _problem.Message,
    };

    public ObservableCollection<AuthenticationOption> Options { get; } = [];

    public AuthenticationOption? SelectedOption => Options.FirstOrDefault(o => o.IsSelected);

    /// <summary>The access-token fields are shown.</summary>
    public bool IsAccessTokenSelected => SelectedOption?.Kind == AuthenticationOptionKind.AccessToken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string UserName { get; set; }

    /// <summary>Personal access token (PasswordBox); never logged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string Token { get; set; } = string.Empty;

    /// <summary>Text of the primary button (mockup authCta).</summary>
    public string ConfirmText => SelectedOption?.Kind switch
    {
        AuthenticationOptionKind.AccessToken => Loc.Get("Auth.Confirm.StoreToken"),
        AuthenticationOptionKind.SwitchProtocol => Loc.Get(_problem.Url?.IsHttp == true ? "Auth.Confirm.SwitchToSsh" : "Auth.Confirm.SwitchToHttps"),
        AuthenticationOptionKind.Retry when _problem.Url?.IsHttp == true => Loc.Get("Auth.Confirm.Reconnect"),
        _ => Loc.Get("Auth.Confirm.TryAgain"),
    };

    public bool CanConfirm => SelectedOption switch
    {
        null or { IsAvailable: false } => false,
        { Kind: AuthenticationOptionKind.AccessToken } => !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Token),
        _ => true,
    };

    /// <summary>"$ git fetch --all", git's output, credential helper (mockup "Détails techniques").</summary>
    public string TechnicalDetails
    {
        get
        {
            var lines = new List<string> { $"$ {_problem.Command}" };
            if (!string.IsNullOrWhiteSpace(_problem.Details)) lines.Add(_problem.Details.Trim());
            if (_problem.RemoteName is { } remote)
                lines.Add(Loc.Format("Auth.Details.Remote", remote, _problem.Url?.Original ?? Loc.Get("Auth.Details.UnknownUrl")));
            lines.Add(Loc.Format("Auth.Details.Helper", _problem.CredentialHelper ?? Loc.Get("Auth.Details.NoHelper")));
            return string.Join('\n', lines);
        }
    }

    /// <summary>The choice to apply; null when the selection cannot be confirmed.</summary>
    public AuthenticationChoice? ToChoice() => !CanConfirm ? null : SelectedOption!.Kind switch
    {
        AuthenticationOptionKind.Retry => RetryAuthentication.Instance,
        AuthenticationOptionKind.AccessToken => new UseAccessToken(UserName.Trim(), Token.Trim()),
        AuthenticationOptionKind.SwitchProtocol when SwitchedUrl(_problem) is { } url && _problem.RemoteName is { } remote =>
            new SwitchRemoteUrl(remote, url),
        _ => null,
    };

    private static string? SwitchedUrl(AuthenticationProblem problem) => problem.Url?.IsHttp == true ? problem.Url.ToSsh() : problem.Url?.ToHttps();

    private static IEnumerable<AuthenticationOption> BuildOptions(AuthenticationProblem problem)
    {
        var url = problem.Url;
        var host = url is { Host.Length: > 0 } ? url.Host : Loc.Get("Auth.Host.FallbackLower");
        var helper = problem.CredentialHelper;
        var switched = SwitchedUrl(problem);
        var canSwitch = switched is not null && problem.RemoteName is not null;
        var switchUnavailable = Loc.Get(problem.RemoteName is null ? "Auth.Option.Switch.NoRemote" : "Auth.Option.Switch.NotConvertible");

        if (url is { IsHttp: true } || url is null && problem.Cause is AuthenticationCause.CredentialsRejected or AuthenticationCause.AccessDenied or AuthenticationCause.NoCredentials)
        {
            yield return new AuthenticationOption(AuthenticationOptionKind.Retry, Loc.Get("Auth.Option.Reconnect.Title"),
                helper is null
                    ? Loc.Get("Auth.Option.Reconnect.NoHelper")
                    : Loc.Format("Auth.Option.Reconnect.Description", HelperName(helper)),
                helper is not null);
            yield return new AuthenticationOption(AuthenticationOptionKind.AccessToken, Loc.Get("Auth.Option.Token.Title"),
                helper is null
                    ? Loc.Get("Auth.Option.Token.NoHelper")
                    : Loc.Format("Auth.Option.Token.Description", HelperName(helper), host),
                helper is not null && url is { IsHttp: true });
            yield return new AuthenticationOption(AuthenticationOptionKind.SwitchProtocol, Loc.Get("Auth.Confirm.SwitchToSsh"),
                canSwitch ? Loc.Format("Auth.Option.Ssh.Description", switched, host) : switchUnavailable, canSwitch);
            yield break;
        }

        yield return new AuthenticationOption(AuthenticationOptionKind.Retry, Loc.Get("Auth.Confirm.TryAgain"),
            problem.Cause == AuthenticationCause.SshHostUnknown
                ? Loc.Format("Auth.Option.Retry.HostUnknown", host)
                : Loc.Format("Auth.Option.Retry.KeyMissing", host),
            true);
        yield return new AuthenticationOption(AuthenticationOptionKind.SwitchProtocol, Loc.Get("Auth.Confirm.SwitchToHttps"),
            canSwitch ? Loc.Format("Auth.Option.Https.Description", switched, helper is null ? Loc.Get("Auth.Helper.Generic") : HelperName(helper)) : switchUnavailable,
            canSwitch);
    }

    private static string HelperName(string helper) =>
        helper.Contains("manager", StringComparison.OrdinalIgnoreCase) ? "Git Credential Manager" : Loc.Format("Auth.Helper.Named", helper);

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AuthenticationOption.IsSelected) || sender is not AuthenticationOption { IsSelected: true } selected) return;
        foreach (var other in Options.Where(o => o != selected)) other.IsSelected = false;
        OnPropertyChanged(nameof(SelectedOption));
        OnPropertyChanged(nameof(IsAccessTokenSelected));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CanConfirm));
    }
}
