using System.Globalization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations;

/// <summary>
/// An expiring OAuth token as the vault keeps it: "&lt;prefix&gt;&lt;expiry, Unix seconds&gt;|&lt;refresh token&gt;|&lt;access token&gt;"
/// (none of the parts contains a '|'). Shared by the hosts that issue refresh tokens.
/// </summary>
internal static class StoredToken
{
    public static bool TryUnpack(string prefix, string stored, out string access, out string refresh, out DateTimeOffset expiresAt)
    {
        access = refresh = string.Empty;
        expiresAt = default;
        if (!stored.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = stored[prefix.Length..].Split('|', 3);
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        refresh = parts[1];
        access = parts[2];
        return true;
    }

    public static string Pack(string prefix, DateTimeOffset expiresAt, string? refresh, string? access) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}{expiresAt.ToUnixTimeSeconds()}|{refresh}|{access}");
}

/// <summary>
/// Renews a stored token. A refresh token works once: calls made at the same time must share one renewal.
/// </summary>
internal sealed class TokenRenewal
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private (string Stored, Task<GitResult<ResolvedToken>> Renewal)? _current;

    /// <summary>
    /// Returns the token to use: a value that is not an expiring token as is, a token that lives longer than two minutes
    /// as is, otherwise the outcome of <paramref name="renew"/> (shared by concurrent callers).
    /// </summary>
    /// <param name="canRenew">Whether the host is configured to renew; otherwise <paramref name="expired"/> is returned.</param>
    /// <param name="renew">Renews from the refresh token; not tied to the caller's cancellation, a half-finished renewal would lose it.</param>
    public async Task<GitResult<ResolvedToken>> ResolveAsync(
        string prefix, string stored, TimeProvider time, bool canRenew,
        Func<AuthenticationFailed> expired, Func<string, Task<GitResult<ResolvedToken>>> renew, CancellationToken cancellationToken)
    {
        if (!StoredToken.TryUnpack(prefix, stored, out var access, out var refresh, out var expiresAt))
            return new Success<ResolvedToken>(new ResolvedToken(stored, null));
        if (time.GetUtcNow() + Margin < expiresAt) return new Success<ResolvedToken>(new ResolvedToken(access, null));
        if (!canRenew) return expired();

        Task<GitResult<ResolvedToken>> renewal;
        lock (_gate)
        {
            if (_current is { } known && known.Stored == stored) renewal = known.Renewal;
            else
            {
                renewal = renew(refresh);
                _current = (stored, renewal);
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
            lock (_gate)
            {
                if (_current is { } current && current.Renewal == renewal) _current = null;
            }
        }
        return result;
    }
}
