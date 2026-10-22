using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

/// <summary>Expiring GitHub tokens: the refresh token is kept at sign-in and used to renew the access token before it expires.</summary>
public sealed class GitHubTokenRenewalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Handler(Func<string, (HttpStatusCode Status, string Json)> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            await Task.Delay(30, cancellationToken);
            var (status, json) = respond(body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (GitHubClient Client, Handler Handler) Create(Func<string, (HttpStatusCode, string)> respond, string? clientId = "client-1")
    {
        var handler = new Handler(respond);
        return (new GitHubClient(new HttpClient(handler), clientId, new Clock(Now)), handler);
    }

    private static string Stored(DateTimeOffset expiry, string refresh = "ghr_old", string access = "ghu_old") =>
        $"gh1|{expiry.ToUnixTimeSeconds()}|{refresh}|{access}";

    private const string Renewed = """{"access_token":"ghu_new","expires_in":28800,"refresh_token":"ghr_new","scope":"repo"}""";

    [Fact]
    public async Task Sign_in_keeps_the_refresh_token_and_the_expiry()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """{"access_token":"ghu_a","expires_in":28800,"refresh_token":"ghr_a"}"""));

        var poll = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(
            new DeviceCode("ABCD", "https://github.com/login/device", "dev", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value).Value;

        Assert.Equal(Stored(Now.AddSeconds(28800), "ghr_a", "ghu_a"), poll.Token);
    }

    [Fact]
    public async Task A_token_that_never_expires_is_stored_as_is()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """{"access_token":"gho_a","scope":"repo"}"""));

        var poll = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(
            new DeviceCode("ABCD", "https://github.com/login/device", "dev", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value).Value;

        Assert.Equal("gho_a", poll.Token);
    }

    [Fact]
    public async Task A_personal_access_token_is_returned_untouched()
    {
        var (client, handler) = Create(_ => throw new InvalidOperationException());

        var resolved = Assert.IsType<Success<ResolvedToken>>((await client.ResolveTokenAsync("ghp_pat", Ct)).Value).Value;

        Assert.Equal(new ResolvedToken("ghp_pat", null), resolved);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task A_token_far_from_expiry_is_not_renewed()
    {
        var (client, handler) = Create(_ => throw new InvalidOperationException());

        var resolved = Assert.IsType<Success<ResolvedToken>>((await client.ResolveTokenAsync(Stored(Now.AddHours(3)), Ct)).Value).Value;

        Assert.Equal(new ResolvedToken("ghu_old", null), resolved);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task An_expired_token_is_renewed_with_its_refresh_token()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, Renewed));

        var resolved = Assert.IsType<Success<ResolvedToken>>((await client.ResolveTokenAsync(Stored(Now.AddHours(-1)), Ct)).Value).Value;

        Assert.Equal("ghu_new", resolved.AccessToken);
        Assert.Equal(Stored(Now.AddSeconds(28800), "ghr_new", "ghu_new"), resolved.UpdatedStored);
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=ghr_old", body);
        Assert.Contains("client_id=client-1", body);
    }

    [Fact]
    public async Task A_token_about_to_expire_is_renewed_too()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, Renewed));

        await client.ResolveTokenAsync(Stored(Now.AddSeconds(60)), Ct);

        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Calls_made_together_share_one_renewal_because_a_refresh_token_works_once()
    {
        var (client, handler) = Create(body => body.Contains("ghr_old", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, Renewed)
            : (HttpStatusCode.OK, """{"error":"bad_refresh_token"}"""));
        var stored = Stored(Now.AddHours(-1));

        var results = await Task.WhenAll(client.ResolveTokenAsync(stored, Ct), client.ResolveTokenAsync(stored, Ct), client.ResolveTokenAsync(stored, Ct));

        Assert.Single(handler.Bodies);
        Assert.All(results, r => Assert.Equal("ghu_new", Assert.IsType<Success<ResolvedToken>>(r.Value).Value.AccessToken));
    }

    [Fact]
    public async Task A_canceled_wait_returns_Canceled_and_the_renewal_still_completes_for_others()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, Renewed));
        var stored = Stored(Now.AddHours(-1));
        using var cts = new CancellationTokenSource();

        var canceled = client.ResolveTokenAsync(stored, cts.Token);
        await cts.CancelAsync();

        Assert.IsType<Canceled>((await canceled).Value);
        Assert.IsType<Success<ResolvedToken>>((await client.ResolveTokenAsync(stored, Ct)).Value);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task A_refused_renewal_asks_to_sign_in_again()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """{"error":"bad_refresh_token","error_description":"The refresh token passed is incorrect or expired."}"""));

        var result = await client.ResolveTokenAsync(Stored(Now.AddHours(-1)), Ct);

        var failure = Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.Contains("reconnectez-vous", failure.Message);
    }

    [Fact]
    public async Task Without_a_registered_application_an_expired_token_asks_to_sign_in_again()
    {
        var (client, handler) = Create(_ => throw new InvalidOperationException(), clientId: null);

        var result = await client.ResolveTokenAsync(Stored(Now.AddHours(-1)), Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.Empty(handler.Bodies);
    }
}
