using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

/// <summary>
/// GitHub sign-in (J6.1, ADR-044): the OAuth device flow (no secret embedded in the app, only the public client id of
/// the OAuth App with "Enable Device Flow" checked) and the account behind a token (personal access token or OAuth).
/// </summary>
/// <param name="http">Shared client; no base address or headers expected.</param>
/// <param name="clientId">Client id of VIGO's OAuth App; empty when none is registered (device flow unavailable).</param>
/// <param name="time">Clock for the code's expiry.</param>
/// <param name="logger">Receives one line per refused request (status, scopes, expiry; never the token).</param>
public sealed partial class GitHubClient(
    HttpClient http, string? clientId, TimeProvider? time = null, Microsoft.Extensions.Logging.ILogger? logger = null) : IHostingClient
{
    /// <summary>Scopes asked at sign-in: repositories (clone, pull requests, checks) and the profile.</summary>
    public const string Scopes = "repo read:user";

    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string UserUrl = "https://api.github.com/user";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public HostingProvider Provider => HostingProvider.GitHub;

    public bool SupportsDeviceFlow => !string.IsNullOrWhiteSpace(clientId);

    public async Task<GitResult<HostUser>> GetUserAsync(string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessTokenOf(token));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // GitHub rejects requests without a user agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VIGO", "0.1"));

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new AuthenticationFailed("github", Loc.Get("Hosting.GitHubTokenRefused"));
            }
            if (!response.IsSuccessStatusCode) return new Failure(Loc.Format("Hosting.StatusReply", "GitHub", (int)response.StatusCode, response.ReasonPhrase));

            var user = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubUser, cancellationToken);
            return user is { Login.Length: > 0 }
                ? new Success<HostUser>(new HostUser(user.Login, string.IsNullOrWhiteSpace(user.Name) ? null : user.Name, user.AvatarUrl))
                : new Failure(Loc.Get("Hosting.GitHubUnreadable"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return new Failure(Loc.Format("Hosting.Unreachable", "GitHub", e.Message), Exception: e);
        }
    }

    public async Task<GitResult<DeviceCode>> StartDeviceFlowAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsDeviceFlow) return new Failure(Loc.Get("Hosting.GitHubNoApp"));

        try
        {
            using var response = await PostFormAsync(DeviceCodeUrl, [("client_id", clientId!), ("scope", Scopes)], cancellationToken);
            var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.DeviceCodeResponse, cancellationToken);
            if (body is not { DeviceCode.Length: > 0, UserCode.Length: > 0 })
                return new Failure(body?.ErrorDescription ?? "GitHub n'a pas fourni de code d'appareil.");

            return new Success<DeviceCode>(new DeviceCode(
                body.UserCode, body.VerificationUri ?? "https://github.com/login/device", body.DeviceCode,
                _time.GetUtcNow().AddSeconds(body.ExpiresIn > 0 ? body.ExpiresIn : 900),
                TimeSpan.FromSeconds(body.Interval > 0 ? body.Interval : 5)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return new Failure(Loc.Format("Hosting.Unreachable", "GitHub", e.Message), Exception: e);
        }
    }

    public async Task<GitResult<DevicePoll>> PollDeviceFlowAsync(DeviceCode code, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await PostFormAsync(AccessTokenUrl,
                [("client_id", clientId ?? string.Empty), ("device_code", code.Secret), ("grant_type", "urn:ietf:params:oauth:grant-type:device_code")],
                cancellationToken);
            var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.AccessTokenResponse, cancellationToken);
            if (body is null) return new Failure(Loc.Get("Hosting.GitHubUnreadable"));
            if (!string.IsNullOrEmpty(body.AccessToken))
            {
                // Whether GitHub issues an expiring token (expires_in, refresh_token) tells if a daily sign-out is expiry or revocation.
                var hasRefresh = !string.IsNullOrEmpty(body.RefreshToken);
                if (logger is not null) LogIssued(logger, body.ExpiresIn, hasRefresh, body.Scope ?? "-");
                return new Success<DevicePoll>(new DevicePoll(Pack(body, body.RefreshToken)));
            }

            // https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#error-codes-for-the-device-flow
            return body.Error switch
            {
                "authorization_pending" => new Success<DevicePoll>(new DevicePoll(null)),
                "slow_down" => new Success<DevicePoll>(new DevicePoll(null, SlowDown: true)),
                "expired_token" => new Failure(Loc.Get("Hosting.CodeExpired")),
                "access_denied" => new Failure(Loc.Get("Hosting.GitHubDenied")),
                _ => new Failure(body.ErrorDescription ?? Loc.Format("Hosting.GitHubRefused", body.Error)),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return new Failure(Loc.Format("Hosting.Unreachable", "GitHub", e.Message), Exception: e);
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "GitHub issued a token: expires_in {ExpiresIn} s (0 = no expiry), refresh token {HasRefresh}, scopes [{Scope}]")]
    private static partial void LogIssued(Microsoft.Extensions.Logging.ILogger logger, int expiresIn, bool hasRefresh, string scope);

    private Task<HttpResponseMessage> PostFormAsync(string url, (string Key, string Value)[] fields, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VIGO", "0.1"));
        // GitHub answers 200 with an "error" field for the expected polling states; other statuses are read the same way.
        return http.SendAsync(request, cancellationToken);
    }
}

internal sealed record GitHubUser(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

internal sealed record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")] string? DeviceCode,
    [property: JsonPropertyName("user_code")] string? UserCode,
    [property: JsonPropertyName("verification_uri")] string? VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

internal sealed record AccessTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

[JsonSerializable(typeof(GitHubUser))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(AccessTokenResponse))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
