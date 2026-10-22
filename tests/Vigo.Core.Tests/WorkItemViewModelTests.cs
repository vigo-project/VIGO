using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>Work item screen (J6.14): opening from the Issues section on Azure DevOps, fields, links, trail, failure.</summary>
public sealed class WorkItemViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private const string AzureUrl = "https://dev.azure.com/contoso/Atlas/_git/api";

    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();
    private readonly StubClient _client = new();
    private readonly Launcher _launcher = new();

    public WorkItemViewModelTests() => _repo.Commit("one", ("a.txt", "1\n"));

    public void Dispose() => _repo.Dispose();

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        _repo.Git("remote", "add", "origin", AzureUrl);
        var accounts = new HostAccounts(new Store([new HostAccount(HostingProvider.AzureDevOps, "ada", null, null)]), new Vault(), [_client]);
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, notifier: null, confirmations: null, authentication: null, inputs: null, preferences: null, accounts, _launcher);
        await tab.LoadAsync();
        await tab.RefActions.LoadRemotesAsync();
        await tab.Hosting.RefreshAsync();
        return tab;
    }

    [Fact]
    public async Task A_work_item_opens_its_own_screen_and_not_the_issue_one()
    {
        using var tab = await OpenAsync();

        await tab.Hosting.ShowIssueAsync(5);

        Assert.False(tab.ShowGraph);
        Assert.True(tab.WorkItem.IsOpen);
        Assert.False(tab.Issue.IsOpen);
        Assert.Empty(_launcher.Opened);
    }

    [Fact]
    public async Task The_header_gives_type_state_assignee_tags_and_comment_count()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.WorkItem;

        Assert.Equal("Crash on save", screen.Title);
        Assert.Equal("#5", screen.NumberText);
        Assert.Equal("Bug", screen.TypeText);
        Assert.Equal("Active", screen.StateText);
        Assert.Equal(WorkItemStateKind.InProgress, screen.StateKind);
        Assert.Equal("CC293D", screen.TypeColor);
        Assert.Equal("Assigné à : Ada Lovelace", screen.AssignedToText);
        Assert.Equal(["urgent", "backend"], screen.Tags.Select(t => t.Name));
        Assert.Equal("2 commentaires", screen.SummaryText);
    }

    [Fact]
    public async Task Fields_texts_links_attachments_and_timeline_are_ready_to_draw()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.WorkItem;

        var planning = Assert.Single(screen.Sections);
        Assert.Equal("Planification", planning.Title);
        Assert.Equal(["Priorité"], planning.Fields.Select(f => f.Label));

        var text = Assert.Single(screen.Texts);
        Assert.Equal("Description", text.Title);
        Assert.IsType<MarkdownParagraph>(Assert.Single(text.Blocks));

        Assert.Equal(["Parent", "Éléments enfants", "Pull requests"], screen.LinkGroups.Select(g => g.Title));
        var parent = Assert.Single(screen.LinkGroups[0].Items);
        Assert.Equal(("Bug", "Active"), (parent.Detail, parent.StateText));
        Assert.True(parent.OpensInApp);
        Assert.False(Assert.Single(screen.LinkGroups[2].Items).OpensInApp);

        var file = Assert.Single(screen.Attachments);
        Assert.Equal(("trace.log", "2 Ko"), (file.Name, file.SizeText));

        Assert.Equal(["ada a commenté", "grace a changé l’état de « New » à « Active »"], screen.Timeline.Select(t => t.ToString()));
    }

    [Fact]
    public async Task A_linked_work_item_opens_in_the_same_screen_and_back_returns_to_the_previous_one()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.WorkItem;
        Assert.False(screen.CanGoBackToItem);

        await screen.OpenLinkCommand.ExecuteAsync(screen.LinkGroups[0].Items[0]);

        Assert.Equal("#2", screen.NumberText);
        Assert.True(screen.CanGoBackToItem);
        Assert.Empty(_launcher.Opened);

        await screen.BackAsync();
        Assert.Equal("#5", screen.NumberText);
        Assert.False(screen.CanGoBackToItem);
        Assert.True(screen.IsOpen);

        await screen.BackAsync();
        Assert.False(screen.IsOpen);
        Assert.True(tab.ShowGraph);
    }

    [Fact]
    public async Task A_link_that_is_not_a_work_item_opens_the_browser()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.WorkItem;

        await screen.OpenLinkCommand.ExecuteAsync(screen.LinkGroups[2].Items[0]);

        Assert.Equal(["https://dev.azure.com/contoso/Atlas/_git/repo-id/pullrequest/12"], _launcher.Opened);
        Assert.Equal("#5", screen.NumberText);
    }

    [Fact]
    public async Task A_read_failure_is_shown_in_the_screen()
    {
        using var tab = await OpenAsync();
        _client.DetailFailure = "boom";

        await tab.Hosting.ShowIssueAsync(5);

        Assert.True(tab.WorkItem.IsOpen);
        Assert.Equal("Impossible de lire l’élément de travail : boom", tab.WorkItem.Message);
        Assert.Null(tab.WorkItem.Detail);
    }

    [Fact]
    public async Task Closing_gives_the_graph_back_and_opening_a_pull_request_closes_the_screen()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);

        tab.WorkItem.CloseCommand.Execute(null);
        Assert.True(tab.ShowGraph);
        Assert.Empty(tab.WorkItem.Timeline);

        await tab.Hosting.ShowIssueAsync(5);
        await tab.ShowPullRequestAsync(new PullRequest(7, "Pull 7", "ada", "fix", null, "main", false, Now, "https://dev.azure.com/contoso/Atlas/_git/api/pullrequest/7"));
        Assert.False(tab.WorkItem.IsOpen);
    }

    private sealed class StubClient : IHostingClient
    {
        public HostingProvider Provider => HostingProvider.AzureDevOps;

        public bool SupportsDeviceFlow => false;

        public bool SupportsWorkItemDetail => true;

        public string? DetailFailure { get; set; }

        public HostRepository? ParseRepository(RemoteUrl url) =>
            url.Host == "dev.azure.com" && url.DisplayPath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var org, var project, "_git", var repo]
                ? new HostRepository(HostingProvider.AzureDevOps, "dev.azure.com", $"{org}/{project}", repo)
                : null;

        public Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IReadOnlyList<PullRequest>>>(new Success<IReadOnlyList<PullRequest>>([]));

        public Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IReadOnlyList<Issue>>>(new Success<IReadOnlyList<Issue>>(
                [new Issue(5, "Crash on save", "Ada", Now, "https://dev.azure.com/contoso/Atlas/_workitems/edit/5", "Bug")]));

        public Task<GitResult<WorkItemDetail>> GetWorkItemDetailAsync(string token, HostRepository repository, Issue workItem, CancellationToken cancellationToken = default)
        {
            if (DetailFailure is not null) return Task.FromResult<GitResult<WorkItemDetail>>(new Failure(DetailFailure));
            var summary = new Issue(workItem.Number, workItem.Number == 5 ? "Crash on save" : "Epic", "Ada", Now, workItem.Url, workItem.Type ?? "Bug");
            var five = workItem.Number == 5;
            var detail = new WorkItemDetail(
                summary, "Active", WorkItemStateKind.InProgress, "CC293D", "Ada Lovelace", five ? ["urgent", "backend"] : [],
                five ? [new WorkItemSection("Planification", [new WorkItemField("Priorité", "2")])] : [],
                five ? [new WorkItemText("Description", "It **crashes**.")] : [],
                five
                    ?
                    [
                        new WorkItemLink(WorkItemLinkKind.Parent, "Epic", "https://dev.azure.com/contoso/Atlas/_workitems/edit/2", 2, "Bug", "Active"),
                        new WorkItemLink(WorkItemLinkKind.Child, "Task", "https://dev.azure.com/contoso/Atlas/_workitems/edit/6", 6, "Task", "New"),
                        new WorkItemLink(WorkItemLinkKind.PullRequest, "Pull request !12", "https://dev.azure.com/contoso/Atlas/_git/repo-id/pullrequest/12"),
                    ]
                    : [],
                five ? [new WorkItemAttachment("trace.log", 2048, "https://dev.azure.com/contoso/_apis/wit/attachments/1")] : [],
                five
                    ?
                    [
                        new TimelineEntry(TimelineKind.Comment, "ada", "Seen **twice**.", Now.AddHours(-2), Id: 1),
                        new TimelineEntry(TimelineKind.Event, "grace", "a changé l’état de « New » à « Active »", Now.AddHours(-1)),
                    ]
                    : [],
                five ? 2 : 0);
            return Task.FromResult<GitResult<WorkItemDetail>>(new Success<WorkItemDetail>(detail));
        }

        public Task<GitResult<string>> GetDefaultBranchAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<string>>(new Success<string>("main"));

        public Task<GitResult<PullRequest>> CreatePullRequestAsync(string token, HostRepository repository, NewPullRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
        public string? Retrieve(HostingProvider provider, string login, string profileId) => "token";

        public void Store(HostingProvider provider, string login, string token, string profileId) { }

        public void Remove(HostingProvider provider, string login, string profileId) { }
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
