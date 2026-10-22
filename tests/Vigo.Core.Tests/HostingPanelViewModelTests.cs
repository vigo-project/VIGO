using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>Pull requests and issues in the left panel, and creating a pull request (J6.2, ADR-045).</summary>
public sealed class HostingPanelViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private const string GitHubUrl = "https://github.com/atlas/atlas-api.git";

    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();
    private readonly StubClient _client = new();
    private readonly Notifier _notifier = new();
    private readonly Inputs _inputs = new();
    private readonly Launcher _launcher = new();

    public HostingPanelViewModelTests() => _repo.Commit("one", ("a.txt", "1\n"));

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PullRequest Pull(int number, string title, string head, bool draft = false, string? headRepository = null) =>
        new(number, title, "octocat", head, headRepository, "main", draft, Now, $"https://github.com/atlas/atlas-api/pull/{number}");

    private async Task<RepositoryTabViewModel> OpenAsync(string? remoteUrl = GitHubUrl, bool signedIn = true)
    {
        if (remoteUrl is not null) _repo.Git("remote", "add", "origin", remoteUrl);
        var accounts = new HostAccounts(new Store(signedIn ? [new HostAccount(HostingProvider.GitHub, "octocat", null, null)] : []), new Vault(), [_client]);
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, _notifier, confirmations: null, authentication: null, _inputs, preferences: null, accounts, _launcher);
        await tab.LoadAsync();
        await tab.RefActions.LoadRemotesAsync();
        await tab.Hosting.RefreshAsync();
        return tab;
    }

    private static string Describe(RefRow row) => $"{row.Label}{(row.Meta.Length > 0 ? " [" + row.Meta + "]" : string.Empty)}";

    [Fact]
    public async Task Pull_requests_and_issues_are_listed_between_the_remotes_and_the_tags()
    {
        _client.PullRequests.AddRange([Pull(12, "Fix cache", "fix/cache"), Pull(9, "Draft idea", "idea", draft: true)]);
        _client.Issues.Add(new Issue(3, "Timeout", "octocat", Now, "https://github.com/atlas/atlas-api/issues/3"));
        using var tab = await OpenAsync();

        var rows = tab.Refs.Rows.Select(i => i.Row).ToList();

        Assert.Equal(HostingState.Ready, tab.Hosting.State);
        Assert.Equal(["Distants", "Pull requests", "Issues", "Tags", "Stashes", "Worktrees"],
            rows.Where(r => r.IsSection && r.Label != "Branches locales").Select(r => r.Label));
        Assert.Equal(["#12 Fix cache [octocat]", "#9 Draft idea [brouillon]"], rows.Where(r => r.Kind == RefRowKind.PullRequest).Select(Describe));
        Assert.Equal("2", rows.Single(r => r.Key == "section:PullRequests").Meta);
        // Issues start collapsed (like the tags).
        Assert.DoesNotContain(rows, r => r.Kind == RefRowKind.Issue);
        Assert.Equal("https://github.com/atlas/atlas-api/pull/12", rows.First(r => r.Kind == RefRowKind.PullRequest).Url);
    }

    [Fact]
    public async Task Checks_and_review_show_on_the_pull_request_rows()
    {
        _client.PullRequests.AddRange([Pull(12, "Fix cache", "fix/cache"), Pull(9, "Add metrics", "metrics"), Pull(5, "Old", "old")]);
        _client.Statuses[12] = new PullRequestStatus(CheckState.Failure, ReviewState.None,
            [new CheckRun("build", CheckState.Success), new CheckRun("e2e", CheckState.Failure), new CheckRun("lint", CheckState.Pending)]);
        _client.Statuses[9] = new PullRequestStatus(CheckState.Success, ReviewState.Approved, [new CheckRun("build", CheckState.Success)]);
        using var tab = await OpenAsync();

        var rows = tab.Refs.Rows.Select(i => i.Row).Where(r => r.Kind == RefRowKind.PullRequest).ToList();

        // #5 could not be read: no mark, the list is shown anyway.
        Assert.Equal(["#12 Fix cache [✕ octocat]", "#9 Add metrics [✓ approuvée]", "#5 Old [octocat]"], rows.Select(Describe));
        Assert.Contains("Checks : 1/3 réussis · échec : e2e · 1 en cours", rows[0].ToolTip, StringComparison.Ordinal);
        Assert.Contains("Revue : approuvée", rows[1].ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_latest_pull_requests_get_their_status_read()
    {
        for (var n = 1; n <= HostingPanelViewModel.StatusLimit + 5; n++)
        {
            _client.PullRequests.Add(Pull(n, $"P{n}", $"b{n}"));
            _client.Statuses[n] = new PullRequestStatus(CheckState.Success, ReviewState.None, [new CheckRun("build", CheckState.Success)]);
        }
        using var tab = await OpenAsync();

        Assert.Equal(HostingPanelViewModel.StatusLimit, tab.Hosting.Statuses.Count);
        Assert.False(tab.Hosting.Statuses.ContainsKey(HostingPanelViewModel.StatusLimit + 1));
    }

    // Commands run from the panel are not awaited by the caller: wait (bounded) for their effect.
    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10, Ct);
    }

    private static Issue NewIssue(int number, string title) => new(number, title, "octocat", Now, $"https://github.com/atlas/atlas-api/issues/{number}");

    private static List<RefRow> IssueRows(RepositoryTabViewModel tab) => [.. tab.Refs.Rows.Select(i => i.Row).Where(r => r.Section == RefSection.Issues)];

    private static void ExpandIssues(RepositoryTabViewModel tab) =>
        tab.Refs.ActivateCommand.Execute(tab.Refs.Rows.Select(i => i.Row).Single(r => r.Key == "section:Issues"));

    [Fact]
    public async Task The_issue_list_is_paged_with_a_load_more_row()
    {
        _client.IssuePageSize = 2;
        _client.Issues.AddRange([NewIssue(5, "Five"), NewIssue(4, "Four"), NewIssue(3, "Three")]);
        using var tab = await OpenAsync();
        ExpandIssues(tab);

        var rows = IssueRows(tab);
        Assert.Equal("2+", rows[0].Meta);
        Assert.Equal(["#5 Five", "#4 Four", "Charger plus…"], rows.Skip(1).Select(r => r.Label));

        tab.Refs.ActivateCommand.Execute(rows.Single(r => r.Key == RefsPanelViewModel.MoreIssuesKey));
        await WaitUntilAsync(() => tab.Hosting.Issues.Count == 3);

        rows = IssueRows(tab);
        Assert.Equal("3", rows[0].Meta);
        Assert.Equal(["#5 Five", "#4 Four", "#3 Three"], rows.Skip(1).Select(r => r.Label));
        Assert.Equal([1, 2], _client.IssueCalls.Select(c => c.Page).Distinct());
    }

    [Fact]
    public async Task Loading_more_does_not_repeat_an_issue_that_moved_between_pages()
    {
        _client.IssuePageSize = 2;
        _client.Issues.AddRange([NewIssue(5, "Five"), NewIssue(4, "Four"), NewIssue(3, "Three")]);
        using var tab = await OpenAsync();
        _client.Overlap = true;

        await tab.Hosting.LoadMoreIssuesAsync();

        Assert.Equal([5, 4, 3], tab.Hosting.Issues.Select(i => i.Number));
    }

    [Fact]
    public async Task A_filter_reads_the_first_page_again_with_the_query_and_the_account()
    {
        _client.Issues.AddRange([NewIssue(5, "Cache bug"), NewIssue(4, "Docs")]);
        using var tab = await OpenAsync();

        await tab.Hosting.SetIssueFilterAsync(new IssueQuery(IssueStateFilter.Closed, AssignedToMe: true, Search: "cache"));

        var call = _client.IssueCalls[^1];
        Assert.Equal(new IssueQuery(IssueStateFilter.Closed, AssignedToMe: true, Search: "cache"), call.Query);
        Assert.Equal("octocat", call.Login);
        Assert.Equal(1, call.Page);
        Assert.Equal([5], tab.Hosting.Issues.Select(i => i.Number));
        ExpandIssues(tab);
        Assert.Equal("Filtre : fermées · assignées à moi · « cache »", IssueRows(tab)[1].Label);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_says_so_and_clicking_its_summary_clears_it()
    {
        _client.Issues.Add(NewIssue(5, "Cache bug"));
        using var tab = await OpenAsync();
        ExpandIssues(tab);

        await tab.Hosting.SetIssueFilterAsync(new IssueQuery(Search: "nothing"));
        var rows = IssueRows(tab);
        Assert.Contains(rows, r => r.Label == "Aucune issue ne correspond aux filtres.");

        tab.Refs.ActivateCommand.Execute(rows.Single(r => r.Key == RefsPanelViewModel.IssueFilterKey));
        await WaitUntilAsync(() => tab.Hosting.IssueFilter.IsDefault);

        Assert.True(tab.Hosting.IssueFilter.IsDefault);
        Assert.Equal(["#5 Cache bug"], IssueRows(tab).Where(r => r.Kind == RefRowKind.Issue).Select(r => r.Label));
    }

    [Fact]
    public async Task A_failed_filter_keeps_the_previous_filter_and_list_and_tells_why()
    {
        _client.Issues.Add(NewIssue(5, "Cache bug"));
        using var tab = await OpenAsync();
        _client.IssueFailure = "Requête de recherche invalide.";

        await tab.Hosting.SetIssueFilterAsync(new IssueQuery(Search: "label:"));

        Assert.True(tab.Hosting.IssueFilter.IsDefault);
        Assert.Equal([5], tab.Hosting.Issues.Select(i => i.Number));
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Error && n.Message == "Requête de recherche invalide.");
    }

    [Fact]
    public async Task The_search_entry_asks_for_the_text_and_an_empty_text_removes_it()
    {
        _client.Issues.AddRange([NewIssue(5, "Cache bug"), NewIssue(4, "Docs")]);
        using var tab = await OpenAsync();
        _inputs.Answer = d => d.Name = "docs";

        await tab.Hosting.SearchIssuesAsync();

        Assert.Equal("docs", tab.Hosting.IssueFilter.Search);
        Assert.Equal([4], tab.Hosting.Issues.Select(i => i.Number));
        var proposed = string.Empty;
        _inputs.Answer = d =>
        {
            proposed = d.Name;
            d.Name = string.Empty;
        };

        await tab.Hosting.SearchIssuesAsync();

        // The dialog starts with the current search.
        Assert.Equal("docs", proposed);
        Assert.True(tab.Hosting.IssueFilter.IsDefault);
    }

    [Fact]
    public async Task The_section_menu_offers_the_filters_with_the_active_ones_checked()
    {
        _client.Issues.Add(NewIssue(5, "Cache bug"));
        using var tab = await OpenAsync();
        await tab.Hosting.SetIssueFilterAsync(new IssueQuery(IssueStateFilter.All, CreatedByMe: true));

        var menu = tab.Refs.MenuFor(tab.Refs.Rows.Select(i => i.Row).Single(r => r.Key == "section:Issues"));

        var entries = Assert.IsAssignableFrom<IReadOnlyList<IssueFilterEntry>>(menu?.IssueFilters);
        Assert.Equal(["Toutes", "Créées par moi"], entries.Where(e => e.IsChecked).Select(e => e.Label));
        Assert.True(entries[^1].IsEnabled);
        Assert.Null(tab.Refs.MenuFor(tab.Refs.Rows.Select(i => i.Row).Single(r => r.Key == "section:PullRequests"))?.IssueFilters);
    }

    [Fact]
    public async Task The_filters_are_forgotten_when_the_repository_changes()
    {
        _client.Issues.Add(NewIssue(5, "Cache bug"));
        using var tab = await OpenAsync();
        await tab.Hosting.SetIssueFilterAsync(new IssueQuery(IssueStateFilter.Closed));

        _repo.Git("remote", "set-url", "origin", "https://github.com/atlas/other.git");
        await tab.RefActions.LoadRemotesAsync();
        await tab.Hosting.RefreshAsync();

        Assert.True(tab.Hosting.IssueFilter.IsDefault);
    }

    [Theory]
    [InlineData(IssueStateFilter.Open, false, false, "", "")]
    [InlineData(IssueStateFilter.Closed, false, false, "", "Filtre : fermées")]
    [InlineData(IssueStateFilter.Open, true, true, "label:bug", "Filtre : assignées à moi · créées par moi · « label:bug »")]
    public void The_active_issue_filters_are_summed_up(IssueStateFilter state, bool assigned, bool created, string search, string expected) =>
        Assert.Equal(expected, RefRowBuilder.IssueFilterSummary(new IssueQuery(state, assigned, created, search)));

    [Theory]
    [InlineData(CheckState.None, ReviewState.None, false, "octocat")]
    [InlineData(CheckState.Success, ReviewState.None, false, "✓ octocat")]
    [InlineData(CheckState.Pending, ReviewState.ChangesRequested, false, "● à corriger")]
    [InlineData(CheckState.Failure, ReviewState.Approved, true, "✕ brouillon")]
    public void The_meta_of_a_pull_request_row_combines_checks_review_and_draft(CheckState checks, ReviewState review, bool draft, string expected) =>
        Assert.Equal(expected, RefRowBuilder.PullRequestMeta(Pull(1, "T", "b", draft), new PullRequestStatus(checks, review, [])));

    [Fact]
    public async Task Without_an_account_the_sections_say_to_sign_in()
    {
        using var tab = await OpenAsync(signedIn: false);

        var rows = tab.Refs.Rows.Select(i => i.Row).ToList();

        Assert.Equal(HostingState.SignedOut, tab.Hosting.State);
        Assert.Equal("Connectez GitHub dans Préférences › Intégrations.", rows.Single(r => r.Kind == RefRowKind.Hint).Label);
        Assert.Equal(string.Empty, rows.Single(r => r.Key == "section:PullRequests").Meta);
        Assert.Equal(0, _client.ListCalls);
    }

    [Fact]
    public async Task A_remote_that_is_not_hosted_shows_no_hosting_section()
    {
        using var tab = await OpenAsync(remoteUrl: "https://git.example.com/atlas/atlas-api.git");

        Assert.Equal(HostingState.Unavailable, tab.Hosting.State);
        Assert.DoesNotContain(tab.Refs.Rows, i => i.Row.Section is RefSection.PullRequests or RefSection.Issues);
    }

    [Fact]
    public async Task A_failed_read_is_explained_in_the_section()
    {
        _client.FailureMessage = "La limite de requêtes de GitHub est atteinte.";
        using var tab = await OpenAsync();

        Assert.Equal(HostingState.Failed, tab.Hosting.State);
        Assert.Equal("La limite de requêtes de GitHub est atteinte.", tab.Refs.Rows.Select(i => i.Row).Single(r => r.Kind == RefRowKind.Hint && r.Key == "hint:pr").Label);
    }

    [Fact]
    public async Task The_filter_matches_numbers_and_titles()
    {
        _client.PullRequests.AddRange([Pull(12, "Fix cache", "fix/cache"), Pull(9, "Add metrics", "metrics")]);
        using var tab = await OpenAsync();

        tab.Refs.FilterText = "metr";

        Assert.Equal(["#9 Add metrics [octocat]"], tab.Refs.Rows.Select(i => i.Row).Where(r => r.Kind == RefRowKind.PullRequest).Select(Describe));
    }

    [Fact]
    public async Task A_pull_request_row_opens_in_the_browser()
    {
        _client.PullRequests.Add(Pull(12, "Fix cache", "fix/cache"));
        using var tab = await OpenAsync();
        var row = tab.Refs.Rows.Select(i => i.Row).Single(r => r.Kind == RefRowKind.PullRequest);

        Assert.True(row.IsOpenable);
        Assert.Equal("Ouvrir dans le navigateur", tab.Refs.MenuFor(row)?.OpenLabel);
        tab.Refs.OpenUrlCommand.Execute(row.Url);
        await Task.Yield();

        Assert.Equal(["https://github.com/atlas/atlas-api/pull/12"], _launcher.Opened);
    }

    [Fact]
    public async Task Creating_a_pull_request_asks_for_a_title_and_opens_the_new_page()
    {
        using var tab = await OpenAsync();
        var branch = Branch("feature/oauth-refresh", upstream: "origin/feature/oauth-refresh");
        _inputs.Answer = dialog =>
        {
            dialog.SecondText = "Refreshes tokens.";
            dialog.OptionChecked = true;
        };

        await tab.Hosting.CreatePullRequestCommand.ExecuteAsync(branch);

        var dialog = Assert.Single(_inputs.Shown);
        Assert.Equal("Oauth refresh", dialog.Name);
        Assert.Equal("feature/oauth-refresh → main · atlas/atlas-api", dialog.Description);
        var created = Assert.Single(_client.Created);
        Assert.Equal(new NewPullRequest("Oauth refresh", "Refreshes tokens.", "feature/oauth-refresh", "main", true), created);
        Assert.Equal(["https://github.com/atlas/atlas-api/pull/100"], _launcher.Opened);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Success && n.Title == "Pull request #100 créée");
    }

    [Fact]
    public async Task A_service_with_creation_options_gets_the_full_dialog_with_reviewers_labels_and_open_issues()
    {
        _client.SupportsPullRequestCreateOptions = true;
        _client.CreateOptions = new PullRequestCreateOptions([new PullRequestReviewerOption("id-1", "Ada"), new PullRequestReviewerOption("id-2", "Grace")]);
        _client.Issues.Add(new Issue(3, "Timeout", "octocat", Now, "https://github.com/atlas/atlas-api/issues/3"));
        using var tab = await OpenAsync();
        _inputs.AnswerFull = dialog =>
        {
            dialog.Body = "Refreshes tokens.";
            dialog.LabelsText = "bug, release";
            var ada = dialog.VisibleReviewers.Single(r => r.Name == "Ada");
            ada.IsSelected = true;
            ada.IsRequired = true;
            dialog.Issues.Single().IsSelected = true;
        };

        await tab.Hosting.CreatePullRequestCommand.ExecuteAsync(Branch("feature/oauth-refresh", upstream: "origin/feature/oauth-refresh"));

        var dialog = Assert.Single(_inputs.ShownFull);
        Assert.Empty(_inputs.Shown);
        Assert.Equal("feature/oauth-refresh → main · atlas/atlas-api", dialog.Description);
        var created = Assert.Single(_client.Created);
        Assert.Equal(("Oauth refresh", "Refreshes tokens.", false), (created.Title, created.Body, created.IsDraft));
        Assert.Equal([new NewPullRequestReviewer("id-1", "Ada", true)], created.Reviewers);
        Assert.Equal(["bug", "release"], created.Labels);
        Assert.Equal([3], created.LinkedIssues);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Success && n.Title == "Pull request #100 créée");
    }

    [Fact]
    public async Task Labels_and_reviewers_are_applied_after_the_creation_and_a_refusal_only_warns()
    {
        _client.SupportsPullRequestCreateOptions = true;
        _client.CreateOptions = new PullRequestCreateOptions([new PullRequestReviewerOption("ada", "ada")], SupportsRequiredReviewers: false);
        _client.ApplyFailure = "Relecteurs : Review cannot be requested from pull request author.";
        using var tab = await OpenAsync();
        _inputs.AnswerFull = dialog =>
        {
            dialog.LabelsText = "bug";
            var ada = dialog.VisibleReviewers.Single();
            ada.IsSelected = true;
            ada.IsRequired = true;
        };

        await tab.Hosting.CreatePullRequestCommand.ExecuteAsync(Branch("feature/x", upstream: "origin/feature/x"));

        var (created, request) = Assert.Single(_client.Applied);
        Assert.Equal(100, created.Number);
        Assert.Equal(["bug"], request.Labels);
        // GitHub cannot make a review mandatory: the flag never leaves the dialog.
        Assert.Equal([new NewPullRequestReviewer("ada", "ada", false)], request.Reviewers);
        Assert.False(Assert.Single(_inputs.ShownFull).VisibleReviewers.Single().ShowRequired);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning && n.Message?.StartsWith("Relecteurs :", StringComparison.Ordinal) == true);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Success && n.Title == "Pull request #100 créée");
    }

    [Fact]
    public async Task Nothing_is_applied_after_the_creation_when_no_label_or_reviewer_was_chosen()
    {
        _client.SupportsPullRequestCreateOptions = true;
        using var tab = await OpenAsync();

        await tab.Hosting.CreatePullRequestCommand.ExecuteAsync(Branch("feature/x", upstream: "origin/feature/x"));

        Assert.Single(_client.Created);
        Assert.Empty(_client.Applied);
    }

    [Fact]
    public async Task Unreadable_reviewers_warn_but_the_dialog_still_creates_the_pull_request()
    {
        _client.SupportsPullRequestCreateOptions = true;
        _client.CreateOptionsFailure = "Accès refusé aux équipes.";
        using var tab = await OpenAsync();

        await tab.Hosting.CreatePullRequestCommand.ExecuteAsync(Branch("feature/x", upstream: "origin/feature/x"));

        Assert.False(Assert.Single(_inputs.ShownFull).HasReviewers);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning && n.Message == "Accès refusé aux équipes.");
        Assert.Single(_client.Created);
    }

    [Fact]
    public async Task Creating_an_issue_asks_for_a_title_and_a_description_then_opens_the_new_page()
    {
        using var tab = await OpenAsync();
        var header = tab.Refs.Rows.Select(i => i.Row).Single(r => r is { Kind: RefRowKind.Section, Section: RefSection.Issues });
        Assert.True(tab.Refs.MenuFor(header)?.CanCreateIssue);
        _inputs.Answer = dialog =>
        {
            dialog.Name = "  Cache never expires ";
            dialog.SecondText = "Seen on **main**.";
        };

        await tab.Refs.NewIssueCommand.ExecuteAsync(null);

        Assert.Equal("atlas/atlas-api", Assert.Single(_inputs.Shown).Description);
        Assert.Equal(new NewIssue("Cache never expires", "Seen on **main**."), Assert.Single(_client.CreatedIssues));
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Success && n.Title == "Issue #77 créée");
        Assert.Equal(["https://github.com/atlas/atlas-api/issues/77"], _launcher.Opened);
    }

    [Fact]
    public async Task A_refused_issue_is_reported_and_nothing_opens()
    {
        using var tab = await OpenAsync();
        _client.CreateIssueFailure = "Issues are disabled for this repo.";
        _inputs.Answer = dialog => dialog.Name = "Title";

        await tab.Hosting.CreateIssueAsync();

        Assert.Empty(_launcher.Opened);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Error && n.Message == "Issues are disabled for this repo.");
    }

    [Fact]
    public async Task An_unpublished_branch_is_not_proposed_and_asks_to_push_first()
    {
        using var tab = await OpenAsync();

        await tab.Hosting.CreatePullRequestCommand.ExecuteAsync(Branch("local-only", upstream: null));

        Assert.Empty(_inputs.Shown);
        Assert.Empty(_client.Created);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning && n.Message!.Contains("Publiez d'abord", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_with_an_open_pull_request_opens_it_instead_of_creating_another()
    {
        _client.PullRequests.Add(Pull(12, "Fix cache", "fix/cache"));
        using var tab = await OpenAsync();

        await tab.Hosting.CreatePullRequestCommand.ExecuteAsync(Branch("fix/cache", upstream: "origin/fix/cache"));

        Assert.Empty(_client.Created);
        Assert.Equal(["https://github.com/atlas/atlas-api/pull/12"], _launcher.Opened);
    }

    [Fact]
    public async Task The_default_branch_cannot_be_the_source()
    {
        using var tab = await OpenAsync();

        await tab.Hosting.CreatePullRequestCommand.ExecuteAsync(Branch("main", upstream: "origin/main"));

        Assert.Empty(_inputs.Shown);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning);
    }

    [Theory]
    [InlineData("origin/feature/x", "origin", "feature/x")]
    [InlineData("origin/main", "origin", "main")]
    [InlineData("upstream/main", "origin", null)]
    [InlineData("origin/", "origin", null)]
    [InlineData(null, "origin", null)]
    public void The_source_branch_is_the_upstream_on_the_hosted_remote(string? upstream, string remote, string? expected) =>
        Assert.Equal(expected, HostingPanelViewModel.HeadBranchOn(Branch("x", upstream), remote));

    [Theory]
    [InlineData("feature/oauth-refresh", "Oauth refresh")]
    [InlineData("fix_cache_ttl", "Fix cache ttl")]
    [InlineData("hotfix", "Hotfix")]
    public void A_default_title_is_made_from_the_branch_name(string branch, string expected) =>
        Assert.Equal(expected, HostingPanelViewModel.TitleFor(branch));

    private static GitRef Branch(string name, string? upstream) =>
        new($"refs/heads/{name}", name, GitRefKind.LocalBranch, GitSha.Parse(new string('a', 40))) { Upstream = upstream };

    private sealed class StubClient : IHostingClient
    {
        public HostingProvider Provider => HostingProvider.GitHub;

        public bool SupportsDeviceFlow => false;

        public List<PullRequest> PullRequests { get; } = [];

        public List<Issue> Issues { get; } = [];

        public List<NewPullRequest> Created { get; } = [];

        /// <summary>Status per pull request number; a number without one fails to read.</summary>
        public Dictionary<int, PullRequestStatus> Statuses { get; } = [];

        public string? FailureMessage { get; set; }

        public int ListCalls { get; private set; }

        public HostRepository? ParseRepository(RemoteUrl url) =>
            url.Host == "github.com" && url.DisplayPath.Split('/') is [var owner, var name]
                ? new HostRepository(HostingProvider.GitHub, "github.com", owner, name)
                : null;

        public Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(string token, HostRepository repository, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<GitResult<IReadOnlyList<PullRequest>>>(FailureMessage is null
                ? new Success<IReadOnlyList<PullRequest>>([.. PullRequests])
                : new Failure(FailureMessage));
        }

        public Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IReadOnlyList<Issue>>>(new Success<IReadOnlyList<Issue>>([.. Issues]));

        public bool SupportsIssueFilters => true;

        public List<(IssueQuery Query, string? Login, int Page)> IssueCalls { get; } = [];

        public int IssuePageSize { get; set; } = 50;

        /// <summary>The next pages start one issue early, as when an issue is updated while the list is being read.</summary>
        public bool Overlap { get; set; }

        public string? IssueFailure { get; set; }

        public Task<GitResult<IssuePage>> ListIssuePageAsync(
            string token, HostRepository repository, IssueQuery query, string? login, int page, CancellationToken cancellationToken = default)
        {
            IssueCalls.Add((query, login, page));
            if (IssueFailure is not null) return Task.FromResult<GitResult<IssuePage>>(new Failure(IssueFailure));

            var matching = Issues.Where(i => query.Search.Length == 0 || i.Title.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToList();
            var skip = (page - 1) * IssuePageSize - (Overlap && page > 1 ? 1 : 0);
            var items = matching.Skip(skip).Take(IssuePageSize).ToList();
            return Task.FromResult<GitResult<IssuePage>>(new Success<IssuePage>(new IssuePage(items, matching.Count > skip + IssuePageSize)));
        }

        public Task<GitResult<string>> GetDefaultBranchAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<string>>(new Success<string>("main"));

        public Task<GitResult<PullRequest>> CreatePullRequestAsync(string token, HostRepository repository, NewPullRequest request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);
            return Task.FromResult<GitResult<PullRequest>>(new Success<PullRequest>(Pull(100, request.Title, request.Head)));
        }

        public bool SupportsPullRequestCreateOptions { get; set; }

        public PullRequestCreateOptions CreateOptions { get; set; } = new([]);

        public string? CreateOptionsFailure { get; set; }

        public Task<GitResult<PullRequestCreateOptions>> GetPullRequestCreateOptionsAsync(
            string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<PullRequestCreateOptions>>(CreateOptionsFailure is { } reason
                ? new Failure(reason)
                : new Success<PullRequestCreateOptions>(CreateOptions));

        public List<(PullRequest Created, NewPullRequest Request)> Applied { get; } = [];

        public string? ApplyFailure { get; set; }

        public Task<GitResult<Done>> ApplyPullRequestCreateOptionsAsync(
            string token, HostRepository repository, PullRequest created, NewPullRequest request, CancellationToken cancellationToken = default)
        {
            Applied.Add((created, request));
            return Task.FromResult<GitResult<Done>>(ApplyFailure is { } reason ? new Failure(reason) : new Success<Done>(Done.Instance));
        }

        public bool SupportsIssueWrite => true;

        public List<NewIssue> CreatedIssues { get; } = [];

        public string? CreateIssueFailure { get; set; }

        public Task<GitResult<Issue>> CreateIssueAsync(string token, HostRepository repository, NewIssue issue, CancellationToken cancellationToken = default)
        {
            if (CreateIssueFailure is { } reason) return Task.FromResult<GitResult<Issue>>(new Failure(reason));
            CreatedIssues.Add(issue);
            return Task.FromResult<GitResult<Issue>>(new Success<Issue>(
                new Issue(77, issue.Title, "octocat", DateTimeOffset.UnixEpoch, "https://github.com/atlas/atlas-api/issues/77")));
        }

        public int StatusReads { get; private set; }

        public Task<GitResult<PullRequestStatus>> GetPullRequestStatusAsync(
            string token, HostRepository repository, PullRequest pullRequest, bool includeRequired = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<PullRequestStatus>>(Read(pullRequest.Number) is { } status
                ? new Success<PullRequestStatus>(status)
                : new Failure("illisible"));

        private PullRequestStatus? Read(int number)
        {
            StatusReads++;
            return Statuses.GetValueOrDefault(number);
        }

        public Task<GitResult<HostUser>> GetUserAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitResult<DeviceCode>> StartDeviceFlowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitResult<DevicePoll>> PollDeviceFlowAsync(DeviceCode code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Store(IReadOnlyList<HostAccount> accounts) : IHostAccountStore
    {
        public IReadOnlyList<HostAccount> Load() => accounts;

        public Task SaveAsync(IReadOnlyList<HostAccount> accounts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Vault : ITokenVault
    {
        public string? Retrieve(HostingProvider provider, string login, string profileId) => "ghp_token";

        public void Store(HostingProvider provider, string login, string token, string profileId) { }

        public void Remove(HostingProvider provider, string login, string profileId) { }
    }

    private sealed class Notifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }

    private sealed class Inputs : IInputDialogService
    {
        public Action<InputDialogViewModel> Answer { get; set; } = _ => { };

        public List<InputDialogViewModel> Shown { get; } = [];

        public Action<NewPullRequestDialogViewModel> AnswerFull { get; set; } = _ => { };

        public List<NewPullRequestDialogViewModel> ShownFull { get; } = [];

        public Task<bool> ShowAsync(NewPullRequestDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            ShownFull.Add(dialog);
            AnswerFull(dialog);
            return Task.FromResult(true);
        }

        public Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            Shown.Add(dialog);
            Answer(dialog);
            return Task.FromResult(true);
        }
    }

    private sealed class Launcher : IUrlLauncher
    {
        public List<string> Opened { get; } = [];

        public Task<bool> OpenAsync(string url)
        {
            Opened.Add(url);
            return Task.FromResult(true);
        }
    }
}
