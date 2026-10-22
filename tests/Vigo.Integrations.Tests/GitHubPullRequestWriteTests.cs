using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

/// <summary>Writes on a GitHub pull request (J6.9b): comments, close / reopen, draft state, merge.</summary>
public sealed class GitHubPullRequestWriteTests
{
    private static readonly HostRepository Atlas = new(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly PullRequest Listed = new(7, "Fix cache", "octocat", "fix/cache", null, "main", false, Now, "https://github.com/atlas/atlas-api/pull/7");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Api = "https://api.github.com/repos/atlas/atlas-api";

    private sealed record Call(HttpMethod Method, string Url, string Body);

    private sealed class Recorder(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.Method, request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }

    private static (GitHubClient Client, Recorder Recorder) Create(HttpStatusCode status = HttpStatusCode.OK, string json = "{}")
    {
        var recorder = new Recorder(status, json);
        return (new GitHubClient(new HttpClient(recorder), "client-1"), recorder);
    }

    private static PullRequestDetail DetailOf(string? nodeId) =>
        new(Listed, string.Empty, PullRequestState.Open, [], [], [], [], NodeId: nodeId);

    [Fact]
    public async Task Adding_a_comment_posts_it_on_the_issue_and_returns_the_stored_entry()
    {
        var (client, recorder) = Create(HttpStatusCode.Created, """{"id":99,"user":{"login":"octocat"},"body":"Thanks","created_at":"2026-10-12T09:00:00Z"}""");

        var result = await client.AddPullRequestCommentAsync("tok", Atlas, Listed, "Thanks", Ct);

        var entry = Assert.IsType<Success<TimelineEntry>>(result.Value).Value;
        Assert.Equal((99L, "octocat", "Thanks"), (entry.Id, entry.Author, entry.Body));
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Post, Api + "/issues/7/comments"), (call.Method, call.Url));
        Assert.Contains("\"body\":\"Thanks\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commenting_on_an_issue_posts_to_its_number_and_returns_the_stored_entry()
    {
        var (client, recorder) = Create(HttpStatusCode.Created, """{"id":77,"user":{"login":"octocat"},"body":"Same here","created_at":"2026-10-12T09:00:00Z"}""");
        var issue = new Issue(5, "Cache", "ada", Now, "https://github.com/atlas/atlas-api/issues/5");

        var result = await client.AddIssueCommentAsync("tok", Atlas, issue, "Same here", Ct);

        Assert.True(client.SupportsIssueWrite);
        var entry = Assert.IsType<Success<TimelineEntry>>(result.Value).Value;
        Assert.Equal((77L, "octocat", "Same here"), (entry.Id, entry.Author, entry.Body));
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Post, Api + "/issues/5/comments"), (call.Method, call.Url));
        Assert.Contains("\"body\":\"Same here\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_an_issue_posts_title_and_body_and_returns_the_stored_issue()
    {
        var (client, recorder) = Create(HttpStatusCode.Created,
            """{"number":12,"title":"Cache never expires","user":{"login":"octocat"},"updated_at":"2026-10-12T09:00:00Z","html_url":"https://github.com/atlas/atlas-api/issues/12"}""");

        var result = await client.CreateIssueAsync("tok", Atlas, new NewIssue("Cache never expires", "Seen on main."), Ct);

        var issue = Assert.IsType<Success<Issue>>(result.Value).Value;
        Assert.Equal((12, "Cache never expires", "octocat", "https://github.com/atlas/atlas-api/issues/12"), (issue.Number, issue.Title, issue.Author, issue.Url));
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Post, Api + "/issues"), (call.Method, call.Url));
        Assert.Contains("\"title\":\"Cache never expires\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"Seen on main.\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_issue_creation_is_a_failure_with_the_reason()
    {
        var (client, _) = Create(HttpStatusCode.Gone, """{"message":"Issues are disabled for this repo"}""");

        var result = await client.CreateIssueAsync("tok", Atlas, new NewIssue("T", ""), Ct);

        Assert.Contains("disabled", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, IssueCloseReason.Completed, "closed", "completed")]
    [InlineData(false, IssueCloseReason.NotPlanned, "closed", "not_planned")]
    [InlineData(true, IssueCloseReason.Completed, "open", "reopened")]
    public async Task Closing_or_reopening_an_issue_patches_its_state_and_reason(bool open, IssueCloseReason reason, string state, string stateReason)
    {
        var (client, recorder) = Create();
        var issue = new Issue(5, "Cache", "ada", Now, "https://github.com/atlas/atlas-api/issues/5");

        var result = await client.SetIssueOpenAsync("tok", Atlas, issue, open, reason, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Patch, Api + "/issues/5"), (call.Method, call.Url));
        Assert.Contains($"\"state\":\"{state}\"", call.Body, StringComparison.Ordinal);
        Assert.Contains($"\"state_reason\":\"{stateReason}\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_the_text_of_an_issue_patches_its_title_and_description()
    {
        var (client, recorder) = Create();
        var issue = new Issue(5, "Cache", "ada", Now, "https://github.com/atlas/atlas-api/issues/5");

        var result = await client.EditIssueTextAsync("tok", Atlas, issue, "Cache never expires", "Seen on **main**.", Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Patch, Api + "/issues/5"), (call.Method, call.Url));
        Assert.Contains("\"title\":\"Cache never expires\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"Seen on **main**.\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_the_metadata_of_an_issue_replaces_labels_assignees_and_milestone()
    {
        var (client, recorder) = Create();
        var issue = new Issue(5, "Cache", "ada", Now, "https://github.com/atlas/atlas-api/issues/5");

        var result = await client.SetIssueMetadataAsync("tok", Atlas, issue, new IssueMetadata(["bug", "cache"], ["ada"], 3), Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Patch, Api + "/issues/5"), (call.Method, call.Url));
        Assert.Contains("\"labels\":[\"bug\",\"cache\"]", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"assignees\":[\"ada\"]", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"milestone\":3", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_the_milestone_and_everything_else_writes_empty_lists_and_a_null_milestone()
    {
        var (client, recorder) = Create();
        var issue = new Issue(5, "Cache", "ada", Now, "https://github.com/atlas/atlas-api/issues/5");

        await client.SetIssueMetadataAsync("tok", Atlas, issue, new IssueMetadata([], [], null), Ct);

        var call = Assert.Single(recorder.Calls);
        Assert.Contains("\"labels\":[]", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"assignees\":[]", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"milestone\":null", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_metadata_change_is_a_failure_with_the_reason()
    {
        var (client, _) = Create(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}""");
        var issue = new Issue(5, "Cache", "ada", Now, "https://github.com/atlas/atlas-api/issues/5");

        var result = await client.SetIssueMetadataAsync("tok", Atlas, issue, new IssueMetadata(["bug"], [], null), Ct);

        Assert.Contains("Validation Failed", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }

    private sealed class Router(Func<string, (HttpStatusCode Status, string Json)> answer) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Urls) Urls.Add(url);
            var (status, json) = answer(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }

    [Fact]
    public async Task The_options_of_the_repository_are_its_labels_assignable_accounts_and_milestones()
    {
        var router = new Router(url => (HttpStatusCode.OK, url switch
        {
            _ when url.Contains("/labels", StringComparison.Ordinal) => """[{"name":"cache"},{"name":"Bug"}]""",
            _ when url.Contains("/assignees", StringComparison.Ordinal) => """[{"login":"linus"},{"login":"ada"}]""",
            _ => """[{"number":3,"title":"v1.0"},{"number":4,"title":"v2.0"}]""",
        }));
        var client = new GitHubClient(new HttpClient(router), "client-1");

        var result = await client.GetIssueMetadataOptionsAsync("tok", Atlas, Ct);

        var options = Assert.IsType<Success<IssueMetadataOptions>>(result.Value).Value;
        Assert.Equal(["Bug", "cache"], options.Labels);
        Assert.Equal(["ada", "linus"], options.Assignees);
        Assert.Equal([(3, "v1.0"), (4, "v2.0")], options.Milestones.Select(m => (m.Number, m.Title)));
        Assert.False(options.IsTruncated);
        Assert.Contains(Api + "/milestones?state=all&per_page=100", router.Urls);
    }

    [Fact]
    public async Task A_refused_options_read_is_a_failure()
    {
        var router = new Router(_ => (HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        var client = new GitHubClient(new HttpClient(router), "client-1");

        var result = await client.GetIssueMetadataOptionsAsync("tok", Atlas, Ct);

        Assert.IsType<Failure>(result.Value);
    }

    [Fact]
    public async Task Pictures_are_downloaded_with_the_token_from_github_hosts_only()
    {
        var (client, recorder) = Create(json: "PNGDATA");

        var own = await client.DownloadImageAsync("tok", "https://github.com/user-attachments/assets/abc", Ct);
        var other = await client.DownloadImageAsync("tok", "https://example.com/a.png", Ct);
        var insecure = await client.DownloadImageAsync("tok", "http://github.com/a.png", Ct);

        Assert.Equal("PNGDATA"u8.ToArray(), Assert.IsType<Success<byte[]>>(own.Value).Value);
        Assert.IsType<Failure>(other.Value);
        Assert.IsType<Failure>(insecure.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal("https://github.com/user-attachments/assets/abc", call.Url);
    }

    [Fact]
    public async Task Editing_a_comment_patches_it_by_identifier()
    {
        var (client, recorder) = Create(json: """{"id":99,"user":{"login":"octocat"},"body":"New","created_at":"2026-10-12T09:00:00Z"}""");

        var result = await client.EditPullRequestCommentAsync("tok", Atlas, 99, "New", Ct);

        Assert.Equal("New", Assert.IsType<Success<TimelineEntry>>(result.Value).Value.Body);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Patch, Api + "/issues/comments/99"), (call.Method, call.Url));
    }

    [Fact]
    public async Task Deleting_a_comment_sends_a_delete()
    {
        var (client, recorder) = Create(HttpStatusCode.NoContent, string.Empty);

        var result = await client.DeletePullRequestCommentAsync("tok", Atlas, 99, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        Assert.Equal((HttpMethod.Delete, Api + "/issues/comments/99"), (recorder.Calls[0].Method, recorder.Calls[0].Url));
    }

    [Theory]
    [InlineData(false, "closed")]
    [InlineData(true, "open")]
    public async Task Closing_and_reopening_patch_the_state(bool open, string state)
    {
        var (client, recorder) = Create();

        var result = await client.SetPullRequestOpenAsync("tok", Atlas, Listed, open, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Patch, Api + "/pulls/7"), (call.Method, call.Url));
        Assert.Contains($"\"state\":\"{state}\"", call.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MergeMethod.Merge, "merge")]
    [InlineData(MergeMethod.Squash, "squash")]
    [InlineData(MergeMethod.Rebase, "rebase")]
    public async Task Merging_puts_the_chosen_method(MergeMethod method, string name)
    {
        var (client, recorder) = Create();

        var result = await client.MergePullRequestAsync("tok", Atlas, Listed, method, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Put, Api + "/pulls/7/merge"), (call.Method, call.Url));
        Assert.Contains($"\"merge_method\":\"{name}\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updating_the_branch_puts_to_update_branch()
    {
        var (client, recorder) = Create(HttpStatusCode.Accepted, """{"message":"Updating pull request branch."}""");

        var result = await client.UpdatePullRequestBranchAsync("tok", Atlas, Listed, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Put, Api + "/pulls/7/update-branch"), (call.Method, call.Url));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Deleting_a_branch_counts_an_already_deleted_one_as_done(HttpStatusCode status)
    {
        var (client, recorder) = Create(status, """{"message":"Reference does not exist"}""");

        var result = await client.DeleteBranchAsync("tok", Atlas, "fix/cache", Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Delete, Api + "/git/refs/heads/fix/cache"), (call.Method, call.Url));
    }

    [Fact]
    public async Task Deleting_a_branch_reports_a_refusal()
    {
        var (client, _) = Create(HttpStatusCode.Forbidden, """{"message":"Resource not accessible"}""");

        Assert.IsType<AuthenticationFailed>((await client.DeleteBranchAsync("tok", Atlas, "fix/cache", Ct)).Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task A_branch_exists_when_its_ref_is_found(HttpStatusCode status, bool exists)
    {
        var (client, recorder) = Create(status);

        var result = await client.BranchExistsAsync("tok", Atlas, "fix/cache", Ct);

        Assert.Equal(exists, Assert.IsType<Success<bool>>(result.Value).Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Get, Api + "/git/ref/heads/fix/cache"), (call.Method, call.Url));
    }

    [Theory]
    [InlineData(true, "convertPullRequestToDraft")]
    [InlineData(false, "markPullRequestReadyForReview")]
    public async Task The_draft_state_goes_through_a_graphql_mutation(bool draft, string mutation)
    {
        var (client, recorder) = Create(json: """{"data":{}}""");

        var result = await client.SetPullRequestDraftAsync("tok", Atlas, DetailOf("PR_node"), draft, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Post, "https://api.github.com/graphql"), (call.Method, call.Url));
        Assert.Contains(mutation, call.Body, StringComparison.Ordinal);
        Assert.Contains("PR_node", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_graphql_error_in_a_200_answer_is_a_failure()
    {
        var (client, _) = Create(json: """{"errors":[{"message":"Pull request is in a bad state"}]}""");

        var result = await client.SetPullRequestDraftAsync("tok", Atlas, DetailOf("PR_node"), true, Ct);

        Assert.Contains("bad state", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_draft_change_without_the_graph_identifier_does_not_call_the_service()
    {
        var (client, recorder) = Create();

        var result = await client.SetPullRequestDraftAsync("tok", Atlas, DetailOf(null), true, Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.Empty(recorder.Calls);
    }

    [Theory]
    [InlineData(MergeMethod.Squash, "SQUASH")]
    [InlineData(MergeMethod.Rebase, "REBASE")]
    [InlineData(MergeMethod.Merge, "MERGE")]
    public async Task Enabling_auto_merge_goes_through_a_graphql_mutation_with_the_method(MergeMethod method, string name)
    {
        var (client, recorder) = Create(json: """{"data":{}}""");

        var result = await client.SetPullRequestAutoMergeAsync("tok", Atlas, DetailOf("PR_node"), method, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((HttpMethod.Post, "https://api.github.com/graphql"), (call.Method, call.Url));
        Assert.Contains("enablePullRequestAutoMerge", call.Body, StringComparison.Ordinal);
        Assert.Contains($"\"method\":\"{name}\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabling_auto_merge_sends_no_method()
    {
        var (client, recorder) = Create(json: """{"data":{}}""");

        var result = await client.SetPullRequestAutoMergeAsync("tok", Atlas, DetailOf("PR_node"), null, Ct);

        Assert.IsType<Success<Done>>(result.Value);
        var call = Assert.Single(recorder.Calls);
        Assert.Contains("disablePullRequestAutoMerge", call.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"method\":", call.Body, StringComparison.Ordinal);
    }

    private sealed class Script(params (HttpStatusCode Status, string Json)[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.Method, request.RequestUri!.ToString(), body));
            var (status, json) = answers[_next++];
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }

    private const string CommentJson = """{"id":99,"user":{"login":"octocat"},"body":"Hi","created_at":"2026-10-12T09:00:00Z","reactions":{"+1":2,"heart":1,"eyes":0}}""";

    [Fact]
    public async Task A_reaction_the_account_has_not_given_is_added_and_the_comment_read_again()
    {
        var script = new Script((HttpStatusCode.OK, """[{"id":5,"user":{"login":"ada"}}]"""), (HttpStatusCode.Created, "{}"), (HttpStatusCode.OK, CommentJson));
        var client = new GitHubClient(new HttpClient(script), "client-1");

        var result = await client.ToggleCommentReactionAsync("tok", Atlas, 99, ReactionKind.ThumbsUp, "octocat", Ct);

        var entry = Assert.IsType<Success<TimelineEntry>>(result.Value).Value;
        Assert.Equal([new ReactionCount(ReactionKind.ThumbsUp, 2), new ReactionCount(ReactionKind.Heart, 1)], entry.Reactions);
        Assert.Equal((HttpMethod.Get, Api + "/issues/comments/99/reactions?content=%2B1&per_page=100"), (script.Calls[0].Method, script.Calls[0].Url));
        Assert.Equal((HttpMethod.Post, Api + "/issues/comments/99/reactions"), (script.Calls[1].Method, script.Calls[1].Url));
        Assert.Contains("\"content\":\"\\u002B1\"", script.Calls[1].Body, StringComparison.Ordinal);
        Assert.Equal(Api + "/issues/comments/99", script.Calls[2].Url);
    }

    [Fact]
    public async Task A_reaction_the_account_already_gave_is_removed()
    {
        var script = new Script((HttpStatusCode.OK, """[{"id":5,"user":{"login":"ada"}},{"id":6,"user":{"login":"Octocat"}}]"""), (HttpStatusCode.NoContent, string.Empty), (HttpStatusCode.OK, CommentJson));
        var client = new GitHubClient(new HttpClient(script), "client-1");

        var result = await client.ToggleCommentReactionAsync("tok", Atlas, 99, ReactionKind.Heart, "octocat", Ct);

        Assert.IsType<Success<TimelineEntry>>(result.Value);
        Assert.Equal((HttpMethod.Delete, Api + "/issues/comments/99/reactions/6"), (script.Calls[1].Method, script.Calls[1].Url));
    }

    [Fact]
    public async Task A_refused_reaction_stops_before_the_comment_is_read_again()
    {
        var script = new Script((HttpStatusCode.OK, "[]"), (HttpStatusCode.Forbidden, """{"message":"Resource not accessible"}"""));
        var client = new GitHubClient(new HttpClient(script), "client-1");

        var result = await client.ToggleCommentReactionAsync("tok", Atlas, 99, ReactionKind.Rocket, "octocat", Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.Equal(2, script.Calls.Count);
    }

    [Fact]
    public async Task A_refusal_comes_back_as_a_failure_with_the_service_message()
    {
        var (client, _) = Create(HttpStatusCode.MethodNotAllowed, """{"message":"Pull Request is not mergeable"}""");

        var result = await client.MergePullRequestAsync("tok", Atlas, Listed, MergeMethod.Merge, Ct);

        Assert.Contains("not mergeable", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }
}
