using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

/// <summary>Detail of a pull request read from GitHub (J6.9a): description, conversation, commits, files, paging.</summary>
public sealed class GitHubPullRequestDetailTests
{
    private static readonly HostRepository Atlas = new(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly PullRequest Listed = new(7, "Fix cache", "octocat", "fix/cache", null, "main", false, Now, "https://github.com/atlas/atlas-api/pull/7");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Routes : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Json, string? Link)> Answers { get; } = [];

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (status, json, link) = Answers.TryGetValue(url, out var known) ? known : (HttpStatusCode.NotFound, "{}", null);
            var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (link is not null) response.Headers.TryAddWithoutValidation("Link", link);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private const string Api = "https://api.github.com/repos/atlas/atlas-api";
    private const string Pull = Api + "/pulls/7";

    private static (GitHubClient Client, Routes Routes) Create()
    {
        var routes = new Routes();
        routes.Answers[Pull] = (HttpStatusCode.OK, """
            {"number":7,"title":"Fix cache","user":{"login":"octocat"},"head":{"ref":"fix/cache","sha":"abc"},"base":{"ref":"main"},
             "draft":false,"updated_at":"2026-10-12T08:00:00Z","html_url":"https://github.com/atlas/atlas-api/pull/7",
             "body":"Fixes **the cache**.","state":"open","merged":false,"labels":[{"name":"bug"},{"name":"cache"}]}
            """, null);
        routes.Answers[Api + "/issues/7/comments?per_page=100"] = (HttpStatusCode.OK, """
            [{"user":{"login":"ada"},"body":"Looks good","created_at":"2026-10-12T08:30:00Z"}]
            """, null);
        routes.Answers[Pull + "/reviews?per_page=100"] = (HttpStatusCode.OK, """
            [{"user":{"login":"grace"},"state":"APPROVED","submitted_at":"2026-10-12T08:45:00Z","body":"Ship it"},
             {"user":{"login":"linus"},"state":"COMMENTED","submitted_at":"2026-10-12T08:20:00Z","body":""},
             {"user":{"login":"me"},"state":"PENDING","submitted_at":null,"body":"draft"}]
            """, null);
        routes.Answers[Pull + "/comments?per_page=100"] = (HttpStatusCode.OK, """
            [{"user":{"login":"linus"},"body":"Why a lock here?","created_at":"2026-10-12T08:21:00Z","path":"src/cache.cs","line":12,"original_line":10,"diff_hunk":"@@ -1 +1 @@"}]
            """, null);
        routes.Answers[Pull + "/commits?per_page=100"] = (HttpStatusCode.OK, """
            [{"sha":"abc1234def","commit":{"message":"Fix the cache\n\nLong text","author":{"name":"Octo Cat","date":"2026-10-11T10:00:00Z"}},"author":{"login":"octocat"}}]
            """, null);
        routes.Answers[Pull + "/files?per_page=100"] = (HttpStatusCode.OK, """
            [{"filename":"src/cache.cs","status":"modified","additions":3,"deletions":1,"patch":"@@ -1,2 +1,4 @@\n a\n+b"},
             {"filename":"logo.png","status":"added","additions":0,"deletions":0},
             {"filename":"new.cs","previous_filename":"old.cs","status":"renamed","additions":0,"deletions":0}]
            """, null);
        return (new GitHubClient(new HttpClient(routes), "client-1"), routes);
    }

    [Fact]
    public async Task Reads_the_description_the_state_and_the_labels()
    {
        var (client, _) = Create();

        var result = await client.GetPullRequestDetailAsync("t", Atlas, Listed, Ct);

        var detail = Assert.IsType<Success<PullRequestDetail>>(result.Value).Value;
        Assert.Equal("Fixes **the cache**.", detail.Body);
        Assert.Equal(PullRequestState.Open, detail.State);
        Assert.Equal(["bug", "cache"], detail.Labels);
        Assert.Equal("fix/cache", detail.Summary.HeadBranch);
        Assert.False(detail.IsTruncated);
        Assert.True(client.SupportsPullRequestDetail);
    }

