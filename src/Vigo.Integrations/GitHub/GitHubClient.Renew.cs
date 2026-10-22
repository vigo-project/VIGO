using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Expiring tokens: when the OAuth App has token expiration on, GitHub issues an access token that lives 8 hours with a refresh
// token. The vault then holds "gh1|<expiry, Unix seconds>|<refresh token>|<access token>"; a personal access token (or a
// token that never expires) is stored as is.
public sealed partial class GitHubClient
{
    private const string StoredPrefix = "gh1|";
    private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(2);

    // A refresh token works once: calls made at the same time (the pull requests and the issues) must share one renewal.
    private readonly object _renewalGate = new();
    private (string Stored, Task<GitResult<ResolvedToken>> Renewal)? _renewal;

    /// <summary>Renews an access token that expires within two minutes; a personal access token is returned as is.</summary>
    public async Task<GitResult<ResolvedToken>> ResolveTokenAsync(string stored, CancellationToken cancellationToken = default)
    {
        if (!TryUnpack(stored, out var access, out var refresh, out var expiresAt))
            return new Success<ResolvedToken>(new ResolvedToken(stored, null));
        if (_time.GetUtcNow() + RenewMargin < expiresAt) return new Success<ResolvedToken>(new ResolvedToken(access, null));
        if (!SupportsDeviceFlow)
            return new AuthenticationFailed("github", Loc.Get("Hosting.GitHubSessionExpired"));

        Task<GitResult<ResolvedToken>> renewal;
        lock (_renewalGate)
        {
            if (_renewal is { } known && known.Stored == stored) renewal = known.Renewal;
            else
            {
                renewal = RenewAsync(refresh);
                _renewal = (stored, renewal);
            }
        }
        // The shared renewal keeps going for the other callers; only this wait is abandoned.
        GitResult<ResolvedToken> result;
        try
        {
            result = await renewal.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        // A refusal is final for this value; a network failure is worth another try.
        if (result is not Success<ResolvedToken> and not AuthenticationFailed)
        {
            lock (_renewalGate)
            {
                if (_renewal is { } current && current.Renewal == renewal) _renewal = null;
            }
        }
        return result;
    }

    // Not tied to the caller's cancellation: the renewal is shared, and a half-finished one would lose the refresh token.
    private async Task<GitResult<ResolvedToken>> RenewAsync(string refresh)
    {
        try
        {
            using var response = await PostFormAsync(AccessTokenUrl,
                [("client_id", clientId!), ("grant_type", "refresh_token"), ("refresh_token", refresh)], CancellationToken.None);
            var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.AccessTokenResponse, CancellationToken.None);
            if (body is { AccessToken.Length: > 0 })
            {
                var hasNewRefresh = !string.IsNullOrEmpty(body.RefreshToken);
                if (logger is not null) LogRenewed(logger, body.ExpiresIn, hasNewRefresh);
                return new Success<ResolvedToken>(new ResolvedToken(body.AccessToken, Pack(body, hasNewRefresh ? body.RefreshToken : refresh)));
            }
            var error = body?.Error ?? "-";
            var description = body?.ErrorDescription ?? "-";
            if (logger is not null) LogRenewalRefused(logger, error, description);
            return new AuthenticationFailed("github",
                Loc.Get("Hosting.GitHubSessionRevoked"), Details: body?.ErrorDescription);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new Failure(Loc.Format("Hosting.Unreachable", "GitHub", e.Message), Exception: e);
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "GitHub renewed the token: expires_in {ExpiresIn} s, new refresh token {HasRefresh}")]
    private static partial void LogRenewed(Microsoft.Extensions.Logging.ILogger logger, int expiresIn, bool hasRefresh);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "GitHub refused to renew the token: {Error} ({Description})")]
    private static partial void LogRenewalRefused(Microsoft.Extensions.Logging.ILogger logger, string error, string description);

    // What the vault keeps for a token the service issued: expiring tokens carry their refresh token and expiry.
    private string Pack(AccessTokenResponse response, string? refresh) =>
        response.ExpiresIn > 0 && !string.IsNullOrEmpty(refresh)
            ? string.Create(CultureInfo.InvariantCulture,
                $"{StoredPrefix}{_time.GetUtcNow().AddSeconds(response.ExpiresIn).ToUnixTimeSeconds()}|{refresh}|{response.AccessToken}")
            : response.AccessToken!;

    private static bool TryUnpack(string stored, out string access, out string refresh, out DateTimeOffset expiresAt)
    {
        access = refresh = string.Empty;
        expiresAt = default;
        if (!stored.StartsWith(StoredPrefix, StringComparison.Ordinal)) return false;
        var parts = stored[StoredPrefix.Length..].Split('|', 3);
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        refresh = parts[1];
        access = parts[2];
        return true;
    }

    // The access token inside a stored value (sign-in checks the account with the freshly obtained value).
    private static string AccessTokenOf(string stored) => TryUnpack(stored, out var access, out _, out _) ? access : stored;
}
