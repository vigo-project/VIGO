using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Microsoft Entra ID sign-in (ADR-047): the OAuth device flow over plain HTTP (no MSAL: Native AOT), against the
// "common" tenant so that work, school and personal Microsoft accounts all work. The vault keeps the access token,
// its expiry and the refresh token in one string; a personal access token is kept as is.
public sealed partial class AzureDevOpsClient
{
    /// <summary>Scopes asked at sign-in: Azure DevOps' delegated permission and a refresh token.</summary>
    public const string Scopes = "499b84ac-1321-427f-aa17-267ca6975798/user_impersonation offline_access";

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string StoredPrefix = "oauth1|";
    private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool SupportsDeviceFlow => !string.IsNullOrWhiteSpace(clientId);

    public async Task<GitResult<DeviceCode>> StartDeviceFlowAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsDeviceFlow) return new Failure(Loc.Get("Hosting.AzureNoApp"));

        try
        {
            using var response = await PostFormAsync(DeviceCodeUrl, [("client_id", clientId!), ("scope", Scopes)], cancellationToken);
            var body = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureDeviceCode, cancellationToken);
            if (body is not { DeviceCode.Length: > 0, UserCode.Length: > 0 })
                return new Failure(body?.ErrorDescription ?? "Microsoft n'a pas fourni de code d'appareil.");

            return new Success<DeviceCode>(new DeviceCode(
                body.UserCode, body.VerificationUri ?? "https://microsoft.com/devicelogin", body.DeviceCode,
                _time.GetUtcNow().AddSeconds(body.ExpiresIn > 0 ? body.ExpiresIn : 900),
                TimeSpan.FromSeconds(body.Interval > 0 ? body.Interval : 5)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Unreachable(e);
        }
    }

    public async Task<GitResult<DevicePoll>> PollDeviceFlowAsync(DeviceCode code, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await PostFormAsync(TokenUrl,
                [("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("client_id", clientId ?? string.Empty), ("device_code", code.Secret)],
                cancellationToken);
            var body = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureTokenResponse, cancellationToken);
            if (body is null) return new Failure(Loc.Get("Hosting.MicrosoftUnreadable"));
            if (!string.IsNullOrEmpty(body.AccessToken)) return new Success<DevicePoll>(new DevicePoll(Pack(body, body.RefreshToken)));

            return body.Error switch
            {
                "authorization_pending" => new Success<DevicePoll>(new DevicePoll(null)),
                "slow_down" => new Success<DevicePoll>(new DevicePoll(null, SlowDown: true)),
                "expired_token" or "code_expired" => new Failure(Loc.Get("Hosting.CodeExpired")),
                "authorization_declined" or "access_denied" => new Failure(Loc.Get("Hosting.MicrosoftDeclined") + " " + AdminHint),
                _ when NeedsAdminApproval(body) => new Failure(AdminApprovalMessage()),
                _ => new Failure(FirstLine(body.ErrorDescription) ?? Loc.Format("Hosting.MicrosoftRefused", body.Error)),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Unreachable(e);
        }
    }

    /// <summary>Renews an OAuth access token that expires within two minutes; a personal access token is returned as is.</summary>
    public async Task<GitResult<ResolvedToken>> ResolveTokenAsync(string stored, CancellationToken cancellationToken = default)
    {
        if (!TryUnpack(stored, out var access, out var refresh, out var expiresAt))
            return new Success<ResolvedToken>(new ResolvedToken(stored, null));
        if (_time.GetUtcNow() + RenewMargin < expiresAt) return new Success<ResolvedToken>(new ResolvedToken(access, null));
        if (!SupportsDeviceFlow)
            return new AuthenticationFailed(Remote, Loc.Get("Hosting.AzureSessionExpired"));

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

    // A refresh token works once: calls made at the same time (work item images) must share one renewal.
    private readonly object _renewalGate = new();
    private (string Stored, Task<GitResult<ResolvedToken>> Renewal)? _renewal;

    // Not tied to the caller's cancellation: the renewal is shared, and a half-finished one would lose the refresh token.
    private async Task<GitResult<ResolvedToken>> RenewAsync(string refresh)
    {
        try
        {
            using var response = await PostFormAsync(TokenUrl,
                [("grant_type", "refresh_token"), ("client_id", clientId!), ("refresh_token", refresh), ("scope", Scopes)], CancellationToken.None);
            var body = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureTokenResponse, CancellationToken.None);
            if (body is { AccessToken.Length: > 0 })
            {
                // The service may keep the same refresh token: it is then absent from the answer.
                var renewed = Pack(body, string.IsNullOrEmpty(body.RefreshToken) ? refresh : body.RefreshToken);
                return new Success<ResolvedToken>(new ResolvedToken(body.AccessToken, renewed));
            }
            return body?.Error == "invalid_grant"
                ? new AuthenticationFailed(Remote, Loc.Get("Hosting.AzureSessionRevoked"))
                : new Failure(FirstLine(body?.ErrorDescription) ?? Loc.Get("Hosting.MicrosoftRenewRefused"));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Unreachable(e);
        }
    }

    private static string AdminHint => Loc.Get("Hosting.AdminHint");

    // AADSTS90094 / 65001 / 900941: the tenant does not let users consent to the application themselves.
    private static bool NeedsAdminApproval(AzureTokenResponse body) =>
        body.Error is "consent_required" or "interaction_required"
        || (body.ErrorDescription?.Contains("AADSTS90094", StringComparison.Ordinal) ?? false)
        || (body.ErrorDescription?.Contains("AADSTS65001", StringComparison.Ordinal) ?? false)
        || (body.ErrorDescription?.Contains("AADSTS900941", StringComparison.Ordinal) ?? false);

    private string AdminApprovalMessage() =>
        Loc.Format("Hosting.AdminApproval", $"https://login.microsoftonline.com/organizations/adminconsent?client_id={clientId}");

    // "oauth1|<expiry, Unix seconds>|<refresh token>|<access token>": none of the parts contains a '|'.
    private string Pack(AzureTokenResponse response, string? refresh) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{StoredPrefix}{_time.GetUtcNow().AddSeconds(response.ExpiresIn > 0 ? response.ExpiresIn : 3600).ToUnixTimeSeconds()}|{refresh}|{response.AccessToken}");

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

    // Entra ID access tokens are JWTs; a personal access token has no dot.
    private static bool IsJwt(string token) => token.StartsWith("eyJ", StringComparison.Ordinal) && token.Contains('.', StringComparison.Ordinal);

    private static string? FirstLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Split('\n', 2)[0].Trim();

    private Task<HttpResponseMessage> PostFormAsync(string url, (string Key, string Value)[] fields, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VIGO", "0.1"));
        // Expected polling states come with a 400 and an "error" member: the body is read whatever the status.
        return http.SendAsync(request, cancellationToken);
    }
}

internal sealed record AzureDeviceCode(
    [property: JsonPropertyName("device_code")] string? DeviceCode,
    [property: JsonPropertyName("user_code")] string? UserCode,
    [property: JsonPropertyName("verification_uri")] string? VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

internal sealed record AzureTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);
