using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Integrations.GitHub;

namespace Vigo.Integrations.Tests;

/// <summary>Detail of an issue read from GitHub (J6.10a): description, labels, assignees, milestone, comments, events, paging.</summary>
public sealed class GitHubIssueDetailTests
{
    private static readonly HostRepository Atlas = new(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Issue Listed = new(5, "Cache misses", "octocat", Now, "https://github.com/atlas/atlas-api/issues/5");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Routes : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Json, string? Link)> Answers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, json, link) = Answers.TryGetValue(request.RequestUri!.ToString(), out var known) ? known : (HttpStatusCode.NotFound, "{}", null);
            var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (link is not null) response.Headers.TryAddWithoutValidation("Link", link);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private const string IssueApi = "https://api.github.com/repos/atlas/atlas-api/issues/5";

    private static (GitHubClient Client, Routes Routes) Create()
    {
        var routes = new Routes();
        routes.Answers[IssueApi] = (HttpStatusCode.OK, """
            {"number":5,"title":"Cache misses","user":{"login":"octocat"},"updated_at":"2026-10-12T08:00:00Z",
             "html_url":"https://github.com/atlas/atlas-api/issues/5","body":"The cache **misses**.","state":"open",
             "labels":[{"name":"bug"},{"name":"cache"}],"assignees":[{"login":"ada"}],"milestone":{"title":"v1.0"}}
            """, null);
        routes.Answers[IssueApi + "/comments?per_page=100"] = (HttpStatusCode.OK, """
            [{"user":{"login":"ada"},"body":"Reproduced","created_at":"2026-10-12T08:30:00Z","id":11,"reactions":{"+1":2,"heart":1}}]
            """, null);
        routes.Answers[IssueApi + "/events?per_page=100"] = (HttpStatusCode.OK, """
            [{"event":"labeled","created_at":"2026-10-12T08:10:00Z","actor":{"login":"grace"},"label":{"name":"bug"}},
             {"event":"subscribed","created_at":"2026-10-12T08:11:00Z","actor":{"login":"grace"}},
             {"event":"closed","created_at":"2026-10-12T08:40:00Z","actor":{"login":"ada"}}]
            """, null);
        return (new GitHubClient(new HttpClient(routes), "client-1"), routes);
    }

    [Fact]
    public async Task Reads_the_description_the_state_the_labels_the_assignees_and_the_milestone()
    {
        var (client, _) = Create();

        var result = await client.GetIssueDetailAsync("t", Atlas, Listed, Ct);

        var detail = Assert.IsType<Success<IssueDetail>>(result.Value).Value;
        Assert.Equal("The cache **misses**.", detail.Body);
        Assert.Equal(IssueState.Open, detail.State);
        Assert.False(detail.NotPlanned);
        Assert.Equal(["bug", "cache"], detail.Labels);
        Assert.Equal(["ada"], detail.Assignees);
        Assert.Equal("v1.0", detail.Milestone);
        Assert.False(detail.IsTruncated);
        Assert.True(client.SupportsIssueDetail);
    }

    [Fact]
    public async Task Merges_comments_and_known_events_in_chronological_order()
    {
        var (client, _) = Create();

        var result = await client.GetIssueDetailAsync("t", Atlas, Listed, Ct);

        var timeline = Assert.IsType<Success<IssueDetail>>(result.Value).Value.Timeline;
        Assert.Equal(
            [(TimelineKind.Event, "grace", "a ajouté le label « bug »"), (TimelineKind.Comment, "ada", "Reproduced"), (TimelineKind.Event, "ada", "a fermé le ticket")],
            timeline.Select(t => (t.Kind, t.Author, t.Body)));
        var reactions = Assert.IsAssignableFrom<IReadOnlyList<ReactionCount>>(timeline[1].Reactions);
        Assert.Equal([new ReactionCount(ReactionKind.ThumbsUp, 2), new ReactionCount(ReactionKind.Heart, 1)], reactions);
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("not_planned", true)]
    public async Task A_closed_issue_keeps_its_reason(string reason, bool notPlanned)
    {
        var (client, routes) = Create();
        routes.Answers[IssueApi] = (HttpStatusCode.OK, $$"""
            {"number":5,"title":"Cache misses","user":{"login":"octocat"},"updated_at":"2026-10-12T08:00:00Z",
             "html_url":"https://github.com/atlas/atlas-api/issues/5","state":"closed","state_reason":"{{reason}}"}
            """, null);

        var result = await client.GetIssueDetailAsync("t", Atlas, Listed, Ct);

        var detail = Assert.IsType<Success<IssueDetail>>(result.Value).Value;
        Assert.Equal(IssueState.Closed, detail.State);
        Assert.Equal(notPlanned, detail.NotPlanned);
        Assert.Equal(string.Empty, detail.Body);
        Assert.Null(detail.Milestone);
    }

    [Fact]
    public async Task Follows_the_next_link_and_flags_a_list_cut_at_the_page_limit()
    {
        var (client, routes) = Create();
        for (var page = 1; page <= 6; page++)
        {
            var url = page == 1 ? IssueApi + "/comments?per_page=100" : $"{IssueApi}/comments?page={page}";
            var link = $"<{IssueApi}/comments?page={page + 1}>; rel=\"next\"";
            routes.Answers[url] = (HttpStatusCode.OK, $$"""[{"user":{"login":"ada"},"body":"c{{page}}","created_at":"2026-10-12T08:3{{page}}:00Z"}]""", link);
        }

        var result = await client.GetIssueDetailAsync("t", Atlas, Listed, Ct);

        var detail = Assert.IsType<Success<IssueDetail>>(result.Value).Value;
        Assert.True(detail.IsTruncated);
        Assert.Equal(5, detail.Timeline.Count(t => t.Kind == TimelineKind.Comment));
    }

    [Fact]
    public async Task An_unknown_issue_is_a_failure()
    {
        var (client, routes) = Create();
        routes.Answers[IssueApi] = (HttpStatusCode.NotFound, """{"message":"Not Found"}""", null);

        var result = await client.GetIssueDetailAsync("t", Atlas, Listed, Ct);

        Assert.IsType<Failure>(result.Value);
    }

    [Fact]
    public async Task A_refused_token_is_an_authentication_failure()
    {
        var (client, routes) = Create();
        routes.Answers[IssueApi] = (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""", null);

        var result = await client.GetIssueDetailAsync("t", Atlas, Listed, Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
    }
}
