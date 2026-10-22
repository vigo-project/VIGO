using Vigo.Core.Models;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class AuthenticationDialogViewModelTests
{
    private static AuthenticationProblem Problem(
        string url, AuthenticationCause cause, string? helper = "manager", string? remote = "origin") => new(
        "git fetch --all", remote, RemoteUrl.TryParse(url), cause, "Authentication failed",
        "remote: Invalid username or token.\nfatal: Authentication failed for 'https://github.com/atlas/atlas-api.git/'", helper);

    [Fact]
    public void Refused_https_credentials_offer_reconnect_token_and_ssh()
    {
        var sut = new AuthenticationDialogViewModel(Problem("https://github.com/atlas/atlas-api.git", AuthenticationCause.CredentialsRejected));

        Assert.Equal(
            [(AuthenticationOptionKind.Retry, true), (AuthenticationOptionKind.AccessToken, true), (AuthenticationOptionKind.SwitchProtocol, true)],
            sut.Options.Select(o => (o.Kind, o.IsAvailable)));
        Assert.StartsWith("github.com a refusé les identifiants pour atlas/atlas-api (HTTP 401)", sut.Explanation, StringComparison.Ordinal);
        Assert.Same(sut.Options[0], sut.SelectedOption);
        Assert.Equal("Se reconnecter", sut.ConfirmText);
        Assert.Contains("Git Credential Manager", sut.Options[0].Description, StringComparison.Ordinal);
        Assert.Contains("git@github.com:atlas/atlas-api.git", sut.Options[2].Description, StringComparison.Ordinal);
        Assert.IsType<RetryAuthentication>(sut.ToChoice()!.Value.Value);
    }

    [Fact]
    public void Access_token_needs_a_user_name_and_a_token()
    {
        var sut = new AuthenticationDialogViewModel(Problem("https://github.com/atlas/atlas-api.git", AuthenticationCause.CredentialsRejected));

        sut.Options[1].IsSelected = true;

        Assert.False(sut.Options[0].IsSelected);
        Assert.True(sut.IsAccessTokenSelected);
        Assert.Equal("Enregistrer le jeton", sut.ConfirmText);
        Assert.False(sut.CanConfirm);
        Assert.Null(sut.ToChoice());

        sut.UserName = " camille ";
        sut.Token = " ghp_secret ";
        Assert.True(sut.CanConfirm);
        var token = Assert.IsType<UseAccessToken>(sut.ToChoice()!.Value.Value);
        Assert.Equal(("camille", "ghp_secret"), (token.UserName, token.Token));
        Assert.DoesNotContain("ghp_secret", token.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_to_ssh_targets_the_remote()
    {
        var sut = new AuthenticationDialogViewModel(Problem("https://github.com/atlas/atlas-api.git", AuthenticationCause.AccessDenied));

        sut.Options[2].IsSelected = true;

        Assert.Equal("Passer en SSH", sut.ConfirmText);
        Assert.Equal(new SwitchRemoteUrl("origin", "git@github.com:atlas/atlas-api.git"), sut.ToChoice()!.Value.Value);
    }

    [Fact]
    public void Without_a_credential_helper_only_ssh_remains()
    {
        var sut = new AuthenticationDialogViewModel(Problem("https://github.com/atlas/atlas-api.git", AuthenticationCause.NoCredentials, helper: null));

        Assert.Equal([false, false, true], sut.Options.Select(o => o.IsAvailable));
        Assert.Equal(AuthenticationOptionKind.SwitchProtocol, sut.SelectedOption!.Kind);
        Assert.Contains("aucun (credential.helper)", sut.TechnicalDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_ssh_key_offers_retry_and_https()
    {
        var sut = new AuthenticationDialogViewModel(Problem("git@github.com:atlas/atlas-api.git", AuthenticationCause.SshKeyRejected));

        Assert.Equal([AuthenticationOptionKind.Retry, AuthenticationOptionKind.SwitchProtocol], sut.Options.Select(o => o.Kind));
        Assert.Contains("ssh-add", sut.Options[0].Description, StringComparison.Ordinal);
        Assert.Equal("Réessayer", sut.ConfirmText);

        sut.Options[1].IsSelected = true;
        Assert.Equal("Passer en HTTPS", sut.ConfirmText);
        Assert.Equal(new SwitchRemoteUrl("origin", "https://github.com/atlas/atlas-api.git"), sut.ToChoice()!.Value.Value);
    }

    [Fact]
    public void Unknown_remote_cannot_be_switched()
    {
        var sut = new AuthenticationDialogViewModel(Problem("https://github.com/atlas/atlas-api.git", AuthenticationCause.CredentialsRejected, remote: null));

        Assert.False(sut.Options[2].IsAvailable);
        Assert.Contains("git remote set-url", sut.Options[2].Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Technical_details_show_the_command_and_the_output()
    {
        var sut = new AuthenticationDialogViewModel(Problem("https://github.com/atlas/atlas-api.git", AuthenticationCause.CredentialsRejected));

        Assert.StartsWith("$ git fetch --all\nremote: Invalid username or token.", sut.TechnicalDetails, StringComparison.Ordinal);
        Assert.EndsWith("Gestionnaire d'identifiants : manager", sut.TechnicalDetails, StringComparison.Ordinal);
    }
}
