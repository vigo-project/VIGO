using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

/// <summary>Review of a GitHub pull request on its diff (J6.9e): threads, replies, resolving, a review sent with its comments.</summary>
public sealed class GitHubPullRequestReviewTests
{
    private static readonly HostRepository Atlas = new(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly PullRequest Listed = new(7, "Fix cache", "octocat", "fix/cache", null, "main", false, Now, "https://github.com/atlas/atlas-api/pull/7");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Api = "https://api.github.com/repos/atlas/atlas-api";

    private sealed record Call(HttpMethod Method, string Url, string Body);

    private const string Header = """
        {"number":7,"title":"T","user":{"login":"o"},"draft":false,"updated_at":"2026-10-12T08:00:00Z","html_url":"u","state":"open","merged":false}
        """;

    // Answers every request with <paramref name="json"/>, except what a full read of the pull request asks beside the graph API:
    // the header and the (empty) lists, so that the threads can be tested through GetPullRequestDetailAsync.
    private sealed class Recorder(HttpStatusCode status, string json, string? readJson = null) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.ToString();
            Calls.Add(new Call(request.Method, url, body));
            var answer = readJson is null || url.EndsWith("/graphql", StringComparison.Ordinal) ? json
                : url.Contains("?per_page", StringComparison.Ordinal) ? "[]" : readJson;
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }

    private static (GitHubClient Client, Recorder Recorder) Create(HttpStatusCode status = HttpStatusCode.OK, string json = "{}", string? readJson = null)
    {
        var recorder = new Recorder(status, json, readJson);
        return (new GitHubClient(new HttpClient(recorder), "client-1"), recorder);
    }

