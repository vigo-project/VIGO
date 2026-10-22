using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

public sealed class GitHubClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        /// <summary>ETag of every answer (a revalidation answers 304 itself, from the responder).</summary>
        public string? ETag { get; set; }

        public Dictionary<string, string> Headers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, json) = respond(request);
            var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (ETag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag);
            foreach (var (name, value) in Headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }
    }

    private static (GitHubClient Client, FakeHandler Handler) Create(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond, string? clientId = "client-1")
    {
        var handler = new FakeHandler(respond);
        return (new GitHubClient(new HttpClient(handler), clientId, new FakeClock(Now)), handler);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Reads_the_account_behind_a_token_and_sends_it_as_a_bearer()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"login":"octocat","name":"The Octocat","avatar_url":"https://avatars.githubusercontent.com/u/1"}"""));

        var result = await client.GetUserAsync("ghp_secret", Ct);

        var user = Assert.IsType<Success<HostUser>>(result.Value).Value;
        Assert.Equal(new HostUser("octocat", "The Octocat", "https://avatars.githubusercontent.com/u/1"), user);
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("https://api.github.com/user", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("ghp_secret", request.Headers.Authorization?.Parameter);
        Assert.NotEmpty(request.Headers.UserAgent);
    }

    [Fact]
    public async Task A_blank_name_is_not_kept()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """{"login":"octocat","name":" ","avatar_url":null}"""));

        var user = Assert.IsType<Success<HostUser>>((await client.GetUserAsync("t", Ct)).Value).Value;

        Assert.Null(user.DisplayName);
        Assert.Null(user.AvatarUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refused_token_is_an_authentication_failure(HttpStatusCode status)
    {
        var (client, _) = Create(_ => (status, """{"message":"Bad credentials"}"""));

        Assert.IsType<AuthenticationFailed>((await client.GetUserAsync("bad", Ct)).Value);
    }

    [Fact]
    public async Task Other_statuses_and_unreachable_servers_are_failures()
    {
        var (server, _) = Create(_ => (HttpStatusCode.InternalServerError, "{}"));
        Assert.IsType<Failure>((await server.GetUserAsync("t", Ct)).Value);

        var unreachable = new GitHubClient(new HttpClient(new ThrowingHandler()), "id");
        Assert.IsType<Failure>((await unreachable.GetUserAsync("t", Ct)).Value);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route");
    }

    [Fact]
    public async Task Starts_the_device_flow_with_the_client_id_and_scopes()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK,
            """{"device_code":"dc","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}"""));

        var code = Assert.IsType<Success<DeviceCode>>((await client.StartDeviceFlowAsync(Ct)).Value).Value;

        Assert.Equal("WDJB-MJHT", code.UserCode);
        Assert.Equal("dc", code.Secret);
        Assert.Equal("https://github.com/login/device", code.VerificationUrl);
        Assert.Equal(Now.AddSeconds(900), code.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(5), code.Interval);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://github.com/login/device/code", request.RequestUri?.ToString());
        Assert.Contains("client_id=client-1", body, StringComparison.Ordinal);
        Assert.Contains("scope=repo+read%3Auser", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_client_id_the_device_flow_is_unavailable()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, "{}"), clientId: null);

        Assert.False(client.SupportsDeviceFlow);
        Assert.IsType<Failure>((await client.StartDeviceFlowAsync(Ct)).Value);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"error":"authorization_pending"}""", null, false)]
    [InlineData("""{"error":"slow_down","interval":10}""", null, true)]
    [InlineData("""{"access_token":"gho_abc","token_type":"bearer","scope":"repo"}""", "gho_abc", false)]
    public async Task Polling_reports_pending_slow_down_and_the_token(string json, string? token, bool slowDown)
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, json));

        var poll = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value).Value;

        Assert.Equal(new DevicePoll(token, slowDown), poll);
        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("device_code=dc", body, StringComparison.Ordinal);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":"expired_token"}""")]
    [InlineData("""{"error":"access_denied"}""")]
    [InlineData("""{"error":"incorrect_client_credentials","error_description":"nope"}""")]
    public async Task Polling_ends_with_a_failure_when_the_code_expired_or_was_refused(string json)
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, json));

        Assert.IsType<Failure>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value);
    }

    // ---- Pull requests and issues (J6.2) ----

    private static readonly HostRepository Atlas = new(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");

    private const string PullJson = """
        [{"number":12,"title":"Fix cache","user":{"login":"octocat"},"head":{"ref":"fix/cache","repo":{"full_name":"atlas/atlas-api"}},
          "base":{"ref":"main"},"draft":false,"updated_at":"2026-10-08T10:00:00Z","html_url":"https://github.com/atlas/atlas-api/pull/12"},
         {"number":9,"title":"Idea","user":null,"head":{"ref":"idea","repo":{"full_name":"someone/atlas-api"}},"base":{"ref":"main"},"draft":true,
          "updated_at":"2026-10-07T10:00:00Z","html_url":"https://github.com/atlas/atlas-api/pull/9"}]
        """;

    private const string OnePullJson = """
        {"number":12,"title":"Fix cache","user":{"login":"octocat"},"head":{"ref":"fix/cache","repo":{"full_name":"atlas/atlas-api"}},
         "base":{"ref":"main"},"draft":true,"updated_at":"2026-10-08T10:00:00Z","html_url":"https://github.com/atlas/atlas-api/pull/12"}
        """;

    [Theory]
    [InlineData("https://github.com/atlas/atlas-api.git", "atlas/atlas-api")]
    [InlineData("git@github.com:atlas/atlas-api.git", "atlas/atlas-api")]
    [InlineData("https://GitHub.com/atlas/atlas-api", "atlas/atlas-api")]
    [InlineData("https://github.com/atlas", null)]
    [InlineData("https://github.com/atlas/atlas-api/extra", null)]
    [InlineData("https://gitlab.com/atlas/atlas-api.git", null)]
    public void Only_github_com_urls_with_an_owner_and_a_name_are_repositories(string url, string? expected)
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, "{}"));

        Assert.Equal(expected, client.ParseRepository(RemoteUrl.TryParse(url)!)?.FullName);
    }

    [Fact]
    public async Task The_status_of_a_pull_request_merges_check_runs_commit_statuses_and_reviews()
    {
        var (client, handler) = Create(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/check-runs", StringComparison.Ordinal) => """
                {"check_runs":[
                  {"name":"build","status":"completed","conclusion":"success"},
                  {"name":"lint","status":"completed","conclusion":"skipped"},
                  {"name":"e2e","status":"in_progress","conclusion":null}]}
                """,
            var p when p.EndsWith("/status", StringComparison.Ordinal) => """{"statuses":[{"context":"ci/legacy","state":"failure"}]}""",
            _ => """
                [{"user":{"login":"ada"},"state":"APPROVED","submitted_at":"2026-10-08T10:00:00Z"},
                 {"user":{"login":"linus"},"state":"CHANGES_REQUESTED","submitted_at":"2026-10-08T11:00:00Z"},
                 {"user":{"login":"linus"},"state":"COMMENTED","submitted_at":"2026-10-08T12:00:00Z"}]
                """,
        }));
        var pull = new PullRequest(12, "Fix", "octocat", "fix", "atlas/atlas-api", "main", false, Now, "https://x", HeadSha: "abc123");

        var status = Assert.IsType<Success<PullRequestStatus>>((await client.GetPullRequestStatusAsync("t", Atlas, pull, cancellationToken: Ct)).Value).Value;

        Assert.Equal(CheckState.Failure, status.Checks);
        Assert.Equal(ReviewState.ChangesRequested, status.Review);
        Assert.Equal([("build", CheckState.Success), ("lint", CheckState.Skipped), ("e2e", CheckState.Pending), ("ci/legacy", CheckState.Failure)],
            status.Runs.Select(r => (r.Name, r.State)));
        Assert.Equal(
            ["/repos/atlas/atlas-api/commits/abc123/check-runs", "/repos/atlas/atlas-api/commits/abc123/status", "/repos/atlas/atlas-api/pulls/12/reviews"],
            handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath));
    }

    private const string DetailedRuns = """
        {"check_runs":[
          {"id":1,"name":"build","status":"completed","conclusion":"failure","html_url":"https://github.com/atlas/atlas-api/runs/1","started_at":"2026-10-15T10:00:00Z","completed_at":"2026-10-15T10:02:13Z","output":{"title":"2 tests failed"},"app":{"name":"GitHub Actions"}},
          {"id":2,"name":"build","status":"completed","conclusion":"success","html_url":"https://github.com/atlas/atlas-api/runs/2","started_at":"2026-10-15T11:00:00Z","completed_at":"2026-10-15T11:01:00Z","app":{"name":"GitHub Actions"}},
          {"id":3,"name":"deploy","status":"completed","conclusion":"cancelled","details_url":"https://ci.example/3","app":{"name":"Other"}},
          {"id":4,"name":"docs","status":"completed","conclusion":"neutral"}]}
        """;

    [Fact]
    public async Task A_re_run_check_keeps_its_most_recent_run_and_every_check_carries_its_details()
    {
        var (client, _) = Create(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/check-runs", StringComparison.Ordinal) => DetailedRuns,
            var p when p.EndsWith("/status", StringComparison.Ordinal) =>
                """{"statuses":[{"context":"ci/legacy","state":"pending","description":"Waiting","target_url":"https://legacy.example","created_at":"2026-10-15T09:00:00Z"}]}""",
            _ => "[]",
        }));
        var pull = new PullRequest(12, "Fix", "octocat", "fix", null, "main", false, Now, "https://x", HeadSha: "abc123");

        var status = Assert.IsType<Success<PullRequestStatus>>((await client.GetPullRequestStatusAsync("t", Atlas, pull, cancellationToken: Ct)).Value).Value;

        Assert.Equal([("build", CheckState.Success), ("deploy", CheckState.Canceled), ("docs", CheckState.Skipped), ("ci/legacy", CheckState.Pending)],
            status.Runs.Select(r => (r.Name, r.State)));
        var build = status.Runs[0];
        Assert.Equal("https://github.com/atlas/atlas-api/runs/2", build.Url);
        Assert.Equal(TimeSpan.FromMinutes(1), build.CompletedAt - build.StartedAt);
        Assert.Equal("https://ci.example/3", status.Runs[1].Url);
        Assert.Equal(CheckKind.Status, status.Runs[3].Kind);
        Assert.Equal("Waiting", status.Runs[3].Description);
        Assert.Equal("https://legacy.example", status.Runs[3].Url);
        Assert.Equal(CheckState.Failure, status.Checks); // a canceled run counts as a failure
    }

    [Fact]
    public async Task Required_checks_come_from_graphql_and_stay_unknown_when_it_fails()
    {
        string RespondGraphQl(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
            ? """
              {"data":{"repository":{"pullRequest":{"commits":{"nodes":[{"commit":{"statusCheckRollup":{"contexts":{"nodes":[
                {"__typename":"CheckRun","name":"build","isRequired":true},
                {"__typename":"CheckRun","name":"deploy","isRequired":false},
                {"__typename":"StatusContext","context":"ci/legacy","isRequired":true}]}}}}]}}}}}
              """
            : string.Empty;
        var pull = new PullRequest(12, "Fix", "octocat", "fix", null, "main", false, Now, "https://x", HeadSha: "abc123");

        var (client, handler) = Create(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/check-runs", StringComparison.Ordinal) => DetailedRuns,
            var p when p.EndsWith("/status", StringComparison.Ordinal) => """{"statuses":[{"context":"ci/legacy","state":"success"}]}""",
            var p when p.EndsWith("/graphql", StringComparison.Ordinal) => RespondGraphQl(r),
            _ => "[]",
        }));
        var status = Assert.IsType<Success<PullRequestStatus>>((await client.GetPullRequestStatusAsync("t", Atlas, pull, includeRequired: true, cancellationToken: Ct)).Value).Value;

        Assert.Equal([true, false, null, true], status.Runs.Select(r => r.IsRequired));
        var graphql = Assert.Single(handler.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal));
        Assert.Contains("isRequired(pullRequestNumber: $number)", graphql.Body, StringComparison.Ordinal);
        Assert.Contains("\"number\":12", graphql.Body, StringComparison.Ordinal);

        // GraphQL refused: the checks are still there, only the "required" mark is missing.
        var (failing, _) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/check-runs", StringComparison.Ordinal) => (HttpStatusCode.OK, DetailedRuns),
            var p when p.EndsWith("/status", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"statuses":[]}"""),
            var p when p.EndsWith("/graphql", StringComparison.Ordinal) => (HttpStatusCode.Forbidden, """{"message":"Resource not accessible"}"""),
            _ => (HttpStatusCode.OK, "[]"),
        });
        var unknown = Assert.IsType<Success<PullRequestStatus>>((await failing.GetPullRequestStatusAsync("t", Atlas, pull, includeRequired: true, cancellationToken: Ct)).Value).Value;

        Assert.Equal(3, unknown.Runs.Count);
        Assert.All(unknown.Runs, r => Assert.Null(r.IsRequired));
    }

    [Fact]
    public async Task The_left_panel_read_does_not_ask_graphql()
    {
        var (client, handler) = Create(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal) ? DetailedRuns : """{"statuses":[]}"""));
        var pull = new PullRequest(12, "Fix", "octocat", "fix", null, "main", false, Now, "https://x", HeadSha: "abc123");

        await client.GetPullRequestStatusAsync("t", Atlas, pull, cancellationToken: Ct);

        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""[{"user":{"login":"a"},"state":"CHANGES_REQUESTED","submitted_at":"2026-10-08T01:00:00Z"},{"user":{"login":"a"},"state":"DISMISSED","submitted_at":"2026-10-08T02:00:00Z"}]""", ReviewState.None)]
    [InlineData("""[{"user":{"login":"a"},"state":"CHANGES_REQUESTED","submitted_at":"2026-10-08T01:00:00Z"},{"user":{"login":"a"},"state":"APPROVED","submitted_at":"2026-10-08T02:00:00Z"}]""", ReviewState.Approved)]
    [InlineData("""[{"user":{"login":"a"},"state":"COMMENTED","submitted_at":"2026-10-08T01:00:00Z"}]""", ReviewState.None)]
    public async Task The_last_decisive_review_of_each_reviewer_counts(string reviews, ReviewState expected)
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, reviews));
        var pull = new PullRequest(12, "Fix", "octocat", "fix", null, "main", false, Now, "https://x");

        var status = Assert.IsType<Success<PullRequestStatus>>((await client.GetPullRequestStatusAsync("t", Atlas, pull, cancellationToken: Ct)).Value).Value;

        Assert.Equal(expected, status.Review);
        Assert.Equal(CheckState.None, status.Checks);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Open_pull_requests_are_read_with_their_source_branch_and_repository()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, PullJson));

        var result = await client.ListPullRequestsAsync("ghp_t", Atlas, Ct);

        var pulls = Assert.IsType<Success<IReadOnlyList<PullRequest>>>(result.Value).Value;
        Assert.Equal(["#12 fix/cache -> main", "#9 idea -> main"], pulls.Select(p => $"#{p.Number} {p.HeadBranch} -> {p.BaseBranch}"));
        Assert.Equal("atlas/atlas-api", pulls[0].HeadRepository);
        Assert.Equal("someone/atlas-api", pulls[1].HeadRepository);
        Assert.Equal("?", pulls[1].Author);
        Assert.True(pulls[1].IsDraft);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero), pulls[0].UpdatedAt);
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("https://api.github.com/repos/atlas/atlas-api/pulls?state=open&sort=updated&direction=desc&per_page=50", request.RequestUri?.ToString());
        Assert.Equal("ghp_t", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task A_list_is_revalidated_with_its_etag_and_a_304_returns_the_same_items()
    {
        var (client, handler) = Create(request => request.Headers.IfNoneMatch.Count == 0
            ? (HttpStatusCode.OK, PullJson)
            : (HttpStatusCode.NotModified, string.Empty));
        handler.ETag = "\"abc\"";

        var first = await client.ListPullRequestsAsync("t", Atlas, Ct);
        var second = await client.ListPullRequestsAsync("t", Atlas, Ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("\"abc\"", handler.Requests[1].Request.Headers.IfNoneMatch.Single().Tag);
        Assert.Same(
            Assert.IsType<Success<IReadOnlyList<PullRequest>>>(first.Value).Value,
            Assert.IsType<Success<IReadOnlyList<PullRequest>>>(second.Value).Value);
    }

    [Fact]
    public async Task A_list_fetched_with_another_token_is_not_revalidated_with_the_old_etag()
    {
        var (client, handler) = Create(request => request.Headers.IfNoneMatch.Count == 0
            ? (HttpStatusCode.OK, PullJson)
            : (HttpStatusCode.NotModified, string.Empty));
        handler.ETag = "\"abc\"";

        await client.ListPullRequestsAsync("old", Atlas, Ct);
        await client.ListPullRequestsAsync("new", Atlas, Ct);
        await client.ListPullRequestsAsync("new", Atlas, Ct);

        Assert.Empty(handler.Requests[1].Request.Headers.IfNoneMatch);
        Assert.Equal("\"abc\"", handler.Requests[2].Request.Headers.IfNoneMatch.Single().Tag);
    }

    [Fact]
    public async Task Issues_leave_out_the_pull_requests_the_endpoint_also_lists()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """
            [{"number":3,"title":"Timeout","user":{"login":"octocat"},"updated_at":"2026-10-08T10:00:00Z","html_url":"https://github.com/atlas/atlas-api/issues/3"},
             {"number":12,"title":"Fix cache","user":{"login":"octocat"},"updated_at":"2026-10-08T10:00:00Z","html_url":"https://github.com/atlas/atlas-api/pull/12",
              "pull_request":{"url":"https://api.github.com/repos/atlas/atlas-api/pulls/12"}}]
            """));

        var issues = Assert.IsType<Success<IReadOnlyList<Issue>>>((await client.ListIssuesAsync("t", Atlas, Ct)).Value).Value;

        Assert.Equal(3, Assert.Single(issues).Number);
    }

    private static string IssuesJson(int count, int firstNumber = 1, bool pullRequests = false) =>
        "[" + string.Join(",", Enumerable.Range(firstNumber, count).Select(n =>
            $$"""{"number":{{n}},"title":"Issue {{n}}","user":{"login":"octocat"},"updated_at":"2026-10-08T10:00:00Z","html_url":"https://github.com/atlas/atlas-api/issues/{{n}}"{{(pullRequests ? ""","pull_request":{"url":"x"}""" : "")}}}""")) + "]";

    [Fact]
    public async Task The_default_issue_page_uses_the_same_request_as_the_plain_list()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, IssuesJson(2)));

        var page = Assert.IsType<Success<IssuePage>>((await client.ListIssuePageAsync("t", Atlas, IssueQuery.Default, "octocat", 1, Ct)).Value).Value;

        Assert.Equal([1, 2], page.Items.Select(i => i.Number));
        Assert.False(page.HasMore);
        Assert.Equal("https://api.github.com/repos/atlas/atlas-api/issues?state=open&sort=updated&direction=desc&per_page=50",
            handler.Requests.Single().Request.RequestUri?.ToString());
    }

    [Fact]
    public async Task Issue_filters_become_query_parameters_and_the_page_number_follows()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, "[]"));

        await client.ListIssuePageAsync("t", Atlas, new IssueQuery(IssueStateFilter.Closed, AssignedToMe: true, CreatedByMe: true), "octo cat", 3, Ct);

        Assert.Equal("https://api.github.com/repos/atlas/atlas-api/issues?state=closed&sort=updated&direction=desc&per_page=50&assignee=octo%20cat&creator=octo%20cat&page=3",
            handler.Requests.Single().Request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task A_full_page_means_more_even_when_pull_requests_were_dropped_from_it()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, IssuesJson(40) .TrimEnd(']') + "," + IssuesJson(10, 100, pullRequests: true).TrimStart('[')));

        var page = Assert.IsType<Success<IssuePage>>((await client.ListIssuePageAsync("t", Atlas, IssueQuery.Default, null, 1, Ct)).Value).Value;

        Assert.Equal(40, page.Items.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task A_search_goes_through_the_search_api_with_the_filters_as_qualifiers()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, $$"""{"total_count":2,"items":{{IssuesJson(2)}}}"""));

        var page = Assert.IsType<Success<IssuePage>>((await client.ListIssuePageAsync("t", Atlas,
            new IssueQuery(IssueStateFilter.Open, AssignedToMe: true, Search: " label:bug timeout "), "octocat", 2, Ct)).Value).Value;

        Assert.Equal([1, 2], page.Items.Select(i => i.Number));
        Assert.False(page.HasMore);
        var url = handler.Requests.Single().Request.RequestUri!;
        Assert.Equal("https://api.github.com/search/issues", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("repo:atlas/atlas-api is:issue is:open assignee:octocat label:bug timeout", System.Web.HttpUtility.ParseQueryString(url.Query)["q"]);
        Assert.Equal("2", System.Web.HttpUtility.ParseQueryString(url.Query)["page"]);
    }

    [Fact]
    public async Task A_search_of_all_the_issues_has_no_state_qualifier()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));

        await client.ListIssuePageAsync("t", Atlas, new IssueQuery(IssueStateFilter.All, CreatedByMe: true, Search: "docs"), "octocat", 1, Ct);

        Assert.Equal("repo:atlas/atlas-api is:issue author:octocat docs",
            System.Web.HttpUtility.ParseQueryString(handler.Requests.Single().Request.RequestUri!.Query)["q"]);
    }

    [Fact]
    public async Task The_me_filters_need_to_know_the_account()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, "[]"));

        var result = await client.ListIssuePageAsync("t", Atlas, new IssueQuery(AssignedToMe: true), null, 1, Ct);

        Assert.Contains("reconnectez-vous", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_refused_search_carries_the_reason_given_by_github()
    {
        var (client, _) = Create(_ => (HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"message":"The search contains an invalid qualifier."}]}"""));

        var result = await client.ListIssuePageAsync("t", Atlas, new IssueQuery(Search: "x"), "octocat", 1, Ct);

        Assert.Contains("invalid qualifier", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_branch_of_the_repository_is_read()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"default_branch":"develop"}"""));

        var result = await client.GetDefaultBranchAsync("t", Atlas, Ct);

        Assert.Equal("develop", Assert.IsType<Success<string>>(result.Value).Value);
        Assert.Equal("https://api.github.com/repos/atlas/atlas-api", handler.Requests.Single().Request.RequestUri?.ToString());
    }

    [Fact]
    public async Task A_pull_request_is_created_with_the_title_branches_and_draft_flag()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.Created, OnePullJson));

        var result = await client.CreatePullRequestAsync("t", Atlas, new NewPullRequest("Fix cache", "Body", "fix/cache", "main", true), Ct);

        Assert.Equal(12, Assert.IsType<Success<PullRequest>>(result.Value).Value.Number);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/repos/atlas/atlas-api/pulls", request.RequestUri?.ToString());
        Assert.Equal("""{"title":"Fix cache","body":"Body","head":"fix/cache","base":"main","draft":true}""", body);
    }

    [Fact]
    public async Task Linked_issues_become_closing_keywords_at_the_end_of_the_body()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.Created, OnePullJson));

        await client.CreatePullRequestAsync("t", Atlas, new NewPullRequest("T", "Body", "fix/cache", "main", false, LinkedIssues: [3, 5, 3]), Ct);
        await client.CreatePullRequestAsync("t", Atlas, new NewPullRequest("T", "", "fix/cache", "main", false, LinkedIssues: [7]), Ct);

        Assert.Contains(""""body":"Body\n\nCloses #3, Closes #5","""", handler.Requests[0].Body);
        Assert.Contains(""""body":"Closes #7","""", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Reviewer_candidates_are_the_assignable_people_without_the_signed_in_account()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/assignees", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """[{"login":"octocat"},{"login":"Grace"},{"login":"ada"}]""")
            : (HttpStatusCode.OK, """{"login":"octocat"}"""));

        Assert.True(client.SupportsPullRequestCreateOptions);
        var result = await client.GetPullRequestCreateOptionsAsync("t", Atlas, Ct);

        var options = Assert.IsType<Success<PullRequestCreateOptions>>(result.Value).Value;
        Assert.Equal(["ada", "Grace"], options.Reviewers.Select(r => r.Id));
        Assert.False(options.SupportsRequiredReviewers);
        Assert.False(options.IsTruncated);
        Assert.Contains(handler.Requests, r => r.Request.RequestUri!.AbsolutePath == "/repos/atlas/atlas-api/assignees");
    }

    [Fact]
    public async Task Assignee_candidates_include_the_signed_in_account()
    {
        var (client, _) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/assignees", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """[{"login":"octocat"},{"login":"Grace"},{"login":"ada"}]""")
            : (HttpStatusCode.OK, """{"login":"octocat"}"""));

        var result = await client.GetPullRequestCreateOptionsAsync("t", Atlas, Ct);

        var options = Assert.IsType<Success<PullRequestCreateOptions>>(result.Value).Value;
        Assert.Equal(["ada", "Grace", "octocat"], options.Assignees!.Select(a => a.Id));
    }

    [Fact]
    public async Task Assignees_are_sent_after_the_creation()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, "{}"));
        var created = new PullRequest(12, "T", "me", "fix/cache", null, "main", false, Now, "u");
        var request = new NewPullRequest("T", "", "fix/cache", "main", false, Assignees: ["me", "ada"]);

        var result = await client.ApplyPullRequestCreateOptionsAsync("t", Atlas, created, request, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/atlas/atlas-api/issues/12/assignees", sent.Request.RequestUri?.ToString());
        Assert.Equal("""{"assignees":["me","ada"]}""", sent.Body);
    }

    [Fact]
    public async Task A_refused_assignee_is_reported_with_its_reason()
    {
        var (client, _) = Create(_ => (HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}"""));
        var created = new PullRequest(12, "T", "me", "fix/cache", null, "main", false, Now, "u");
        var request = new NewPullRequest("T", "", "fix/cache", "main", false, Assignees: ["ghost"]);

        var result = await client.ApplyPullRequestCreateOptionsAsync("t", Atlas, created, request, Ct);

        Assert.StartsWith("Assignés : ", Assert.IsType<Failure>(result.Value).Message);
    }

    [Fact]
    public async Task Reviewer_candidates_are_still_listed_when_the_own_account_cannot_be_read()
    {
        var (client, _) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/assignees", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """[{"login":"ada"}]""")
            : (HttpStatusCode.Forbidden, "{}"));

        var result = await client.GetPullRequestCreateOptionsAsync("t", Atlas, Ct);

        Assert.Equal(["ada"], Assert.IsType<Success<PullRequestCreateOptions>>(result.Value).Value.Reviewers.Select(r => r.Id));
    }

    [Fact]
    public async Task Labels_then_reviewers_are_sent_after_the_creation()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, "{}"));
        var created = new PullRequest(12, "T", "me", "fix/cache", null, "main", false, Now, "u");
        var request = new NewPullRequest("T", "", "fix/cache", "main", false, [new NewPullRequestReviewer("ada", "ada", false)], ["bug", "release"]);

        var result = await client.ApplyPullRequestCreateOptionsAsync("t", Atlas, created, request, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://api.github.com/repos/atlas/atlas-api/issues/12/labels", handler.Requests[0].Request.RequestUri?.ToString());
        Assert.Equal("""{"labels":["bug","release"]}""", handler.Requests[0].Body);
        Assert.Equal("https://api.github.com/repos/atlas/atlas-api/pulls/12/requested_reviewers", handler.Requests[1].Request.RequestUri?.ToString());
        Assert.Equal("""{"reviewers":["ada"]}""", handler.Requests[1].Body);
    }

    [Fact]
    public async Task A_refused_reviewer_does_not_stop_the_labels_and_is_reported()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/requested_reviewers", StringComparison.Ordinal)
            ? (HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"message":"Review cannot be requested from pull request author."}]}""")
            : (HttpStatusCode.OK, "{}"));
        var created = new PullRequest(12, "T", "me", "fix/cache", null, "main", false, Now, "u");
        var request = new NewPullRequest("T", "", "fix/cache", "main", false, [new NewPullRequestReviewer("me", "me", false)], ["bug"]);

        var result = await client.ApplyPullRequestCreateOptionsAsync("t", Atlas, created, request, Ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Relecteurs : Validation Failed. Review cannot be requested from pull request author.", Assert.IsType<Failure>(result.Value).Message);
    }

    [Fact]
    public async Task A_refusal_to_create_carries_the_reason_given_by_github()
    {
        var (client, _) = Create(_ => (HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"message":"A pull request already exists for atlas:fix/cache."}]}"""));

        var result = await client.CreatePullRequestAsync("t", Atlas, new NewPullRequest("T", "", "fix/cache", "main", false), Ct);

        Assert.Equal("Validation Failed. A pull request already exists for atlas:fix/cache.", Assert.IsType<Failure>(result.Value).Message);
    }

    [Fact]
    public async Task Errors_are_told_apart_unauthorized_forbidden_rate_limited_and_missing()
    {
        var status = HttpStatusCode.Unauthorized;
        var (client, handler) = Create(_ => (status, "{}"));

        Assert.IsType<AuthenticationFailed>((await client.ListPullRequestsAsync("t", Atlas, Ct)).Value);

        status = HttpStatusCode.Forbidden;
        Assert.IsType<AuthenticationFailed>((await client.ListPullRequestsAsync("t", Atlas, Ct)).Value);

        handler.Headers["X-RateLimit-Remaining"] = "0";
        Assert.Contains("limite", Assert.IsType<Failure>((await client.ListPullRequestsAsync("t", Atlas, Ct)).Value).Message, StringComparison.Ordinal);

        handler.Headers.Clear();
        status = HttpStatusCode.NotFound;
        Assert.Contains("introuvable", Assert.IsType<Failure>((await client.ListIssuesAsync("t", Atlas, Ct)).Value).Message, StringComparison.Ordinal);
    }
}
