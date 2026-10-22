namespace Vigo.Core.Models;

/// <summary>Supported hosting providers (J6). Azure DevOps: MSAL/Entra ID, PAT fallback (ADR-005).</summary>
public enum HostingProvider
{
    GitHub,
    GitLab,
    AzureDevOps,
    Bitbucket,
}

/// <summary>User of a hosting service as the service reports it.</summary>
public sealed record HostUser(string Login, string? DisplayName, string? AvatarUrl);

/// <summary>An account VIGO is signed in to; its token lives in the credential vault, never in this record.</summary>
/// <param name="Provider">Hosting service.</param>
/// <param name="Login">Account name on the service (key of the token in the vault).</param>
/// <param name="DisplayName">Full name when the service gives one.</param>
/// <param name="AvatarUrl">Picture of the account.</param>
/// <param name="ProfileId">Git profile the account belongs to (J6.7); accounts saved before profiles are the "default" one.</param>
/// <param name="Organization">Azure DevOps organization the token is limited to; null for a token valid on every organization (and on other services).</param>
public sealed record HostAccount(HostingProvider Provider, string Login, string? DisplayName, string? AvatarUrl, string ProfileId = SessionProfiles.Default, string? Organization = null)
{
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Login : DisplayName;
}

/// <summary>Code the user types on the provider's page to authorize VIGO (OAuth device flow).</summary>
/// <param name="UserCode">Short code to type, e.g. "WDJB-MJHT".</param>
/// <param name="VerificationUrl">Page where the code is typed.</param>
/// <param name="Secret">Device code polled with; never shown.</param>
/// <param name="ExpiresAt">The code stops working at this time.</param>
/// <param name="Interval">Minimum delay between two polls.</param>
public sealed record DeviceCode(string UserCode, string VerificationUrl, string Secret, DateTimeOffset ExpiresAt, TimeSpan Interval);
