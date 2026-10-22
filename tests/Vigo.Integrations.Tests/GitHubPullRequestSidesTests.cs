using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

/// <summary>Whole old and new versions of a pull request file, for the side-by-side diff.</summary>
public sealed class GitHubPullRequestSidesTests
{
    private static readonly HostRepository Atlas = new(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly PullRequest Listed =
        new(7, "Fix cache", "octocat", "fix/cache", null, "main", false, Now, "https://github.com/atlas/atlas-api/pull/7", HeadSha: "headsha");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Api = "https://api.github.com/repos/atlas/atlas-api";

    private sealed class Routes : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Answers { get; } = [];

        public List<(string Url, string Accept)> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add((url, request.Headers.Accept.ToString()));
            var (status, body) = Answers.TryGetValue(url, out var known) ? known : (HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain"), RequestMessage = request });
        }
    }

    private static (GitHubClient Client, Routes Routes) Create()
    {
        var routes = new Routes();
        routes.Answers[Api + "/compare/main...headsha?per_page=1"] = (HttpStatusCode.OK, """{"merge_base_commit":{"sha":"basesha"}}""");
        routes.Answers[Api + "/contents/src/a.cs?ref=basesha"] = (HttpStatusCode.OK, "old\nsame\n");
        routes.Answers[Api + "/contents/src/a.cs?ref=headsha"] = (HttpStatusCode.OK, "new\nsame\n");
        return (new GitHubClient(new HttpClient(routes), "client-1"), routes);
    }

    [Fact]
    public async Task A_modified_file_is_read_at_the_merge_base_and_at_the_head()
    {
        var (client, routes) = Create();

        var result = await client.GetPullRequestFileSidesAsync("tok", Atlas, Listed, new PullRequestFile("src/a.cs", null, FileChangeKind.Modified, 1, 1, "@@"), Ct);

        var sides = Assert.IsType<Success<DiffSides>>(result.Value).Value;
        Assert.Equal(("old\nsame\n", "new\nsame\n"), (sides.Original, sides.Modified));
        Assert.All(routes.Requested.Where(r => r.Url.Contains("/contents/", StringComparison.Ordinal)),
            r => Assert.Contains("raw", r.Accept, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_added_file_has_no_old_side_and_is_not_read_at_the_base()
    {
        var (client, routes) = Create();

        var result = await client.GetPullRequestFileSidesAsync("tok", Atlas, Listed, new PullRequestFile("src/a.cs", null, FileChangeKind.Added, 2, 0, "@@"), Ct);

        var sides = Assert.IsType<Success<DiffSides>>(result.Value).Value;
        Assert.Equal(string.Empty, sides.Original);
        Assert.Equal("new\nsame\n", sides.Modified);
        Assert.DoesNotContain(routes.Requested, r => r.Url.Contains("basesha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_missing_on_one_side_reads_as_empty()
    {
        var (client, routes) = Create();
        routes.Answers.Remove(Api + "/contents/src/a.cs?ref=headsha");

        var result = await client.GetPullRequestFileSidesAsync("tok", Atlas, Listed, new PullRequestFile("src/a.cs", null, FileChangeKind.Modified, 1, 1, "@@"), Ct);

        Assert.Equal(string.Empty, Assert.IsType<Success<DiffSides>>(result.Value).Value.Modified);
    }

    [Fact]
    public async Task Without_a_head_commit_nothing_is_requested()
    {
        var (client, routes) = Create();

        var result = await client.GetPullRequestFileSidesAsync("tok", Atlas, Listed with { HeadSha = null }, new PullRequestFile("src/a.cs", null, FileChangeKind.Modified, 1, 1, "@@"), Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.Empty(routes.Requested);
    }
}