    [Theory]
    [InlineData("closed", false, PullRequestState.Closed)]
    [InlineData("closed", true, PullRequestState.Merged)]
    public async Task A_closed_pull_request_is_merged_or_not(string state, bool merged, PullRequestState expected)
    {
        var (client, routes) = Create();
        routes.Answers[Pull] = (HttpStatusCode.OK, $$"""
            {"number":7,"title":"T","user":{"login":"o"},"draft":false,"updated_at":"2026-10-12T08:00:00Z","html_url":"u","state":"{{state}}","merged":{{(merged ? "true" : "false")}}}
            """, null);

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, Listed, Ct)).Value).Value;

        Assert.Equal(expected, detail.State);
    }

    [Fact]
    public async Task The_conversation_mixes_comments_reviews_and_line_comments_oldest_first()
    {
        var (client, _) = Create();

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, Listed, Ct)).Value).Value;

        // The empty "commented" review and the pending draft are left out.
        Assert.Collection(detail.Timeline,
            e =>
            {
                Assert.Equal(TimelineKind.ReviewComment, e.Kind);
                Assert.Equal(("linus", "src/cache.cs", 12), (e.Author, e.Path, e.Line));
            },
            e => Assert.Equal((TimelineKind.Comment, "ada", "Looks good"), (e.Kind, e.Author, e.Body)),
            e => Assert.Equal((TimelineKind.Review, "grace", ReviewVerdict.Approved, "Ship it"), (e.Kind, e.Author, e.Verdict, e.Body)));
    }

    [Fact]
    public async Task Commits_and_files_are_read_with_their_patch()
    {
        var (client, _) = Create();

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, Listed, Ct)).Value).Value;

        var commit = Assert.Single(detail.Commits);
        Assert.Equal(("abc1234def", "Fix the cache", "octocat"), (commit.Sha, commit.Subject, commit.Author));
        Assert.Equal(["src/cache.cs", "logo.png", "new.cs"], detail.Files.Select(f => f.Path));
        Assert.Equal([FileChangeKind.Modified, FileChangeKind.Added, FileChangeKind.Renamed], detail.Files.Select(f => f.Kind));
        Assert.Equal("old.cs", detail.Files[2].OldPath);
        Assert.Null(detail.Files[1].Patch);
        Assert.Equal((3, 1), (detail.Additions, detail.Deletions));
    }

    [Fact]
    public async Task A_list_follows_the_next_link_and_is_cut_at_the_page_limit()
    {
        var (client, routes) = Create();
        // Every page of the files points to another one: the reading stops at the limit and says so.
        routes.Answers[Pull + "/files?per_page=100"] = (HttpStatusCode.OK, """[{"filename":"a.cs","status":"modified","additions":1,"deletions":0}]""",
            $"<{Pull}/files?per_page=100>; rel=\"next\", <{Pull}/files?page=99>; rel=\"last\"");

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, Listed, Ct)).Value).Value;

        Assert.True(detail.IsTruncated);
        Assert.Equal(10, detail.Files.Count);
    }

    [Fact]
    public async Task Pages_are_joined()
    {
        var (client, routes) = Create();
        routes.Answers[Api + "/issues/7/comments?per_page=100"] = (HttpStatusCode.OK,
            """[{"user":{"login":"a"},"body":"one","created_at":"2026-10-12T08:00:00Z"}]""", $"<{Api}/issues/7/comments?page=2>; rel=\"next\"");
        routes.Answers[Api + "/issues/7/comments?page=2"] = (HttpStatusCode.OK,
            """[{"user":{"login":"b"},"body":"two","created_at":"2026-10-12T08:10:00Z"}]""", null);

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, Listed, Ct)).Value).Value;

        Assert.Equal(["one", "two"], detail.Timeline.Where(t => t.Kind == TimelineKind.Comment).Select(t => t.Body));
        Assert.False(detail.IsTruncated);
    }

    [Fact]
    public async Task A_missing_pull_request_is_a_failure()
    {
        var (client, routes) = Create();
        routes.Answers[Pull] = (HttpStatusCode.NotFound, "{}", null);

        var result = await client.GetPullRequestDetailAsync("t", Atlas, Listed, Ct);

        Assert.IsType<Failure>(result.Value);
    }

    [Fact]
    public async Task A_refused_token_is_an_authentication_failure()
    {
        var (client, routes) = Create();
        routes.Answers[Pull + "/files?per_page=100"] = (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""", null);

        var result = await client.GetPullRequestDetailAsync("t", Atlas, Listed, Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
    }

    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task A_refused_token_is_logged_without_the_token_and_the_message_says_how_to_recover()
    {
        var routes = new Routes();
        routes.Answers[Pull] = (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""", null);
        var logger = new CaptureLogger();
        var client = new GitHubClient(new HttpClient(routes), "client-1", logger: logger);

        var result = await client.GetPullRequestDetailAsync("ghp_SECRET", Atlas, Listed, Ct);

        Assert.Contains("reconnectez-vous", Assert.IsType<AuthenticationFailed>(result.Value).Message, StringComparison.Ordinal);
        var line = Assert.Single(logger.Lines);
        Assert.Contains("401", line, StringComparison.Ordinal);
        Assert.Contains("/pulls/7", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_SECRET", line, StringComparison.Ordinal);
    }
}
