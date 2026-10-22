using Microsoft.Extensions.Logging;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Windows.Security.Credentials;

namespace Vigo.App.Services;

/// <summary>
/// Access tokens in the Windows Credential Locker (<see cref="PasswordVault"/>): encrypted per Windows user, one entry
/// per resource "VIGO/&lt;provider&gt;" and login. Works packaged and unpackaged.
/// </summary>
public sealed partial class CredentialLockerVault(ILogger<CredentialLockerVault> logger) : ITokenVault, ISecretVault
{
    private const string SecretUser = "VIGO";

    private readonly PasswordVault _vault = new();

    // Named secrets (J7.9): resource "VIGO/secret/<name>", fixed user name.
    public string? Retrieve(string name)
    {
        try
        {
            var credential = _vault.Retrieve($"VIGO/secret/{name}", SecretUser);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LogSecretNotRetrieved(logger, name, e.Message);
            return null;
        }
    }

    public void Store(string name, string secret)
    {
        Remove(name);
        _vault.Add(new PasswordCredential($"VIGO/secret/{name}", SecretUser, secret));
    }

    public void Remove(string name)
    {
        try
        {
            _vault.Remove(_vault.Retrieve($"VIGO/secret/{name}", SecretUser));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LogSecretNotRetrieved(logger, name, e.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No secret {Name} in the credential vault ({Reason})")]
    private static partial void LogSecretNotRetrieved(ILogger logger, string name, string reason);

    public string? Retrieve(HostingProvider provider, string login, string profileId)
    {
        try
        {
            var credential = _vault.Retrieve(ResourceOf(provider, profileId), login);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Element not found is reported with a COMException: nothing stored for this account.
            LogNotRetrieved(logger, provider, e.Message);
            return null;
        }
    }

    public void Store(HostingProvider provider, string login, string token, string profileId)
    {
        // Same as the named secrets: do not rely on Add replacing an existing entry (a renewed token must not fail or keep the old one).
        Remove(provider, login, profileId);
        _vault.Add(new PasswordCredential(ResourceOf(provider, profileId), login, token));
    }

    public void Remove(HostingProvider provider, string login, string profileId)
    {
        try
        {
            _vault.Remove(_vault.Retrieve(ResourceOf(provider, profileId), login));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LogNotRemoved(logger, provider, e.Message);
        }
    }

    // The "default" profile keeps the resource names used before profiles existed.
    private static string ResourceOf(HostingProvider provider, string profileId) =>
        profileId == SessionProfiles.Default ? $"VIGO/{provider}" : $"VIGO/{provider}/{profileId}";

    [LoggerMessage(Level = LogLevel.Information, Message = "No {Provider} token in the credential vault ({Reason})")]
    private static partial void LogNotRetrieved(ILogger logger, HostingProvider provider, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "No {Provider} token to remove from the credential vault ({Reason})")]
    private static partial void LogNotRemoved(ILogger logger, HostingProvider provider, string reason);
}
