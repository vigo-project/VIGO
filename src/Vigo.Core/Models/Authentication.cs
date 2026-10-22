namespace Vigo.Core.Models;

/// <summary>Why a remote refused the operation, deduced from git's output.</summary>
public enum AuthenticationCause
{
    /// <summary>HTTP 401 / "Authentication failed": credentials sent and refused (expired or revoked token…).</summary>
    CredentialsRejected,

    /// <summary>HTTP 403: authenticated but not allowed (missing scope, no access to the repository).</summary>
    AccessDenied,

    /// <summary>Credentials required and none available ("could not read Username", no credential helper).</summary>
    NoCredentials,

    /// <summary>"Permission denied (publickey)": no SSH key accepted by the server.</summary>
    SshKeyRejected,

    /// <summary>"Host key verification failed": the server's host key is unknown or changed.</summary>
    SshHostUnknown,

    Unknown,
}

/// <summary>Everything the authentication dialog shows about a refused fetch / pull / push.</summary>
/// <param name="Command">Command shown in the technical details ("git fetch --all").</param>
/// <param name="RemoteName">Remote concerned; null when it could not be determined.</param>
/// <param name="Url">URL that was refused (from git's output, otherwise the remote's URL).</param>
/// <param name="Details">Full git output.</param>
/// <param name="CredentialHelper">Effective <c>credential.helper</c> ("manager"); null when there is none.</param>
public sealed record AuthenticationProblem(
    string Command,
    string? RemoteName,
    RemoteUrl? Url,
    AuthenticationCause Cause,
    string Message,
    string? Details,
    string? CredentialHelper)
{
    /// <summary>Cause read from git's error output (English, LC_ALL=C).</summary>
    public static AuthenticationCause CauseOf(string text) => text switch
    {
        _ when text.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) => AuthenticationCause.SshHostUnknown,
        _ when text.Contains("Permission denied (publickey", StringComparison.OrdinalIgnoreCase) => AuthenticationCause.SshKeyRejected,
        _ when text.Contains("returned error: 403", StringComparison.OrdinalIgnoreCase) => AuthenticationCause.AccessDenied,
        _ when text.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
               || text.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase) => AuthenticationCause.NoCredentials,
        _ when text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("returned error: 401", StringComparison.OrdinalIgnoreCase)
               || text.Contains("HTTP Basic: Access denied", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Invalid username or", StringComparison.OrdinalIgnoreCase) => AuthenticationCause.CredentialsRejected,
        _ => AuthenticationCause.Unknown,
    };
}

/// <summary>Try again as is: Git Credential Manager asks for new credentials (the refused ones were dropped by git).</summary>
public sealed record RetryAuthentication
{
    public static readonly RetryAuthentication Instance = new();
    private RetryAuthentication() { }
}

/// <summary>Store a personal access token with the credential helper, then try again.</summary>
public sealed record UseAccessToken(string UserName, string Token)
{
    // Never print the token (logs, debugger display).
    public override string ToString() => $"UseAccessToken {{ UserName = {UserName}, Token = *** }}";
}

/// <summary>Replace the URL of the remote (HTTPS ⇄ SSH), then try again.</summary>
public sealed record SwitchRemoteUrl(string RemoteName, string NewUrl);

/// <summary>What the user chose in the authentication dialog.</summary>
public union AuthenticationChoice(RetryAuthentication, UseAccessToken, SwitchRemoteUrl);
