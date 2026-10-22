using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Integrations.AzureDevOps;

namespace Vigo.Integrations.Tests;

public sealed class AzureDevOpsClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HostRepository Atlas = new(HostingProvider.AzureDevOps, "dev.azure.com", "contoso/Atlas Project", "atlas-api");

    private sealed class FakeHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body, string MediaType)> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body, mediaType) = respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        }
    }

    private static (AzureDevOpsClient Client, FakeHandler Handler) Create(Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        var handler = new FakeHandler(r => { var (s, b) = respond(r); return (s, b, "application/json"); });
        return (new AzureDevOpsClient(new HttpClient(handler)), handler);
    }

    private static HostRepository? Parse(string url) => new AzureDevOpsClient(new HttpClient()).ParseRepository(RemoteUrl.TryParse(url)!);

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Atlas/_git/api", "contoso/Atlas", "api")]
    [InlineData("https://contoso@dev.azure.com/contoso/Atlas/_git/api", "contoso/Atlas", "api")]
    [InlineData("https://dev.azure.com/contoso/Atlas%20Project/_git/api", "contoso/Atlas Project", "api")]
    [InlineData("https://contoso.visualstudio.com/Atlas/_git/api", "contoso/Atlas", "api")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Atlas/_git/api", "contoso/Atlas", "api")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Atlas/api", "contoso/Atlas", "api")]
    [InlineData("contoso@vs-ssh.visualstudio.com:v3/contoso/Atlas/api", "contoso/Atlas", "api")]
    public void Recognizes_the_remote_urls_of_azure_devops(string url, string owner, string name)
    {
        Assert.Equal(new HostRepository(HostingProvider.AzureDevOps, "dev.azure.com", owner, name), Parse(url));
    }

    [Theory]
    [InlineData("https://github.com/contoso/Atlas")]
    [InlineData("https://dev.azure.com/contoso/Atlas")]
    [InlineData("https://dev.azure.com/contoso")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Atlas")]
    public void Ignores_other_hosts_and_incomplete_paths(string url)
    {
        Assert.Null(Parse(url));
    }

    [Fact]
    public void Builds_the_web_pages_of_a_repository()
    {
        Assert.Equal("https://dev.azure.com/contoso/Atlas Project/_git/atlas-api", Atlas.WebUrl);
        Assert.Equal("https://dev.azure.com/contoso/Atlas Project/_git/atlas-api/pullrequests", Atlas.PullRequestsUrl);
        Assert.Equal("https://dev.azure.com/contoso/Atlas Project/_workitems", Atlas.IssuesUrl);
    }

    [Fact]
    public async Task Reads_the_account_with_the_token_as_a_basic_password()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"displayName":"Ada Lovelace","emailAddress":"ada@contoso.com"}"""));

        var user = Assert.IsType<Success<HostUser>>((await client.GetUserAsync("pat-secret", Ct)).Value).Value;

        Assert.Equal(new HostUser("ada@contoso.com", "Ada Lovelace", null), user);
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes(":pat-secret")), request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Checks_a_token_limited_to_one_organization_against_its_connection_data()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK,
            """{"authenticatedUser":{"providerDisplayName":"Ada Lovelace","properties":{"Account":{"$type":"System.String","$value":"ada@contoso.com"}}}}"""));

        var user = Assert.IsType<Success<HostUser>>((await client.GetUserAsync("pat-secret", "contoso", Ct)).Value).Value;

        Assert.Equal(new HostUser("ada@contoso.com", "Ada Lovelace", null), user);
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("dev.azure.com", request.RequestUri?.Host);
        Assert.StartsWith("/contoso/_apis/connectionData", request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task A_token_refused_for_the_organization_is_an_authentication_failure()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NonAuthoritativeInformation, "<html>Sign in</html>", "text/html"));
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var failure = Assert.IsType<AuthenticationFailed>((await client.GetUserAsync("bad", "contoso", Ct)).Value);
        Assert.Contains("contoso", failure.Message);
    }

    [Fact]
    public async Task An_invalid_token_answered_with_the_sign_in_page_is_an_authentication_failure()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NonAuthoritativeInformation, "<html>Sign in</html>", "text/html"));
        var client = new AzureDevOpsClient(new HttpClient(handler));

        Assert.IsType<AuthenticationFailed>((await client.GetUserAsync("bad", Ct)).Value);
        Assert.IsType<AuthenticationFailed>((await client.ListPullRequestsAsync("bad", Atlas, Ct)).Value);
    }

    [Fact]
    public async Task Lists_the_active_pull_requests_of_the_repository()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """
            {"value":[
              {"pullRequestId":7,"title":"Old","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/feature/a","targetRefName":"refs/heads/main","isDraft":false,"creationDate":"2026-10-01T10:00:00Z"},
              {"pullRequestId":9,"title":"New","createdBy":{"displayName":"Linus"},"sourceRefName":"refs/heads/fix","targetRefName":"refs/heads/main","isDraft":true,"creationDate":"2026-10-08T10:00:00Z"}
            ]}
            """));

        var list = Assert.IsType<Success<IReadOnlyList<PullRequest>>>((await client.ListPullRequestsAsync("t", Atlas, Ct)).Value).Value;

        Assert.Equal([9, 7], list.Select(p => p.Number));
        Assert.Equal("fix", list[0].HeadBranch);
        Assert.Equal("main", list[0].BaseBranch);
        Assert.True(list[0].IsDraft);
        Assert.Equal("Linus", list[0].Author);
        Assert.Equal("https://dev.azure.com/contoso/Atlas Project/_git/atlas-api/pullrequest/9", list[0].Url);
        var url = Assert.Single(handler.Requests).Request.RequestUri!.AbsoluteUri;
        Assert.StartsWith("https://dev.azure.com/contoso/Atlas%20Project/_apis/git/repositories/atlas-api/pullrequests?searchCriteria.status=active", url, StringComparison.Ordinal);
        Assert.Contains("api-version=7.1", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_requests_carry_the_project_id_and_the_review_decision_from_the_votes()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """
            {"value":[
              {"pullRequestId":7,"title":"A","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/a","targetRefName":"refs/heads/main","isDraft":false,"creationDate":"2026-10-08T10:00:00Z",
               "repository":{"project":{"id":"p-1"}},"reviewers":[{"vote":10},{"vote":0},{"vote":0,"isContainer":true}]},
              {"pullRequestId":8,"title":"B","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/b","targetRefName":"refs/heads/main","isDraft":false,"creationDate":"2026-10-07T10:00:00Z",
               "reviewers":[{"vote":10},{"vote":-5}]}
            ]}
            """));

        var list = Assert.IsType<Success<IReadOnlyList<PullRequest>>>((await client.ListPullRequestsAsync("t", Atlas, Ct)).Value).Value;

        Assert.Equal("p-1", list[0].ProjectId);
        Assert.Equal(ReviewState.Approved, list[0].Review);
        Assert.Equal(ReviewState.ChangesRequested, list[1].Review);
    }

    [Fact]
    public async Task The_status_of_a_pull_request_merges_posted_statuses_and_policy_evaluations()
    {
        var (client, handler) = Create(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("/statuses", StringComparison.Ordinal)
            ? """{"value":[{"state":"succeeded","context":{"name":"lint","genre":"ci"}},{"state":"notApplicable","context":{"name":"x"}}]}"""
            : """
              {"value":[
                {"status":"approved","configuration":{"type":{"displayName":"Build"},"settings":{"displayName":"Build validation"}}},
                {"status":"running","configuration":{"type":{"displayName":"Minimum number of reviewers"}}},
                {"status":"notApplicable","configuration":{"type":{"displayName":"Work item linking"}}}]}
              """));
        var pull = new PullRequest(7, "A", "Ada", "a", Atlas.FullName, "main", false, DateTimeOffset.UnixEpoch, "https://x", ProjectId: "p-1", Review: ReviewState.Approved);

        var status = Assert.IsType<Success<PullRequestStatus>>((await client.GetPullRequestStatusAsync("t", Atlas, pull, cancellationToken: Ct)).Value).Value;

        Assert.Equal(CheckState.Pending, status.Checks);
        Assert.Equal(ReviewState.Approved, status.Review);
        Assert.Equal(
            [("ci/lint", CheckState.Success), ("Build validation", CheckState.Success), ("Minimum number of reviewers", CheckState.Pending), ("Work item linking", CheckState.Skipped)],
            status.Runs.Select(r => (r.Name, r.State)));
        var policy = handler.Requests.Last().Request.RequestUri!.AbsoluteUri;
        Assert.Contains("/_apis/policy/evaluations?artifactId=vstfs%3A%2F%2F%2FCodeReview%2FCodeReviewId%2Fp-1%2F7", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policies_say_whether_they_block_and_link_to_their_build_and_statuses_link_to_their_target()
    {
        var (client, _) = Create(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("/statuses", StringComparison.Ordinal)
            ? """{"value":[{"state":"failed","description":"3 errors","targetUrl":"https://ci.example/9","creationDate":"2026-10-15T10:00:00Z","updatedDate":"2026-10-15T10:05:00Z","context":{"name":"lint","genre":"ci"}}]}"""
            : """
              {"value":[
                {"status":"rejected","startedDate":"2026-10-15T10:00:00Z","completedDate":"2026-10-15T10:02:13Z","context":{"buildId":42},
                 "configuration":{"isBlocking":true,"type":{"displayName":"Build"},"settings":{"displayName":"Build validation"}}},
                {"status":"approved","configuration":{"isBlocking":false,"type":{"displayName":"Comment requirements"}}}]}
              """));
        var pull = new PullRequest(7, "A", "Ada", "a", Atlas.FullName, "main", false, DateTimeOffset.UnixEpoch, "https://x", ProjectId: "p-1");

        var status = Assert.IsType<Success<PullRequestStatus>>((await client.GetPullRequestStatusAsync("t", Atlas, pull, cancellationToken: Ct)).Value).Value;

        var legacy = status.Runs[0];
        Assert.Equal((CheckKind.Status, "3 errors", "https://ci.example/9"), (legacy.Kind, legacy.Description, legacy.Url));
        Assert.Null(legacy.IsRequired);
        var build = status.Runs[1];
        Assert.Equal((CheckKind.Policy, true, "Build"), (build.Kind, build.IsRequired, build.Description));
        Assert.Equal("https://dev.azure.com/contoso/Atlas%20Project/_build/results?buildId=42", build.Url);
        Assert.Equal(TimeSpan.FromSeconds(133), build.CompletedAt - build.StartedAt);
        var optional = status.Runs[2];
        Assert.Equal((false, null), (optional.IsRequired, optional.Url));
    }

    [Fact]
    public async Task Lists_the_open_work_items_in_two_calls()
    {
        var (client, handler) = Create(r => r.Method == HttpMethod.Post
            ? (HttpStatusCode.OK, """{"workItems":[{"id":3},{"id":5}]}""")
            : (HttpStatusCode.OK, """
                {"value":[
                  {"id":3,"fields":{"System.Title":"Crash","System.CreatedBy":{"displayName":"Ada"},"System.ChangedDate":"2026-10-02T10:00:00Z"}},
                  {"id":5,"fields":{"System.Title":"Slow","System.CreatedBy":{"displayName":"Linus"},"System.ChangedDate":"2026-10-05T10:00:00Z"}}
                ]}
                """));

        var list = Assert.IsType<Success<IReadOnlyList<Issue>>>((await client.ListIssuesAsync("t", Atlas, Ct)).Value).Value;

        Assert.Equal([5, 3], list.Select(i => i.Number));
        Assert.Equal("https://dev.azure.com/contoso/Atlas Project/_workitems/edit/5", list[0].Url);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("@project", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("ids=3,5", handler.Requests[1].Request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_open_work_item_costs_a_single_call()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"workItems":[]}"""));

        var list = Assert.IsType<Success<IReadOnlyList<Issue>>>((await client.ListIssuesAsync("t", Atlas, Ct)).Value).Value;

        Assert.Empty(list);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reads_the_default_branch_without_its_prefix()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """{"defaultBranch":"refs/heads/develop"}"""));

        Assert.Equal("develop", Assert.IsType<Success<string>>((await client.GetDefaultBranchAsync("t", Atlas, Ct)).Value).Value);
    }

    [Fact]
    public async Task Creates_a_pull_request_between_full_reference_names()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.Created, """
            {"pullRequestId":12,"title":"Add x","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/feature/x","targetRefName":"refs/heads/main","isDraft":true,"creationDate":"2026-10-09T10:00:00Z"}
            """));

        var created = Assert.IsType<Success<PullRequest>>(
            (await client.CreatePullRequestAsync("t", Atlas, new NewPullRequest("Add x", "Body", "feature/x", "main", true), Ct)).Value).Value;

        Assert.Equal(12, created.Number);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("\"sourceRefName\":\"refs/heads/feature/x\"", body, StringComparison.Ordinal);
        Assert.Contains("\"targetRefName\":\"refs/heads/main\"", body, StringComparison.Ordinal);
        Assert.Contains("\"isDraft\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creates_a_pull_request_with_reviewers_labels_and_linked_work_items()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.Created, """
            {"pullRequestId":12,"title":"Add x","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/feature/x","targetRefName":"refs/heads/main","isDraft":false,"creationDate":"2026-10-09T10:00:00Z"}
            """));
        var request = new NewPullRequest(
            "Add x", "", "feature/x", "main", false,
            [new NewPullRequestReviewer("id-1", "Ada", true), new NewPullRequestReviewer("id-2", "Grace", false)],
            ["bug", "release"], [41, 42]);

        Assert.IsType<Success<PullRequest>>((await client.CreatePullRequestAsync("t", Atlas, request, Ct)).Value);

        var (_, body) = Assert.Single(handler.Requests);
        Assert.Contains("\"reviewers\":[{\"id\":\"id-1\",\"isRequired\":true},{\"id\":\"id-2\",\"isRequired\":false}]", body, StringComparison.Ordinal);
        Assert.Contains("\"labels\":[{\"name\":\"bug\"},{\"name\":\"release\"}]", body, StringComparison.Ordinal);
        Assert.Contains("\"workItemRefs\":[{\"id\":41},{\"id\":42}]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_pull_request_sends_no_reviewers_labels_or_work_items()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.Created, """
            {"pullRequestId":12,"title":"Add x","createdBy":{"displayName":"Ada"},"sourceRefName":"refs/heads/feature/x","targetRefName":"refs/heads/main","isDraft":false,"creationDate":"2026-10-09T10:00:00Z"}
            """));

        await client.CreatePullRequestAsync("t", Atlas, new NewPullRequest("Add x", "", "feature/x", "main", false, [], [], []), Ct);

        var (_, body) = Assert.Single(handler.Requests);
        Assert.DoesNotContain("reviewers", body, StringComparison.Ordinal);
        Assert.DoesNotContain("labels", body, StringComparison.Ordinal);
        Assert.DoesNotContain("workItemRefs", body, StringComparison.Ordinal);
    }

    // The request body is JSON: the quotes of the query are escaped there.
    private static string WiqlOf(string body) => System.Text.Json.JsonElement.Parse(body).GetProperty("query").GetString()!;

    private static IReadOnlyList<ReferenceSuggestion> Suggestions(GitResult<IReadOnlyList<ReferenceSuggestion>> result) =>
        Assert.IsType<Success<IReadOnlyList<ReferenceSuggestion>>>(result.Value).Value;

    [Fact]
    public async Task Work_items_are_searched_by_title_and_number_with_their_type_and_state()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/wiql", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"workItems":[{"id":12}]}""")
            : (HttpStatusCode.OK, """
                {"value":[{"id":12,"fields":{"System.Title":"Fix cache","System.WorkItemType":"Bug","System.State":"Active","System.ChangedDate":"2026-10-01T00:00:00Z"}}]}
                """));

        var items = Suggestions(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.Issue, "12", Ct));

        var item = Assert.Single(items);
        Assert.Equal(("Fix cache", "#12 · Bug · Active", "#12"), (item.Label, item.Detail, item.Insert));
        var query = WiqlOf(handler.Requests[0].Body);
        Assert.Contains("[System.Id] = 12", query, StringComparison.Ordinal);
        Assert.Contains("[System.Title] Contains '12'", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quote_in_the_searched_text_is_doubled_in_the_query()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"workItems":[]}"""));

        Assert.Empty(Suggestions(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.Issue, "it's", Ct)));

        Assert.Contains("Contains 'it''s'", WiqlOf(handler.Requests[0].Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_requests_are_filtered_by_number_prefix_or_title()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """
            {"value":[
              {"pullRequestId":3,"title":"Add cache","createdBy":{"displayName":"Ada"},"status":"active","isDraft":false,"creationDate":"2026-10-02T00:00:00Z"},
              {"pullRequestId":31,"title":"Other","createdBy":{"displayName":"Bob"},"status":"completed","isDraft":false,"creationDate":"2026-10-03T00:00:00Z"}]}
            """));

        var byTitle = Suggestions(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.PullRequest, "cache", Ct));
        Assert.Equal("!3", Assert.Single(byTitle).Insert);
        Assert.Equal("!3 · Ada · active", byTitle[0].Detail);

        var byNumber = Suggestions(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.PullRequest, "3", Ct));
        Assert.Equal(["!31", "!3"], byNumber.Select(i => i.Insert));
    }

    [Fact]
    public async Task People_are_proposed_as_mentions_by_identity_id()
    {
        var (client, _) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/teams", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"value":[{"id":"t1"}]}"""),
            var p when p.Contains("/teams/t1/members", StringComparison.Ordinal) => (HttpStatusCode.OK, """
                {"value":[{"identity":{"id":"9d6f3b2a-1c4e-4f7a-8b21-5e0c7a1d2f34","displayName":"Ada Lovelace"}},{"identity":{"id":"b","displayName":"Grace"}}]}
                """),
            _ => (HttpStatusCode.NotFound, "{}"),
        });

        var items = Suggestions(await client.SearchReferencesAsync("t", Atlas, ReferenceKind.Person, "love", Ct));

        var person = Assert.Single(items);
        Assert.Equal(("Ada Lovelace", "@<9d6f3b2a-1c4e-4f7a-8b21-5e0c7a1d2f34>"), (person.Label, person.Insert));
    }

    [Fact]
    public async Task Mentioned_identities_are_named_from_the_teams_then_the_identity_service()
    {
        const string Ada = "9d6f3b2a-1c4e-4f7a-8b21-5e0c7a1d2f34";
        const string Bob = "11111111-2222-3333-4444-555555555555";
        var (client, handler) = Create(r => r.RequestUri!.Host switch
        {
            "vssps.dev.azure.com" => (HttpStatusCode.OK, $$"""{"value":[{"id":"{{Bob}}","providerDisplayName":"Bob Martin"}]}"""),
            _ when r.RequestUri.AbsolutePath.EndsWith("/teams", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"value":[{"id":"t1"}]}"""),
            _ when r.RequestUri.AbsolutePath.Contains("/teams/t1/members", StringComparison.Ordinal) => (HttpStatusCode.OK,
                $$$"""{"value":[{"identity":{"id":"{{{Ada}}}","displayName":"Ada Lovelace"}}]}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });

        var names = Assert.IsType<Success<IReadOnlyDictionary<string, string>>>((await client.ResolveMentionsAsync("t", Atlas, [Ada, Bob], Ct)).Value).Value;

        Assert.Equal("Ada Lovelace", names[Ada]);
        Assert.Equal("Bob Martin", names[Bob]);
        Assert.Contains(handler.Requests, r => r.Request.RequestUri!.AbsoluteUri.Contains("identityIds=" + Bob, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Offers_the_people_of_the_project_teams_once_each_and_sorted()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/teams", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"value":[{"id":"t1"},{"id":"t2"}]}"""),
            var p when p.Contains("/teams/t1/members", StringComparison.Ordinal) => (HttpStatusCode.OK, """
                {"value":[{"identity":{"id":"b","displayName":"Grace","uniqueName":"grace@contoso.com"}},{"identity":{"id":"g","displayName":"Everyone","isContainer":true}}]}
                """),
            var p when p.Contains("/teams/t2/members", StringComparison.Ordinal) => (HttpStatusCode.OK, """
                {"value":[{"identity":{"id":"a","displayName":"Ada"}},{"identity":{"id":"B","displayName":"Grace"}},{"identity":{"id":"c","uniqueName":"zed@contoso.com"}}]}
                """),
            _ => (HttpStatusCode.NotFound, "{}"),
        });

        var options = Assert.IsType<Success<PullRequestCreateOptions>>((await client.GetPullRequestCreateOptionsAsync("t", Atlas, Ct)).Value).Value;

        Assert.Equal(["Ada", "Grace", "zed@contoso.com"], options.Reviewers.Select(r => r.Name));
        Assert.False(options.IsTruncated);
        Assert.Equal("https://dev.azure.com/contoso/_apis/projects/Atlas%20Project/teams", handler.Requests[0].Request.RequestUri!.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task A_team_that_cannot_be_read_is_skipped()
    {
        var (client, _) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/teams", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"value":[{"id":"t1"},{"id":"t2"}]}""")
            : r.RequestUri.AbsolutePath.Contains("/t1/", StringComparison.Ordinal)
                ? (HttpStatusCode.Forbidden, "{}")
                : (HttpStatusCode.OK, """{"value":[{"identity":{"id":"a","displayName":"Ada"}}]}"""));

        var options = Assert.IsType<Success<PullRequestCreateOptions>>((await client.GetPullRequestCreateOptionsAsync("t", Atlas, Ct)).Value).Value;

        Assert.Equal(["Ada"], options.Reviewers.Select(r => r.Name));
    }

    [Fact]
    public async Task A_refused_team_list_is_a_failure()
    {
        var (client, _) = Create(_ => (HttpStatusCode.Forbidden, "{}"));

        Assert.IsType<AuthenticationFailed>((await client.GetPullRequestCreateOptionsAsync("t", Atlas, Ct)).Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(AuthenticationFailed))]
    [InlineData(HttpStatusCode.Forbidden, typeof(AuthenticationFailed))]
    [InlineData(HttpStatusCode.NotFound, typeof(Failure))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(Failure))]
    public async Task Refusals_are_explained(HttpStatusCode status, Type expected)
    {
        var (client, _) = Create(_ => (status, "{}"));

        Assert.IsType(expected, (await client.ListPullRequestsAsync("t", Atlas, Ct)).Value);
    }

    [Fact]
    public async Task A_conflict_shows_the_message_of_the_service()
    {
        var (client, _) = Create(_ => (HttpStatusCode.Conflict, """{"message":"TF401179: An active pull request for the source and target branch already exists."}"""));

        var failure = Assert.IsType<Failure>((await client.CreatePullRequestAsync("t", Atlas, new NewPullRequest("a", "", "x", "main", false), Ct)).Value);

        Assert.Contains("TF401179", failure.Message, StringComparison.Ordinal);
    }

    // --- Microsoft Entra ID device flow (ADR-047) ---

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private const string Jwt = "eyJhbGciOi.payload.signature";

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (AzureDevOpsClient Client, FakeHandler Handler, FakeClock Clock) CreateOAuth(
        Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond, string? clientId = "client-1")
    {
        var handler = new FakeHandler(r =>
        {
            var body = r.Content is null ? string.Empty : r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var (s, b) = respond(r, body);
            return (s, b, "application/json");
        });
        var clock = new FakeClock(Now);
        return (new AzureDevOpsClient(new HttpClient(handler), clientId, clock), handler, clock);
    }

    [Fact]
    public async Task The_device_flow_needs_a_client_id()
    {
        var (client, handler, _) = CreateOAuth((_, _) => (HttpStatusCode.OK, "{}"), clientId: null);

        Assert.False(client.SupportsDeviceFlow);
        Assert.IsType<Failure>((await client.StartDeviceFlowAsync(Ct)).Value);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Starts_the_device_flow_with_the_client_id_and_the_azure_devops_scope()
    {
        var (client, handler, _) = CreateOAuth((_, _) => (HttpStatusCode.OK,
            """{"device_code":"dc","user_code":"ABCD1234","verification_uri":"https://microsoft.com/devicelogin","expires_in":900,"interval":5}"""));

        var code = Assert.IsType<Success<DeviceCode>>((await client.StartDeviceFlowAsync(Ct)).Value).Value;

        Assert.True(client.SupportsDeviceFlow);
        Assert.Equal("ABCD1234", code.UserCode);
        Assert.Equal(Now.AddSeconds(900), code.ExpiresAt);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/devicecode", request.RequestUri?.ToString());
        Assert.Contains("client_id=client-1", body, StringComparison.Ordinal);
        Assert.Contains("499b84ac-1321-427f-aa17-267ca6975798", body, StringComparison.Ordinal);
        Assert.Contains("offline_access", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":"authorization_pending"}""", false)]
    [InlineData("""{"error":"slow_down"}""", true)]
    public async Task Polling_reports_pending_and_slow_down(string json, bool slowDown)
    {
        var (client, _, _) = CreateOAuth((_, _) => (HttpStatusCode.BadRequest, json));
        var code = new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5));

        var poll = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(code, Ct)).Value).Value;

        Assert.Equal(new DevicePoll(null, slowDown), poll);
    }

    [Theory]
    [InlineData("""{"error":"authorization_declined"}""")]
    [InlineData("""{"error":"expired_token"}""")]
    public async Task Polling_fails_when_refused_or_expired(string json)
    {
        var (client, _, _) = CreateOAuth((_, _) => (HttpStatusCode.BadRequest, json));

        Assert.IsType<Failure>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value);
    }

    [Fact]
    public async Task A_granted_code_gives_a_stored_token_that_the_account_lookup_sends_as_a_bearer()
    {
        var (client, handler, _) = CreateOAuth((r, _) => r.RequestUri!.Host == "login.microsoftonline.com"
            ? (HttpStatusCode.OK, $$"""{"access_token":"{{Jwt}}","refresh_token":"rt-1","expires_in":3600}""")
            : (HttpStatusCode.OK, """{"displayName":"Ada","emailAddress":"ada@contoso.com"}"""));

        var poll = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value).Value;
        Assert.NotNull(poll.Token);
        Assert.StartsWith("oauth1|", poll.Token, StringComparison.Ordinal);

        Assert.IsType<Success<HostUser>>((await client.GetUserAsync(poll.Token, Ct)).Value);
        var profile = handler.Requests[^1].Request;
        Assert.Equal("Bearer", profile.Headers.Authorization?.Scheme);
        Assert.Equal(Jwt, profile.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task A_fresh_token_is_used_as_is_and_a_personal_token_untouched()
    {
        var (client, handler, _) = CreateOAuth((_, _) => (HttpStatusCode.OK, $$"""{"access_token":"{{Jwt}}","refresh_token":"rt-1","expires_in":3600}"""));
        var stored = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value).Value.Token!;
        handler.Requests.Clear();

        var fresh = Assert.IsType<Success<ResolvedToken>>((await client.ResolveTokenAsync(stored, Ct)).Value).Value;
        var personal = Assert.IsType<Success<ResolvedToken>>((await client.ResolveTokenAsync("pat-secret", Ct)).Value).Value;

        Assert.Equal(new ResolvedToken(Jwt, null), fresh);
        Assert.Equal(new ResolvedToken("pat-secret", null), personal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_expired_token_is_renewed_with_the_refresh_token()
    {
        var (client, handler, clock) = CreateOAuth((_, body) => body.Contains("grant_type=refresh_token", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"access_token":"eyJnew.access.token","expires_in":3600}""")
            : (HttpStatusCode.OK, $$"""{"access_token":"{{Jwt}}","refresh_token":"rt-1","expires_in":3600}"""));
        var stored = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value).Value.Token!;
        clock.Now = Now.AddMinutes(59);

        var renewed = Assert.IsType<Success<ResolvedToken>>((await client.ResolveTokenAsync(stored, Ct)).Value).Value;

        Assert.Equal("eyJnew.access.token", renewed.AccessToken);
        Assert.NotNull(renewed.UpdatedStored);
        Assert.Contains("|rt-1|eyJnew.access.token", renewed.UpdatedStored, StringComparison.Ordinal);
        Assert.Contains("refresh_token=rt-1", handler.Requests[^1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calls_made_together_share_one_renewal()
    {
        var (client, handler, clock) = CreateOAuth((_, body) => body.Contains("grant_type=refresh_token", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"access_token":"eyJnew.access.token","expires_in":3600}""")
            : (HttpStatusCode.OK, $$"""{"access_token":"{{Jwt}}","refresh_token":"rt-1","expires_in":3600}"""));
        var stored = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value).Value.Token!;
        clock.Now = Now.AddMinutes(59);
        handler.Requests.Clear();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.ResolveTokenAsync(stored, Ct)));

        Assert.Single(handler.Requests);
        Assert.All(results, r => Assert.Equal("eyJnew.access.token", Assert.IsType<Success<ResolvedToken>>(r.Value).Value.AccessToken));
    }

    [Fact]
    public async Task A_revoked_refresh_token_asks_to_sign_in_again()
    {
        var (client, _, clock) = CreateOAuth((_, body) => body.Contains("grant_type=refresh_token", StringComparison.Ordinal)
            ? (HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"AADSTS700082: The refresh token has expired."}""")
            : (HttpStatusCode.OK, $$"""{"access_token":"{{Jwt}}","refresh_token":"rt-1","expires_in":3600}"""));
        var stored = Assert.IsType<Success<DevicePoll>>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value).Value.Token!;
        clock.Now = Now.AddHours(2);

        Assert.IsType<AuthenticationFailed>((await client.ResolveTokenAsync(stored, Ct)).Value);
    }

    [Theory]
    [InlineData("""{"error":"consent_required","error_description":"AADSTS65001: The user or administrator has not consented."}""")]
    [InlineData("""{"error":"invalid_grant","error_description":"AADSTS90094: The grant requires admin permission."}""")]
    public async Task A_tenant_that_requires_admin_approval_gets_the_link_and_the_token_alternative(string json)
    {
        var (client, _, _) = CreateOAuth((_, _) => (HttpStatusCode.BadRequest, json));

        var failure = Assert.IsType<Failure>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value);

        Assert.Contains("administrateur", failure.Message, StringComparison.Ordinal);
        Assert.Contains("adminconsent?client_id=client-1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("jeton d'accès personnel", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_the_consent_screen_also_suggests_the_token()
    {
        var (client, _, _) = CreateOAuth((_, _) => (HttpStatusCode.BadRequest, """{"error":"authorization_declined"}"""));

        var failure = Assert.IsType<Failure>((await client.PollDeviceFlowAsync(new DeviceCode("U", "https://x", "dc", Now.AddMinutes(5), TimeSpan.FromSeconds(5)), Ct)).Value);

        Assert.Contains("jeton d'accès personnel", failure.Message, StringComparison.Ordinal);
    }

    private static (HttpStatusCode, string) DetailResponse(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/threads", StringComparison.Ordinal)) return (HttpStatusCode.OK, """
            {"value":[
              {"comments":[
                {"content":"Looks good overall.","author":{"displayName":"Linus"},"publishedDate":"2026-10-09T10:00:00Z","commentType":"text"},
                {"content":"Linus voted 10","author":{"displayName":"Linus"},"publishedDate":"2026-10-09T10:01:00Z","commentType":"system"}]},
              {"threadContext":{"filePath":"/src/App.cs","rightFileStart":{"line":12,"offset":1}},
               "comments":[{"content":"Rename this.","author":{"displayName":"Ada"},"publishedDate":"2026-10-09T11:00:00Z","commentType":"text"}]},
              {"isDeleted":true,"comments":[{"content":"gone","author":{"displayName":"Ada"},"publishedDate":"2026-10-09T12:00:00Z","commentType":"text"}]}
            ]}
            """);
        if (path.EndsWith("/commits", StringComparison.Ordinal)) return (HttpStatusCode.OK, """
            {"value":[{"commitId":"abc123","comment":"Add feature\n\nlong text","author":{"name":"Ada","date":"2026-10-08T09:00:00Z"}}]}
            """);
        if (path.EndsWith("/iterations", StringComparison.Ordinal)) return (HttpStatusCode.OK, """{"value":[{"id":1},{"id":2}]}""");
        if (path.EndsWith("/iterations/2/changes", StringComparison.Ordinal)) return (HttpStatusCode.OK, """
            {"changeEntries":[
              {"changeType":"edit","item":{"path":"/src/App.cs"}},
              {"changeType":"add","item":{"path":"/src/New.cs"}},
              {"changeType":"delete","item":{"path":"/src/Old.cs"}},
              {"changeType":"edit, rename","item":{"path":"/src/Moved.cs"},"originalPath":"/src/Before.cs"},
              {"changeType":"add","item":{"path":"/src","isFolder":true}}]}
            """);
        return (HttpStatusCode.OK, """
            {"pullRequestId":7,"title":"Add feature","description":"Does **a lot**.","status":"active","createdBy":{"displayName":"Ada"},
             "sourceRefName":"refs/heads/feature","targetRefName":"refs/heads/main","isDraft":false,"creationDate":"2026-10-08T10:00:00Z",
             "labels":[{"name":"backend","active":true},{"name":"old","active":false}],
             "reviewers":[{"displayName":"Linus","vote":10},{"displayName":"Grace","vote":-5},{"displayName":"Team","vote":0},{"displayName":"Group","vote":10,"isContainer":true}]}
            """);
    }

    [Fact]
    public async Task Reads_the_detail_of_a_pull_request()
    {
        var (client, handler) = Create(DetailResponse);
        var pull = new PullRequest(7, "Add feature", "Ada", "feature", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, pull, Ct)).Value).Value;

        Assert.True(client.SupportsPullRequestDetail);
        Assert.Equal("Does **a lot**.", detail.Body);
        Assert.Equal(PullRequestState.Open, detail.State);
        Assert.Equal(["backend"], detail.Labels);
        Assert.Equal("feature", detail.Summary.HeadBranch);

        // System comments and deleted threads are dropped; line comments carry their file and line; votes come last.
        Assert.Equal(
            [(TimelineKind.Comment, "Linus"), (TimelineKind.ReviewComment, "Ada"), (TimelineKind.Review, "Linus"), (TimelineKind.Review, "Grace")],
            detail.Timeline.Select(t => (t.Kind, t.Author)));
        var line = detail.Timeline[1];
        Assert.Equal("src/App.cs", line.Path);
        Assert.Equal(12, line.Line);
        Assert.Equal(ReviewVerdict.Approved, detail.Timeline[2].Verdict);
        Assert.Equal(ReviewVerdict.ChangesRequested, detail.Timeline[3].Verdict);

        var commit = Assert.Single(detail.Commits);
        Assert.Equal(("abc123", "Add feature", "Ada"), (commit.Sha, commit.Subject, commit.Author));

        Assert.Equal(
            [("src/App.cs", FileChangeKind.Modified, (string?)null), ("src/New.cs", FileChangeKind.Added, null),
             ("src/Old.cs", FileChangeKind.Deleted, null), ("src/Moved.cs", FileChangeKind.Renamed, "src/Before.cs")],
            detail.Files.Select(f => (f.Path, f.Kind, f.OldPath)));
        Assert.False(detail.IsTruncated);
        Assert.Contains(handler.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("/pullRequests/7/iterations/2/changes", StringComparison.Ordinal));
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.");
    }

    [Fact]
    public async Task A_request_that_times_out_is_reported_as_a_failure_not_thrown()
    {
        var client = new AzureDevOpsClient(new HttpClient(new TimeoutHandler()));
        NewReviewComment[] comments = [new("src/App.cs", 12, ReviewSide.New, "Slow")];

        var result = await client.SubmitReviewAsync("t", Atlas, Seven, ReviewDecision.Comment, string.Empty, comments, Ct);

        Assert.IsType<Failure>(result.Value);
    }

    [Fact]
    public async Task A_comment_on_a_selection_is_anchored_with_its_lines_and_columns()
    {
        var (client, handler) = Create(r => (HttpStatusCode.OK, """{"id":1}"""));
        NewReviewComment[] comments =
        [
            new("src/App.cs", 12, ReviewSide.New, "On the selection", StartLine: 10, StartColumn: 4, EndColumn: 20),
            new("src/Old.cs", 5, ReviewSide.Old, "On an old line", StartColumn: 3, EndColumn: 9),
        ];

        Assert.IsType<Success<Done>>((await client.SubmitReviewAsync("t", Atlas, Seven, ReviewDecision.Comment, string.Empty, comments, Ct)).Value);

        var posts = handler.Requests.Where(r => r.Request.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Contains("\"rightFileStart\":{\"line\":10,\"offset\":4}", posts[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"rightFileEnd\":{\"line\":12,\"offset\":20}", posts[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"leftFileStart\":{\"line\":5,\"offset\":3}", posts[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"leftFileEnd\":{\"line\":5,\"offset\":9}", posts[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thread_on_a_selection_keeps_its_lines_and_columns()
    {
        var (client, _) = Create(r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/threads", StringComparison.Ordinal)) return DetailResponse(r);
            return (HttpStatusCode.OK, """
                {"value":[
                  {"id":3,"status":"active","threadContext":{"filePath":"/src/App.cs","rightFileStart":{"line":10,"offset":4},"rightFileEnd":{"line":12,"offset":20}},
                   "comments":[{"id":1,"content":"Rename this.","author":{"displayName":"Ada"},"publishedDate":"2026-10-09T11:00:00Z","commentType":"text"}]}
                ]}
                """);
        });
        var pull = new PullRequest(7, "Add feature", "Ada", "feature", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, pull, Ct)).Value).Value;

        var thread = Assert.Single(detail.ReviewThreads);
        Assert.Equal((12, 10, 4, 20), (thread.Line, thread.StartLine, thread.StartColumn, thread.EndColumn));
    }

    [Fact]
    public async Task Threads_on_files_become_read_only_review_threads()
    {
        var (client, _) = Create(r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/threads", StringComparison.Ordinal)) return DetailResponse(r);
            return (HttpStatusCode.OK, """
                {"value":[
                  {"id":3,"status":"active","threadContext":{"filePath":"/src/App.cs","rightFileStart":{"line":12}},
                   "comments":[{"id":1,"content":"Rename this.","author":{"displayName":"Ada"},"publishedDate":"2026-10-09T11:00:00Z","commentType":"text"},
                               {"id":2,"content":"Done","author":{"displayName":"Bob"},"publishedDate":"2026-10-09T11:05:00Z","commentType":"text"},
                               {"id":3,"content":"Bob updated the status","author":{"displayName":"Bob"},"publishedDate":"2026-10-09T11:06:00Z","commentType":"system"}]},
                  {"id":4,"status":"fixed","threadContext":{"filePath":"/src/Old.cs","leftFileStart":{"line":5}},
                   "comments":[{"id":1,"content":"Why?","author":{"displayName":"Ada"},"publishedDate":"2026-10-09T12:00:00Z","commentType":"text"}]},
                  {"id":5,"status":"active","comments":[{"id":1,"content":"General remark","author":{"displayName":"Ada"},"publishedDate":"2026-10-09T13:00:00Z","commentType":"text"}]},
                  {"id":6,"status":"active","threadContext":{"filePath":"/src/Empty.cs","rightFileStart":{"line":1}},
                   "comments":[{"id":1,"content":"x","commentType":"system","publishedDate":"2026-10-09T13:00:00Z"}]}
                ]}
                """);
        });
        var pull = new PullRequest(7, "Add feature", "Ada", "feature", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, pull, Ct)).Value).Value;

        Assert.Equal(2, detail.ReviewThreads.Count);
        var open = detail.ReviewThreads[0];
        Assert.Equal(("3", "src/App.cs", 12, ReviewSide.New, false), (open.Id, open.Path, open.Line, open.Side, open.IsResolved));
        Assert.Equal(["Rename this.", "Done"], open.Comments.Select(c => c.Body));
        var fixedThread = detail.ReviewThreads[1];
        Assert.Equal(("src/Old.cs", 5, ReviewSide.Old, true), (fixedThread.Path, fixedThread.Line, fixedThread.Side, fixedThread.IsResolved));
    }

    [Theory]
    [InlineData(ReviewVote.Approved, 10)]
    [InlineData(ReviewVote.ApprovedWithSuggestions, 5)]
    [InlineData(ReviewVote.WaitingForAuthor, -5)]
    [InlineData(ReviewVote.Rejected, -10)]
    [InlineData(ReviewVote.None, 0)]
    public async Task A_vote_is_put_on_the_reviewer_of_the_signed_in_identity(ReviewVote vote, int expected)
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/connectionData", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"authenticatedUser":{"id":"guid-1","providerDisplayName":"Ada"}}""")
            : (HttpStatusCode.OK, """{"vote":0}"""));
        var pull = new PullRequest(7, "T", "Ada", "f", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        Assert.True(client.SupportsPullRequestVote);
        Assert.IsType<Success<Done>>((await client.SetPullRequestVoteAsync("t", Atlas, pull, vote, Ct)).Value);

        var (put, body) = handler.Requests[^1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.EndsWith("/pullRequests/7/reviewers/guid-1", put.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal($$"""{"vote":{{expected}}}""", body);
        Assert.Contains("/contoso/_apis/connectionData", handler.Requests[0].Request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vote_without_a_known_identity_or_with_a_refusal_fails()
    {
        var pull = new PullRequest(7, "T", "Ada", "f", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        var (noIdentity, _) = Create(_ => (HttpStatusCode.OK, """{"authenticatedUser":{}}"""));
        Assert.IsType<Failure>((await noIdentity.SetPullRequestVoteAsync("t", Atlas, pull, ReviewVote.Approved, Ct)).Value);

        var (refused, _) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/connectionData", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"authenticatedUser":{"id":"guid-1"}}""")
            : (HttpStatusCode.BadRequest, """{"message":"TF401027: permission needed"}"""));
        var failure = Assert.IsType<Failure>((await refused.SetPullRequestVoteAsync("t", Atlas, pull, ReviewVote.Approved, Ct)).Value);
        Assert.Contains("TF401027", failure.Message, StringComparison.Ordinal);
    }

    private static readonly PullRequest Seven = new(7, "T", "Ada", "f", null, "main", false, DateTimeOffset.UnixEpoch, "u");

    private static ReviewThread AzureThread(string id = "3") =>
        new(id, "src/App.cs", 12, ReviewSide.New, false, false, [new ReviewThreadComment(1, "Ada", "Rename this.", DateTimeOffset.UnixEpoch)]);

    [Fact]
    public async Task A_reply_is_posted_under_the_first_comment_of_the_thread()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK,
            """{"id":2,"content":"Done","author":{"displayName":"Bob"},"publishedDate":"2026-10-09T11:05:00Z","commentType":"text"}"""));

        var reply = Assert.IsType<Success<ReviewThreadComment>>((await client.ReplyToReviewThreadAsync("t", Atlas, Seven, AzureThread(), "Done", Ct)).Value).Value;

        Assert.Equal((2L, "Bob", "Done"), (reply.Id, reply.Author, reply.Body));
        var (post, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.EndsWith("/pullRequests/7/threads/3/comments", post.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"Done\"", body, StringComparison.Ordinal);
        Assert.Contains("\"parentCommentId\":1", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "fixed")]
    [InlineData(false, "active")]
    public async Task Resolving_a_thread_patches_its_status(bool resolved, string status)
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"id":3}"""));

        Assert.IsType<Success<Done>>((await client.SetReviewThreadResolvedAsync("t", Atlas, Seven, AzureThread(), resolved, Ct)).Value);

        var (patch, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.EndsWith("/pullRequests/7/threads/3", patch.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal($$"""{"status":"{{status}}"}""", body);
    }

    [Fact]
    public async Task A_review_posts_one_thread_per_comment_then_votes()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/connectionData", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"authenticatedUser":{"id":"guid-1"}}""")
            : (HttpStatusCode.OK, """{"id":1}"""));
        NewReviewComment[] comments =
        [
            new("src/App.cs", 12, ReviewSide.New, "On the new line"),
            new("src/Old.cs", 5, ReviewSide.Old, "On the old line"),
        ];

        Assert.IsType<Success<Done>>((await client.SubmitReviewAsync("t", Atlas, Seven, ReviewDecision.RequestChanges, "Please fix", comments, Ct)).Value);

        var posts = handler.Requests.Where(r => r.Request.Method == HttpMethod.Post).ToList();
        Assert.Equal(3, posts.Count);
        Assert.All(posts, p => Assert.EndsWith("/pullRequests/7/threads", p.Request.RequestUri!.AbsolutePath, StringComparison.Ordinal));
        Assert.DoesNotContain("threadContext", posts[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"filePath\":\"/src/App.cs\"", posts[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"rightFileStart\":{\"line\":12,\"offset\":1}", posts[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("leftFile", posts[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"leftFileStart\":{\"line\":5,\"offset\":1}", posts[2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("rightFile", posts[2].Body, StringComparison.Ordinal);
        var (put, vote) = handler.Requests[^1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("""{"vote":-5}""", vote);
    }

    [Fact]
    public async Task A_comment_only_review_does_not_vote_and_a_refused_thread_fails()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"id":1}"""));
        Assert.IsType<Success<Done>>((await client.SubmitReviewAsync("t", Atlas, Seven, ReviewDecision.Comment, "Remark", [], Ct)).Value);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Put);

        var (refused, _) = Create(_ => (HttpStatusCode.BadRequest, """{"message":"TF401027: permission needed"}"""));
        var failure = Assert.IsType<Failure>((await refused.SubmitReviewAsync("t", Atlas, Seven, ReviewDecision.Approve, "Remark", [], Ct)).Value);
        Assert.Contains("TF401027", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_general_comment_is_a_thread_without_context()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK,
            """{"id":9,"comments":[{"id":1,"content":"Looks good","author":{"displayName":"Bob"},"publishedDate":"2026-10-09T11:05:00Z","commentType":"text"}]}"""));

        var entry = Assert.IsType<Success<TimelineEntry>>((await client.AddPullRequestCommentAsync("t", Atlas, Seven, "Looks good", Ct)).Value).Value;

        Assert.Equal((TimelineKind.Comment, "Bob", "Looks good"), (entry.Kind, entry.Author, entry.Body));
        var (post, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.EndsWith("/pullRequests/7/threads", post.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"Looks good\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("threadContext", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_general_comment_answered_without_comments_or_refused_fails()
    {
        var (empty, _) = Create(_ => (HttpStatusCode.OK, """{"id":9}"""));
        Assert.IsType<Failure>((await empty.AddPullRequestCommentAsync("t", Atlas, Seven, "x", Ct)).Value);

        var (refused, _) = Create(_ => (HttpStatusCode.BadRequest, """{"message":"TF401027: permission needed"}"""));
        var failure = Assert.IsType<Failure>((await refused.AddPullRequestCommentAsync("t", Atlas, Seven, "x", Ct)).Value);
        Assert.Contains("TF401027", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, """{"status":"abandoned"}""")]
    [InlineData(true, """{"status":"active"}""")]
    public async Task Closing_or_reopening_patches_the_status(bool open, string expected)
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"pullRequestId":7}"""));

        Assert.IsType<Success<Done>>((await client.SetPullRequestOpenAsync("t", Atlas, Seven, open, Ct)).Value);

        var (patch, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.EndsWith("/pullRequests/7", patch.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(expected, body);
    }

    [Theory]
    [InlineData(true, """{"isDraft":true}""")]
    [InlineData(false, """{"isDraft":false}""")]
    public async Task Changing_the_draft_state_patches_is_draft(bool draft, string expected)
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"pullRequestId":7}"""));
        var detail = new PullRequestDetail(Seven, string.Empty, PullRequestState.Open, [], [], [], []);

        Assert.IsType<Success<Done>>((await client.SetPullRequestDraftAsync("t", Atlas, detail, draft, Ct)).Value);

        Assert.Equal(expected, Assert.Single(handler.Requests).Body);
    }

    [Theory]
    [InlineData(MergeMethod.Merge, "noFastForward")]
    [InlineData(MergeMethod.Squash, "squash")]
    [InlineData(MergeMethod.Rebase, "rebase")]
    public async Task Merging_completes_the_pull_request_at_its_latest_source_commit(MergeMethod method, string strategy)
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """{"pullRequestId":7,"title":"T","lastMergeSourceCommit":{"commitId":"abc123"},"mergeStatus":"queued"}"""));

        Assert.IsType<Success<Done>>((await client.MergePullRequestAsync("t", Atlas, Seven, method, Ct)).Value);

        Assert.Equal([HttpMethod.Get, HttpMethod.Patch], handler.Requests.Select(r => r.Request.Method));
        Assert.Equal(
            $$$"""{"status":"completed","lastMergeSourceCommit":{"commitId":"abc123"},"completionOptions":{"mergeStrategy":"{{{strategy}}}","deleteSourceBranch":false}}""",
            handler.Requests[1].Body);
    }

    [Fact]
    public async Task Merging_without_a_source_commit_or_with_conflicts_fails()
    {
        var (blind, blindHandler) = Create(_ => (HttpStatusCode.OK, """{"pullRequestId":7,"title":"T"}"""));
        Assert.IsType<Failure>((await blind.MergePullRequestAsync("t", Atlas, Seven, MergeMethod.Merge, Ct)).Value);
        Assert.Single(blindHandler.Requests);

        var (conflicted, _) = Create(r => r.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, """{"pullRequestId":7,"title":"T","lastMergeSourceCommit":{"commitId":"abc123"}}""")
            : (HttpStatusCode.OK, """{"pullRequestId":7,"title":"T","mergeStatus":"conflicts","mergeFailureMessage":"Conflicts in a.txt"}"""));
        var failure = Assert.IsType<Failure>((await conflicted.MergePullRequestAsync("t", Atlas, Seven, MergeMethod.Merge, Ct)).Value);
        Assert.Contains("a.txt", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_state_change_fails()
    {
        var (client, _) = Create(_ => (HttpStatusCode.BadRequest, """{"message":"TF401027: permission needed"}"""));

        var failure = Assert.IsType<Failure>((await client.SetPullRequestOpenAsync("t", Atlas, Seven, false, Ct)).Value);

        Assert.Contains("TF401027", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("completed", PullRequestState.Merged)]
    [InlineData("abandoned", PullRequestState.Closed)]
    public async Task A_completed_or_abandoned_pull_request_is_merged_or_closed(string status, PullRequestState expected)
    {
        var (client, _) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/7", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, $$"""{"pullRequestId":7,"title":"T","status":"{{status}}","creationDate":"2026-10-08T10:00:00Z"}""")
            : (HttpStatusCode.OK, """{"value":[]}"""));
        var pull = new PullRequest(7, "T", "Ada", "f", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        var detail = Assert.IsType<Success<PullRequestDetail>>((await client.GetPullRequestDetailAsync("t", Atlas, pull, Ct)).Value).Value;

        Assert.Equal(expected, detail.State);
        Assert.Empty(detail.Files);
    }

    private const string IterationsJson =
        """{"value":[{"id":1,"commonRefCommit":{"commitId":"old0"},"sourceRefCommit":{"commitId":"src1"}},{"id":2,"commonRefCommit":{"commitId":"base"},"sourceRefCommit":{"commitId":"head"}}]}""";

    private static (HttpStatusCode, string) ItemsResponse(HttpRequestMessage r)
    {
        var url = Uri.UnescapeDataString(r.RequestUri!.PathAndQuery);
        if (url.Contains("/iterations?", StringComparison.Ordinal)) return (HttpStatusCode.OK, IterationsJson);
        if (url.Contains("path=/src/a.cs", StringComparison.Ordinal))
            return url.Contains("version=base", StringComparison.Ordinal) ? (HttpStatusCode.OK, "old\nsame\n") : (HttpStatusCode.OK, "new\nsame\n");
        return (HttpStatusCode.NotFound, "{}");
    }

    [Fact]
    public async Task Reads_both_versions_of_a_file_at_the_common_ancestor_and_the_last_source_commit()
    {
        var (client, handler) = Create(ItemsResponse);
        var pull = new PullRequest(7, "T", "Ada", "f", null, "main", false, DateTimeOffset.UnixEpoch, "u");
        var file = new PullRequestFile("src/a.cs", null, FileChangeKind.Modified, 0, 0, null);

        var sides = Assert.IsType<Success<DiffSides>>((await client.GetPullRequestFileSidesAsync("t", Atlas, pull, file, Ct)).Value).Value;

        Assert.True(client.SupportsPullRequestSides);
        Assert.True(client.ComputesPullRequestDiffs);
        Assert.Equal(("old\nsame\n", "new\nsame\n"), (sides.Original, sides.Modified));
        var items = handler.Requests.Select(r => Uri.UnescapeDataString(r.Request.RequestUri!.Query)).Where(q => q.Contains("path=", StringComparison.Ordinal)).ToList();
        Assert.Contains(items, q => q.Contains("version=base", StringComparison.Ordinal) && q.Contains("versionType=commit", StringComparison.Ordinal));
        Assert.Contains(items, q => q.Contains("version=head", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_added_or_deleted_file_has_one_empty_side_and_a_missing_file_reads_as_empty()
    {
        var (client, handler) = Create(ItemsResponse);
        var pull = new PullRequest(7, "T", "Ada", "f", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        var added = Assert.IsType<Success<DiffSides>>(
            (await client.GetPullRequestFileSidesAsync("t", Atlas, pull, new PullRequestFile("src/a.cs", null, FileChangeKind.Added, 0, 0, null), Ct)).Value).Value;
        var deleted = Assert.IsType<Success<DiffSides>>(
            (await client.GetPullRequestFileSidesAsync("t", Atlas, pull, new PullRequestFile("src/a.cs", null, FileChangeKind.Deleted, 0, 0, null), Ct)).Value).Value;
        var missing = Assert.IsType<Success<DiffSides>>(
            (await client.GetPullRequestFileSidesAsync("t", Atlas, pull, new PullRequestFile("src/none.cs", null, FileChangeKind.Modified, 0, 0, null), Ct)).Value).Value;

        Assert.Equal((string.Empty, "new\nsame\n"), (added.Original, added.Modified));
        Assert.Equal(("old\nsame\n", string.Empty), (deleted.Original, deleted.Modified));
        Assert.Equal((string.Empty, string.Empty), (missing.Original, missing.Modified));
        Assert.NotEmpty(handler.Requests);
    }

    [Fact]
    public async Task A_binary_file_has_no_text_diff()
    {
        var (client, _) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/iterations", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, IterationsJson)
            : (HttpStatusCode.OK, "PNG\0data"));
        var pull = new PullRequest(7, "T", "Ada", "f", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        var result = await client.GetPullRequestFileSidesAsync("t", Atlas, pull, new PullRequestFile("logo.png", null, FileChangeKind.Modified, 0, 0, null), Ct);

        Assert.Contains("binaire", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_token_fails_the_detail()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.NonAuthoritativeInformation, "<html>Sign in</html>", "text/html"));
        var client = new AzureDevOpsClient(new HttpClient(handler));
        var pull = new PullRequest(7, "T", "Ada", "f", null, "main", false, DateTimeOffset.UnixEpoch, "u");

        Assert.IsType<AuthenticationFailed>((await client.GetPullRequestDetailAsync("bad", Atlas, pull, Ct)).Value);
    }
}
