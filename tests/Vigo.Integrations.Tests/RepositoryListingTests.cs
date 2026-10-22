using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Integrations.AzureDevOps;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

// Repositories of the signed-in account for the clone dialog (J6.13, ADR-077): GitHub /user/repos pages, Azure DevOps organizations.
public sealed class RepositoryListingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body, string? Link)> respond) : HttpMessageHandler
    {
        public List<Uri> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!);
            var (status, body, link) = respond(request);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
            if (link is not null) response.Headers.Add("Link", link);
            return Task.FromResult(response);
        }
    }

    private static HostedRepositoryList Listed(GitResult<HostedRepositoryList> result) =>
        Assert.IsType<Success<HostedRepositoryList>>(result.Value).Value;

    private static string GitHubRepo(string fullName, bool isPrivate = false, bool fork = false, string? description = null)
    {
        var text = description is null ? "null" : "\"" + description + "\"";
        return $$$"""
            {"full_name":"{{{fullName}}}","description":{{{text}}},"private":{{{isPrivate.ToString().ToLowerInvariant()}}},"fork":{{{fork.ToString().ToLowerInvariant()}}},
             "clone_url":"https://github.com/{{{fullName}}}.git","ssh_url":"git@github.com:{{{fullName}}}.git","pushed_at":"2026-10-01T10:00:00Z","owner":{"login":"{{{fullName.Split('/')[0]}}}"}}
            """;
    }

    [Fact]
    public async Task GitHub_repositories_are_read_page_after_page_through_the_link_header()
    {
        var handler = new Handler(r => r.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, $"[{GitHubRepo("atlas/web")}]", null)
            : (HttpStatusCode.OK, $"[{GitHubRepo("atlas/api", isPrivate: true, description: "The API")},{GitHubRepo("me/fork", fork: true)}]",
                "<https://api.github.com/user/repos?page=2>; rel=\"next\", <https://api.github.com/user/repos?page=2>; rel=\"last\""));
        var client = new GitHubClient(new HttpClient(handler), "client-1");

        var list = Listed(await client.ListRepositoriesAsync("t", Ct));

        Assert.False(list.IsTruncated);
        Assert.Equal(["atlas/api", "me/fork", "atlas/web"], list.Items.Select(i => i.FullName));
        var api = list.Items[0];
        Assert.True(api.IsPrivate);
        Assert.Equal("The API", api.Description);
        Assert.Equal("https://github.com/atlas/api.git", api.HttpsUrl);
        Assert.Equal("git@github.com:atlas/api.git", api.SshUrl);
        Assert.Equal("atlas", api.Owner);
        Assert.NotNull(api.UpdatedAt);
        Assert.True(list.Items[1].IsFork);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("affiliation=owner,collaborator,organization_member", handler.Urls[0].Query, StringComparison.Ordinal);
        Assert.Contains("sort=pushed", handler.Urls[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_stops_after_ten_pages_and_says_the_list_is_truncated()
    {
        var handler = new Handler(_ => (HttpStatusCode.OK, $"[{GitHubRepo("atlas/api")}]", "<https://api.github.com/user/repos?page=2>; rel=\"next\""));
        var client = new GitHubClient(new HttpClient(handler), "client-1");

        var list = Listed(await client.ListRepositoriesAsync("t", Ct));

        Assert.True(list.IsTruncated);
        Assert.Equal(10, handler.Urls.Count);
    }

    [Fact]
    public async Task GitHub_does_not_follow_a_next_page_outside_the_api()
    {
        var handler = new Handler(_ => (HttpStatusCode.OK, $"[{GitHubRepo("atlas/api")}]", "<https://evil.example/repos?page=2>; rel=\"next\""));
        var client = new GitHubClient(new HttpClient(handler), "client-1");

        var list = Listed(await client.ListRepositoriesAsync("t", Ct));

        Assert.Single(list.Items);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task GitHub_refused_token_is_an_authentication_failure()
    {
        var handler = new Handler(_ => (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""", null));
        var client = new GitHubClient(new HttpClient(handler), "client-1");

        Assert.IsType<AuthenticationFailed>((await client.ListRepositoriesAsync("t", Ct)).Value);
    }

    private static (HttpStatusCode, string, string?) AzureAnswer(HttpRequestMessage r)
    {
        var url = r.RequestUri!;
        if (url.Host == "app.vssps.visualstudio.com" && url.AbsolutePath.EndsWith("/profiles/me", StringComparison.Ordinal))
            return (HttpStatusCode.OK, """{"id":"me-1","displayName":"Me","emailAddress":"me@example.com"}""", null);
        if (url.Host == "app.vssps.visualstudio.com")
            return (HttpStatusCode.OK, """{"count":3,"value":[{"accountName":"contoso"},{"accountName":"fabrikam"},{"accountName":"locked"}]}""", null);
        return url.AbsolutePath.Split('/')[1] switch
        {
            "contoso" => (HttpStatusCode.OK, """
                {"value":[
                 {"name":"api","remoteUrl":"https://contoso@dev.azure.com/contoso/Atlas/_git/api","sshUrl":"git@ssh.dev.azure.com:v3/contoso/Atlas/api","project":{"name":"Atlas","visibility":"private"}},
                 {"name":"old","remoteUrl":"https://contoso@dev.azure.com/contoso/Atlas/_git/old","isDisabled":true,"project":{"name":"Atlas","visibility":"private"}},
                 {"name":"site","remoteUrl":"https://contoso@dev.azure.com/contoso/Open/_git/site","project":{"name":"Open","visibility":"public"}}]}
                """, null),
            "fabrikam" => (HttpStatusCode.OK, """
                {"value":[{"name":"shop","remoteUrl":"https://fabrikam@dev.azure.com/fabrikam/Retail/_git/shop","isFork":true,"project":{"name":"Retail","visibility":"private"}}]}
                """, null),
            _ => (HttpStatusCode.Forbidden, """{"message":"denied"}""", null),
        };
    }

    [Fact]
    public async Task Azure_repositories_come_from_every_organization_and_skip_disabled_ones()
    {
        var handler = new Handler(AzureAnswer);
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var list = Listed(await client.ListRepositoriesAsync("pat", Ct));

        // An organization that refuses the token ("locked") does not hide the others.
        Assert.Equal(["contoso/Atlas/api", "contoso/Open/site", "fabrikam/Retail/shop"], list.Items.Select(i => i.FullName));
        var api = list.Items[0];
        Assert.True(api.IsPrivate);
        Assert.Equal("git@ssh.dev.azure.com:v3/contoso/Atlas/api", api.SshUrl);
        Assert.Equal("contoso/Atlas", api.Owner);
        Assert.False(list.Items[1].IsPrivate);
        Assert.True(list.Items[2].IsFork);
        Assert.Contains(handler.Urls, u => u.Query.Contains("memberId=me-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Azure_failure_of_every_organization_is_reported()
    {
        var handler = new Handler(r => r.RequestUri!.Host == "app.vssps.visualstudio.com"
            ? AzureAnswer(r) switch
            {
                (_, var body, _) when body.Contains("count", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"value":[{"accountName":"locked"}]}""", null),
                var other => other,
            }
            : (HttpStatusCode.Forbidden, """{"message":"denied"}""", null));
        var client = new AzureDevOpsClient(new HttpClient(handler));

        Assert.IsType<AuthenticationFailed>((await client.ListRepositoriesAsync("pat", Ct)).Value);
    }

    [Fact]
    public async Task Azure_token_limited_to_one_organization_cannot_list_organizations()
    {
        var handler = new Handler(_ => (HttpStatusCode.Unauthorized, "{}", null));
        var client = new AzureDevOpsClient(new HttpClient(handler));

        Assert.IsType<AuthenticationFailed>((await client.ListRepositoriesAsync("pat", Ct)).Value);
    }
}
