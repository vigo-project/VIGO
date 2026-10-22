using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

// Completion after #, ! and @ in a comment (J6.12), GitHub side.
public sealed class GitHubReferenceSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HostRepository Atlas = new(HostingProvider.GitHub, "github.com", "atlas", "api");

    private sealed class Handler(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public List<Uri> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer(request), Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private static (GitHubClient Client, Handler Handler) Create(Func<HttpRequestMessage, string> answer)
    {
        var handler = new Handler(answer);
        return (new GitHubClient(new HttpClient(handler), "client-1"), handler);
    }

    private static IReadOnlyList<ReferenceSuggestion> Items(GitResult<IReadOnlyList<ReferenceSuggestion>> result) =>
        Assert.IsType<Success<IReadOnlyList<ReferenceSuggestion>>>(result.Value).Value;

    [Fact]
    public async Task Issues_and_pull_requests_are_searched_by_title_in_the_repository()
    {
        var (client, handler) = Create(_ => """
            {"items":[{"number":7,"title":"Fix the cache","state":"open"},{"number":9,"title":"Cache PR","state":"closed","pull_request":{"url":"x"}}]}
            """);

        var items = Items(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.Issue, "cache", Ct));

        Assert.Equal([("Fix the cache", "#7"), ("Cache PR", "#9")], items.Select(i => (i.Label, i.Insert)));
        Assert.Equal("#7 · issue · open", items[0].Detail);
        Assert.Equal("#9 · PR · closed", items[1].Detail);
        var query = Uri.UnescapeDataString(Assert.Single(handler.Urls).Query);
        Assert.Contains("repo:atlas/api cache in:title", query, StringComparison.Ordinal);
        Assert.DoesNotContain("is:pr", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_requests_are_searched_with_the_pull_request_filter()
    {
        var (client, handler) = Create(_ => """{"items":[]}""");

        Assert.Empty(Items(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.PullRequest, string.Empty, Ct)));

        var query = Uri.UnescapeDataString(Assert.Single(handler.Urls).Query);
        Assert.Contains("repo:atlas/api is:pr", query, StringComparison.Ordinal);
        Assert.DoesNotContain("in:title", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typed_number_is_read_directly_and_comes_first()
    {
        var (client, _) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/issues/7", StringComparison.Ordinal)
            ? """{"number":7,"title":"Fix the cache","state":"open"}"""
            : """{"items":[{"number":70,"title":"Seventy","state":"open"}]}""");

        var items = Items(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.Issue, "7", Ct));

        Assert.Equal(["#7", "#70"], items.Select(i => i.Insert));
    }

    [Fact]
    public async Task People_are_the_assignable_accounts_filtered_by_login_and_written_as_mentions()
    {
        var (client, handler) = Create(_ => """[{"login":"octocat"},{"login":"hubot"}]""");

        var items = Items(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.Person, "hub", Ct));

        Assert.Equal("@hubot", Assert.Single(items).Insert);
        // Kept a few minutes: the next keystroke does not ask again.
        Items(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.Person, "octo", Ct));
        Assert.Single(handler.Urls);
    }
}