    [Fact]
    public async Task Threads_are_read_through_the_graph_api_with_their_comments_and_state()
    {
        var (client, recorder) = Create(readJson: Header, json: """
            {"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[
              {"id":"PRRT_1","isResolved":false,"isOutdated":false,"path":"src/cache.cs","line":12,"originalLine":10,"diffSide":"RIGHT",
               "comments":{"nodes":[{"databaseId":11,"body":"Why a lock?","createdAt":"2026-10-12T08:21:00Z","author":{"login":"linus"}},
                                    {"databaseId":12,"body":"Thread safety","createdAt":"2026-10-12T08:25:00Z","author":{"login":"octocat"}}]}},
              {"id":"PRRT_2","isResolved":true,"isOutdated":true,"path":"a.cs","line":null,"originalLine":4,"diffSide":"LEFT",
               "comments":{"nodes":[{"databaseId":13,"body":"Old","createdAt":"2026-10-12T08:00:00Z","author":null}]}}]}}}}}
            """);

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("tok", Atlas, Listed, Ct)).Value).Value;

        var graph = Assert.Single(recorder.Calls, c => c.Url == "https://api.github.com/graphql");
        Assert.Contains("reviewThreads", graph.Body, StringComparison.Ordinal);
        Assert.Contains("\"owner\":\"atlas\"", graph.Body, StringComparison.Ordinal);
        Assert.Contains("\"number\":7", graph.Body, StringComparison.Ordinal);
        Assert.Equal(2, detail.ReviewThreads.Count);
        var open = detail.ReviewThreads[0];
        Assert.Equal(("PRRT_1", "src/cache.cs", 12, ReviewSide.New, false, false), (open.Id, open.Path, open.Line, open.Side, open.IsResolved, open.IsOutdated));
        Assert.Equal([11L, 12L], open.Comments.Select(c => c.Id));
        Assert.Equal("linus", open.Comments[0].Author);
        var old = detail.ReviewThreads[1];
        Assert.Equal((ReviewSide.Old, true, true, 4), (old.Side, old.IsResolved, old.IsOutdated, old.Line));
        Assert.Equal("?", old.Comments[0].Author);
    }

    [Fact]
    public async Task A_pull_request_is_still_read_when_its_threads_cannot_be()
    {
        var (client, _) = Create(json: """{"data":null}""", readJson: Header);

        var result = await client.GetPullRequestDetailAsync("tok", Atlas, Listed, Ct);

        Assert.Empty(Assert.IsType<Success<PullRequestDetail>>(result.Value).Value.ReviewThreads);
    }

    private static ReviewThread Thread(string id) => new(id, "a.cs", 3, ReviewSide.New, false, false, []);

    [Fact]
    public async Task Replying_posts_to_the_comment_and_returns_the_stored_reply()
    {
        var (client, recorder) = Create(HttpStatusCode.Created, """{"id":77,"user":{"login":"octocat"},"body":"Done","created_at":"2026-10-12T09:00:00Z"}""");

        var thread = new ReviewThread("PRRT_1", "a.cs", 3, ReviewSide.New, false, false, [new ReviewThreadComment(11, "linus", "Why?", DateTimeOffset.UnixEpoch)]);

        var result = await client.ReplyToReviewThreadAsync("tok", Atlas, Listed, thread, "Done", Ct);

        var reply = Assert.IsType<Success<ReviewThreadComment>>(result.Value).Value;
        Assert.Equal((77L, "octocat", "Done"), (reply.Id, reply.Author, reply.Body));
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Post, Api + "/pulls/7/comments/11/replies"), (call.Method, call.Url));
        Assert.Contains("\"body\":\"Done\"", call.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "resolveReviewThread")]
    [InlineData(false, "unresolveReviewThread")]
    public async Task Resolving_a_thread_sends_the_matching_mutation(bool resolved, string mutation)
    {
        var (client, recorder) = Create(json: """{"data":{}}""");

        var result = await client.SetReviewThreadResolvedAsync("tok", Atlas, Listed, Thread("PRRT_1"), resolved, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal("https://api.github.com/graphql", call.Url);
        Assert.Contains(mutation + "(", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"PRRT_1\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_resolution_is_a_failure()
    {
        var (client, _) = Create(json: """{"errors":[{"message":"Resource not accessible"}]}""");

        var result = await client.SetReviewThreadResolvedAsync("tok", Atlas, Listed, Thread("PRRT_1"), true, Ct);

        Assert.Equal("Resource not accessible", Assert.IsType<Failure>(result.Value).Message);
    }

    [Theory]
    [InlineData(ReviewDecision.Approve, "APPROVE")]
    [InlineData(ReviewDecision.RequestChanges, "REQUEST_CHANGES")]
    [InlineData(ReviewDecision.Comment, "COMMENT")]
    public async Task A_review_is_sent_in_one_request_with_its_decision_and_line_comments(ReviewDecision decision, string name)
    {
        var (client, recorder) = Create();
        NewReviewComment[] comments =
        [
            new("src/cache.cs", 12, ReviewSide.New, "Use a lock"),
            new("src/cache.cs", 4, ReviewSide.Old, "Why remove?"),
        ];

        var result = await client.SubmitReviewAsync("tok", Atlas, Listed, decision, "Overall fine", comments, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Post, Api + "/pulls/7/reviews"), (call.Method, call.Url));
        Assert.Contains($"\"event\":\"{name}\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"Overall fine\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"src/cache.cs\",\"line\":12,\"side\":\"RIGHT\",\"body\":\"Use a lock\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"line\":4,\"side\":\"LEFT\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comment_on_several_lines_names_the_first_one()
    {
        var (client, recorder) = Create();

        await client.SubmitReviewAsync("tok", Atlas, Listed, ReviewDecision.Comment, string.Empty,
            [new NewReviewComment("src/cache.cs", 14, ReviewSide.New, "This block", StartLine: 12, StartColumn: 2, EndColumn: 8)], Ct);

        var call = Assert.Single(recorder.Calls);
        Assert.Contains("\"line\":14,\"side\":\"RIGHT\",\"body\":\"This block\",\"start_line\":12,\"start_side\":\"RIGHT\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thread_on_several_lines_keeps_its_first_line()
    {
        var (client, _) = Create(readJson: Header, json: """
            {"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[
              {"id":"PRRT_1","isResolved":false,"isOutdated":false,"path":"src/cache.cs","line":14,"originalLine":14,"startLine":12,"originalStartLine":12,"diffSide":"RIGHT",
               "comments":{"nodes":[{"databaseId":11,"body":"Why?","createdAt":"2026-10-12T08:21:00Z","author":{"login":"linus"}}]}}]}}}}}
            """);

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("tok", Atlas, Listed, Ct)).Value).Value;

        var thread = Assert.Single(detail.ReviewThreads);
        Assert.Equal((14, 12), (thread.Line, thread.StartLine));
    }

    [Fact]
    public async Task A_review_without_text_or_comments_leaves_those_fields_out()
    {
        var (client, recorder) = Create();

        await client.SubmitReviewAsync("tok", Atlas, Listed, ReviewDecision.Approve, "  ", [], Ct);

        var body = Assert.Single(recorder.Calls).Body;
        Assert.DoesNotContain("\"body\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"comments\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_review_reports_the_reason()
    {
        var (client, _) = Create(HttpStatusCode.UnprocessableEntity, """{"message":"Can not approve your own pull request"}""");

        var result = await client.SubmitReviewAsync("tok", Atlas, Listed, ReviewDecision.Approve, string.Empty, [], Ct);

        Assert.IsType<Failure>(result.Value);
    }
}
