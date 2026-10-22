using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Integrations.AzureDevOps;

namespace Vigo.Integrations.Tests;

/// <summary>Work item screen and filtered list of Azure DevOps (J6.14).</summary>
public sealed class AzureDevOpsWorkItemTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HostRepository Atlas = new(HostingProvider.AzureDevOps, "dev.azure.com", "contoso/Atlas Project", "atlas-api");

    private sealed class UtcTime : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            var (status, text) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static (AzureDevOpsClient Client, FakeHandler Handler) Create(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond)
    {
        var handler = new FakeHandler(respond);
        return (new AzureDevOpsClient(new HttpClient(handler), time: new UtcTime()), handler);
    }

    private static readonly Issue Listed = new(42, "Crash on save", "Grace Hopper", default, "https://dev.azure.com/contoso/Atlas Project/_workitems/edit/42", "Bug");

    private const string ItemJson = """
        {"id":42,"rev":7,"fields":{
          "System.Title":"Crash on save","System.WorkItemType":"Bug","System.State":"Active","System.Reason":"Implementation started",
          "System.AreaPath":"Atlas\\Backend","System.IterationPath":"Atlas\\Sprint 4",
          "System.AssignedTo":{"displayName":"Ada Lovelace","uniqueName":"ada@contoso.com"},
          "System.CreatedBy":{"displayName":"Grace Hopper"},"System.CreatedDate":"2026-10-01T08:00:00Z",
          "System.ChangedDate":"2026-10-15T10:30:00Z","System.Tags":"urgent; backend","System.CommentCount":2,"System.Rev":7,
          "Microsoft.VSTS.Common.Priority":2,"Microsoft.VSTS.Scheduling.RemainingWork":3.5,"Microsoft.VSTS.Scheduling.TargetDate":"2026-11-01T00:00:00Z",
          "System.Description":"<p>It crashes</p>","Microsoft.VSTS.TCM.ReproSteps":"<div>Open <b>file</b></div>",
          "Custom.TeamOwner":"Platform","Custom.IsBlocked":true,"WEF_ABC_Kanban.Column":"Doing"},
         "relations":[
          {"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/contoso/pid/_apis/wit/workItems/7","attributes":{}},
          {"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/contoso/pid/_apis/wit/workItems/43","attributes":{}},
          {"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/pid%2Frid%2F12","attributes":{"name":"Pull Request"}},
          {"rel":"ArtifactLink","url":"vstfs:///Git/Commit/pid%2Frid%2F0123456789abcdef","attributes":{"name":"Fixed in Commit"}},
          {"rel":"ArtifactLink","url":"vstfs:///Git/Ref/pid%2Frid%2FGBfeature%2Fcache","attributes":{"name":"Branch"}},
          {"rel":"Hyperlink","url":"https://example.com/doc","attributes":{"comment":"Spec"}},
          {"rel":"AttachedFile","url":"https://dev.azure.com/contoso/_apis/wit/attachments/abc","attributes":{"name":"trace.log","resourceSize":2048}}]}
        """;

    private const string TypeJson = """{"color":"CC293D","states":[{"name":"New","category":"Proposed"},{"name":"Active","category":"InProgress"}]}""";

    private const string CommentsJson = """
        {"totalCount":2,"count":2,"comments":[
          {"id":1,"text":"<div>Seen <b>twice</b></div>","createdBy":{"displayName":"Ada Lovelace"},"createdDate":"2026-10-10T09:00:00Z"},
          {"id":2,"text":"Plain markdown **text**","createdBy":{"displayName":"Grace Hopper"},"createdDate":"2026-10-10T10:00:00Z"}]}
        """;

    private const string UpdatesJson = """
        {"value":[
          {"rev":1,"revisedBy":{"displayName":"Grace Hopper"},"revisedDate":"2026-10-01T08:00:00Z","fields":{"System.State":{"newValue":"New"}}},
          {"rev":2,"revisedBy":{"displayName":"Ada Lovelace"},"revisedDate":"2026-10-12T09:00:00Z","fields":{
             "System.State":{"oldValue":"New","newValue":"Active"},
             "System.AssignedTo":{"newValue":{"displayName":"Ada Lovelace"}},
             "Microsoft.VSTS.Common.Priority":{"oldValue":3,"newValue":2},
             "System.ChangedBy":{"oldValue":"Grace","newValue":"Ada"}},
           "relations":{"added":[{"rel":"AttachedFile","url":"https://dev.azure.com/contoso/_apis/wit/attachments/abc","attributes":{"name":"trace.log"}}]}},
          {"rev":7,"revisedBy":{"displayName":"Ada Lovelace"},"revisedDate":"9999-01-01T00:00:00Z","fields":{"System.Title":{"oldValue":"a","newValue":"b"}}}]}
        """;

    private const string LinkedJson = """
        {"value":[
          {"id":7,"fields":{"System.Title":"Reliability epic","System.WorkItemType":"Epic","System.State":"Active"}},
          {"id":43,"fields":{"System.Title":"Add retry","System.WorkItemType":"Task","System.State":"New"}}]}
        """;

    private static (HttpStatusCode, string) Route(HttpRequestMessage request, string _)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url.Contains("/wit/workitemtypes/", StringComparison.Ordinal)) return (HttpStatusCode.OK, TypeJson);
        if (url.Contains("/comments", StringComparison.Ordinal)) return (HttpStatusCode.OK, CommentsJson);
        if (url.Contains("/updates", StringComparison.Ordinal)) return (HttpStatusCode.OK, UpdatesJson);
        if (url.Contains("/wit/workitems?ids=", StringComparison.Ordinal)) return (HttpStatusCode.OK, LinkedJson);
        if (url.Contains("/wit/workitems/42", StringComparison.Ordinal)) return (HttpStatusCode.OK, ItemJson);
        return (HttpStatusCode.NotFound, "{}");
    }

    private static async Task<WorkItemDetail> ReadAsync(AzureDevOpsClient client) =>
        Assert.IsType<Success<WorkItemDetail>>((await client.GetWorkItemDetailAsync("t", Atlas, Listed, Ct)).Value).Value;

    [Fact]
    public async Task Reads_the_header_state_and_people_of_a_work_item()
    {
        var (client, _) = Create(Route);

        var detail = await ReadAsync(client);

        Assert.Equal((42, "Crash on save", "Grace Hopper", "Bug"), (detail.Summary.Number, detail.Summary.Title, detail.Summary.Author, detail.Summary.Type));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 10, 30, 0, TimeSpan.Zero), detail.Summary.UpdatedAt);
        Assert.Equal(("Active", WorkItemStateKind.InProgress, "CC293D"), (detail.State, detail.StateKind, detail.TypeColor));
        Assert.Equal("Ada Lovelace", detail.AssignedTo);
        Assert.Equal(["urgent", "backend"], detail.Tags);
        Assert.Equal(2, detail.CommentCount);
        Assert.False(detail.IsTruncated);
    }

    [Fact]
    public async Task Groups_the_fields_by_what_they_say_and_lists_custom_ones_last()
    {
        var (client, _) = Create(Route);

        var sections = (await ReadAsync(client)).Sections;

        Assert.Equal(["Planification", "Personnes", "Dates", "Détails", "Champs supplémentaires"], sections.Select(s => s.Title));
        Assert.Equal(
            [("Zone", "Atlas/Backend"), ("Itération", "Atlas/Sprint 4"), ("Priorité", "2"), ("Travail restant", "3.5 h"), ("Date cible", "2026-11-01")],
            sections[0].Fields.Select(f => (f.Label, f.Value)));
        Assert.Equal([("Assigné à", "Ada Lovelace"), ("Créé par", "Grace Hopper")], sections[1].Fields.Select(f => (f.Label, f.Value)));
        Assert.Equal([("Créé le", "2026-10-01 08:00"), ("Modifié le", "2026-10-15 10:30")], sections[2].Fields.Select(f => (f.Label, f.Value)));
        Assert.Equal([("Motif", "Implementation started"), ("Révision", "7")], sections[3].Fields.Select(f => (f.Label, f.Value)));
        // The Kanban bookkeeping field is not listed; custom ones get a readable label.
        Assert.Equal([("Is Blocked", "Oui"), ("Team Owner", "Platform")], sections[4].Fields.Select(f => (f.Label, f.Value)));
    }

    [Fact]
    public async Task Turns_the_rich_text_fields_into_markdown()
    {
        var (client, _) = Create(Route);

        var texts = (await ReadAsync(client)).Texts;

        Assert.Equal([("Description", "It crashes"), ("Étapes de reproduction", "Open **file**")], texts.Select(t => (t.Title, t.Markdown)));
    }

    [Fact]
    public async Task Resolves_links_to_work_items_pull_requests_commits_branches_and_hyperlinks()
    {
        var (client, _) = Create(Route);

        var links = (await ReadAsync(client)).Links;

        Assert.Equal(
            [WorkItemLinkKind.Parent, WorkItemLinkKind.Child, WorkItemLinkKind.PullRequest, WorkItemLinkKind.Commit, WorkItemLinkKind.Branch, WorkItemLinkKind.Hyperlink],
            links.Select(l => l.Kind));
        var parent = links[0];
        Assert.Equal((7, "Reliability epic", "Epic", "Active"), (parent.WorkItemId, parent.Title, parent.Type, parent.State));
        Assert.Equal("https://dev.azure.com/contoso/Atlas Project/_workitems/edit/7", parent.Url);
        Assert.Equal("Add retry", links[1].Title);
        Assert.Equal(("Pull request !12", "https://dev.azure.com/contoso/Atlas Project/_git/rid/pullrequest/12"), (links[2].Title, links[2].Url));
        Assert.Equal(("Commit 01234567", "https://dev.azure.com/contoso/Atlas Project/_git/rid/commit/0123456789abcdef"), (links[3].Title, links[3].Url));
        Assert.Equal(("Branche feature/cache", "https://dev.azure.com/contoso/Atlas Project/_git/rid?version=GBfeature%2Fcache"), (links[4].Title, links[4].Url));
        Assert.Equal(("Spec", "https://example.com/doc"), (links[5].Title, links[5].Url));
        Assert.All(links.Skip(2), l => Assert.Null(l.WorkItemId));
    }

    [Theory]
    [InlineData("active", false, "Active")]
    [InlineData("active", true, "Brouillon")]
    [InlineData("completed", false, "Terminée")]
    [InlineData("abandoned", false, "Abandonnée")]
    public async Task A_pull_request_link_shows_its_title_and_status(string status, bool draft, string expected)
    {
        var (client, handler) = Create((r, b) => r.RequestUri!.AbsoluteUri.Contains("/pullrequests/12", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, $$"""{"pullRequestId":12,"title":"Fix cache","status":"{{status}}","isDraft":{{(draft ? "true" : "false")}}}""")
            : Route(r, b));

        var pull = (await ReadAsync(client)).Links.Single(l => l.Kind == WorkItemLinkKind.PullRequest);

        Assert.Equal(("!12 Fix cache", expected), (pull.Title, pull.State));
        Assert.Contains(handler.Requests, q => q.Request.RequestUri!.AbsoluteUri == "https://dev.azure.com/contoso/pid/_apis/git/repositories/rid/pullrequests/12?api-version=7.1");
    }

    [Fact]
    public async Task A_pull_request_that_cannot_be_read_keeps_its_plain_link()
    {
        var (client, _) = Create(Route);

        var pull = (await ReadAsync(client)).Links.Single(l => l.Kind == WorkItemLinkKind.PullRequest);

        Assert.Equal(("Pull request !12", null), (pull.Title, pull.State));
    }

    [Fact]
    public async Task Lists_attachments_apart_from_links()
    {
        var (client, _) = Create(Route);

        var file = Assert.Single((await ReadAsync(client)).Attachments);

        Assert.Equal(("trace.log", 2048L, "https://dev.azure.com/contoso/_apis/wit/attachments/abc"), (file.Name, file.Size, file.Url));
    }

    [Fact]
    public async Task Merges_comments_and_changes_into_one_timeline_oldest_first()
    {
        var (client, _) = Create(Route);

        var timeline = (await ReadAsync(client)).Timeline;

        Assert.Equal(
            [
                (TimelineKind.Event, "Grace Hopper", "a créé l’élément de travail"),
                (TimelineKind.Comment, "Ada Lovelace", "Seen **twice**"),
                (TimelineKind.Comment, "Grace Hopper", "Plain markdown **text**"),
                (TimelineKind.Event, "Ada Lovelace", "a changé l’état de « New » à « Active »"),
                (TimelineKind.Event, "Ada Lovelace", "a assigné l’élément à Ada Lovelace"),
                (TimelineKind.Event, "Ada Lovelace", "a modifié Priorité : « 3 » → « 2 »"),
                (TimelineKind.Event, "Ada Lovelace", "a ajouté le lien « trace.log »"),
            ],
            timeline.Select(t => (t.Kind, t.Author, t.Body)));
    }

    [Fact]
    public async Task The_extras_are_optional_a_failed_one_leaves_the_rest()
    {
        var (client, _) = Create((r, b) => r.RequestUri!.AbsoluteUri.Contains("/comments", StringComparison.Ordinal) ? (HttpStatusCode.InternalServerError, "{}") : Route(r, b));

        var detail = await ReadAsync(client);

        Assert.Equal("Active", detail.State);
        Assert.DoesNotContain(detail.Timeline, t => t.Kind == TimelineKind.Comment);
        Assert.NotEmpty(detail.Timeline);
    }

    [Fact]
    public async Task A_refused_item_read_is_an_authentication_failure()
    {
        var (client, _) = Create((_, _) => (HttpStatusCode.Unauthorized, "{}"));

        var result = await client.GetWorkItemDetailAsync("t", Atlas, Listed, Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
    }

    [Fact]
    public async Task Without_the_type_the_state_name_tells_where_the_item_stands()
    {
        var (client, _) = Create((r, b) => r.RequestUri!.AbsoluteUri.Contains("/wit/workitemtypes/", StringComparison.Ordinal) ? (HttpStatusCode.Forbidden, "{}") : Route(r, b));

        var detail = await ReadAsync(client);

        Assert.Equal(WorkItemStateKind.InProgress, detail.StateKind);
        Assert.Null(detail.TypeColor);
    }

    [Fact]
    public void Test_case_steps_are_read_from_their_xml_with_the_escaped_html_converted()
    {
        const string xml = """
            <steps id="0" last="2"><step id="1" type="ActionStep"><parameterizedString isformatted="true">&lt;DIV&gt;&lt;P&gt;Open &lt;B&gt;file&lt;/B&gt;&lt;/P&gt;&lt;/DIV&gt;</parameterizedString>
            <parameterizedString isformatted="true">&lt;DIV&gt;It opens&lt;/DIV&gt;</parameterizedString><description/></step>
            <step id="2" type="ValidateStep"><parameterizedString isformatted="true">Close</parameterizedString><parameterizedString isformatted="true"></parameterizedString></step></steps>
            """;

        var markdown = AzureDevOpsClient.StepsMarkdown(xml);

        Assert.Equal("**Étape 1**\n\nOpen **file**\n\n*Résultat attendu :* It opens\n\n---\n\n**Étape 2**\n\nClose", markdown);
        Assert.DoesNotContain("<", markdown, StringComparison.Ordinal);
    }

    // --- Repositories of a token limited to one organization --------------------------------------------------------------

    [Fact]
    public async Task A_token_limited_to_one_organization_lists_only_that_organization()
    {
        var (client, handler) = Create((_, _) => (HttpStatusCode.OK,
            """{"value":[{"name":"api","remoteUrl":"https://contoso@dev.azure.com/contoso/Atlas/_git/api","project":{"name":"Atlas"}}]}"""));

        var list = Assert.IsType<Success<HostedRepositoryList>>((await client.ListRepositoriesAsync("pat", "contoso", Ct)).Value).Value;

        Assert.Equal(["contoso/Atlas/api"], list.Items.Select(r => r.FullName));
        // No profile or accounts call: the token would be refused there.
        Assert.Equal(["https://dev.azure.com/contoso/_apis/git/repositories?api-version=7.1"], handler.Requests.Select(r => r.Request.RequestUri!.AbsoluteUri));
    }

    [Fact]
    public async Task A_refused_account_wide_listing_tells_to_enter_the_organization()
    {
        var (client, _) = Create((_, _) => (HttpStatusCode.Unauthorized, "{}"));

        var result = await client.ListRepositoriesAsync("pat", null, Ct);

        var failure = Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.Contains("organisation", failure.Message, StringComparison.Ordinal);
    }

    // --- Filtered list -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_default_query_is_the_open_work_items()
    {
        Assert.Equal(
            "Select [System.Id] From WorkItems Where [System.TeamProject] = @project And [System.State] Not In ('Closed', 'Done', 'Removed') Order By [System.ChangedDate] Desc",
            AzureDevOpsClient.BuildWiql(IssueQuery.Default));
    }

    [Fact]
    public void State_and_me_filters_become_clauses()
    {
        var wiql = AzureDevOpsClient.BuildWiql(new IssueQuery(IssueStateFilter.Closed, AssignedToMe: true, CreatedByMe: true));

        Assert.Contains("[System.State] In ('Closed', 'Done', 'Removed')", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.AssignedTo] = @Me", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.CreatedBy] = @Me", wiql, StringComparison.Ordinal);
        Assert.DoesNotContain("State] Not In", AzureDevOpsClient.BuildWiql(new IssueQuery(IssueStateFilter.All)), StringComparison.Ordinal);
    }

    [Fact]
    public void Search_words_and_qualifiers_narrow_the_query()
    {
        var wiql = AzureDevOpsClient.BuildWiql(new IssueQuery(Search: "type:Bug tag:\"to do\" crash 42 O'Brien iteration:\"Atlas\\Sprint 4\""));

        Assert.Contains("[System.WorkItemType] = 'Bug'", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.Tags] Contains 'to do'", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.Title] Contains 'crash'", wiql, StringComparison.Ordinal);
        Assert.Contains("([System.Id] = 42 Or [System.Title] Contains '42')", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.Title] Contains 'O''Brien'", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.IterationPath] Under 'Atlas\\Sprint 4'", wiql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_qualifier_replaces_the_state_switch()
    {
        var wiql = AzureDevOpsClient.BuildWiql(new IssueQuery(Search: "state:Resolved"));

        Assert.Contains("[System.State] = 'Resolved'", wiql, StringComparison.Ordinal);
        Assert.DoesNotContain("Not In", wiql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_through_the_matching_work_items_keeping_the_query_order()
    {
        var all = Enumerable.Range(1, 120).Reverse().ToArray();
        var (client, handler) = Create((r, _) =>
        {
            if (r.Method == HttpMethod.Post) return (HttpStatusCode.OK, "{\"workItems\":[" + string.Join(',', all.Select(i => $"{{\"id\":{i}}}")) + "]}");
            var ids = r.RequestUri!.Query.Split('&').First(p => p.Contains("ids=", StringComparison.Ordinal)).Split('=')[1].Split(',').Select(int.Parse).Reverse();
            // The service answers in any order: the page must come back in the order of the query.
            return (HttpStatusCode.OK, "{\"value\":[" + string.Join(',', ids.Select(i => $"{{\"id\":{i},\"fields\":{{\"System.Title\":\"Item {i}\",\"System.ChangedDate\":\"2026-10-01T00:00:00Z\"}}}}")) + "]}");
        });

        var second = Assert.IsType<Success<IssuePage>>((await client.ListIssuePageAsync("t", Atlas, IssueQuery.Default, null, 2, Ct)).Value).Value;
        Assert.Equal(Enumerable.Range(1, 120).Reverse().Skip(50).Take(50), second.Items.Select(i => i.Number));
        Assert.True(second.HasMore);
        Assert.Contains("$top=101", handler.Requests[0].Request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);

        var third = Assert.IsType<Success<IssuePage>>((await client.ListIssuePageAsync("t", Atlas, IssueQuery.Default, null, 3, Ct)).Value).Value;
        Assert.Equal(20, third.Items.Count);
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task An_empty_page_costs_a_single_call()
    {
        var (client, handler) = Create((_, _) => (HttpStatusCode.OK, """{"workItems":[]}"""));

        var page = Assert.IsType<Success<IssuePage>>((await client.ListIssuePageAsync("t", Atlas, new IssueQuery(Search: "zzz"), null, 1, Ct)).Value).Value;

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
        Assert.Single(handler.Requests);
    }

    // --- Pictures ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_picture_of_the_organization_is_downloaded_unless_it_is_too_large()
    {
        const string url = "https://dev.azure.com/contoso/_apis/wit/attachments/abc";
        var (client, _) = Create((_, _) => (HttpStatusCode.OK, "PNGDATA"));
        var (huge, _) = Create((_, _) => (HttpStatusCode.OK, new string('a', 26 * 1024 * 1024)));

        var small = await client.DownloadImageAsync("t", url, Ct);
        var large = await huge.DownloadImageAsync("t", url, Ct);

        Assert.Equal("PNGDATA"u8.ToArray(), Assert.IsType<Success<byte[]>>(small.Value).Value);
        Assert.IsType<Failure>(large.Value);
    }
}
