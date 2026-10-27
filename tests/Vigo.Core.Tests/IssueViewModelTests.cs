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

/// <summary>Issue screen (J6.10a): opening from the left panel, description, labels, comments and events.</summary>
public sealed class IssueViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private const string GitHubUrl = "https://github.com/atlas/atlas-api.git";

    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();
    private readonly StubClient _client = new();
    private readonly Launcher _launcher = new();
    private readonly FakeConfirmations _confirmations = new() { Answer = true };
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeInputs _inputs = new();

    public IssueViewModelTests() => _repo.Commit("one", ("a.txt", "1\n"));

    public void Dispose() => _repo.Dispose();

    private static Issue IssueOf(int number) =>
        new(number, $"Issue {number}", "octocat", Now, $"https://github.com/atlas/atlas-api/issues/{number}");

    private async Task<RepositoryTabViewModel> OpenAsync(bool detailSupported = true)
    {
        _client.SupportsDetail = detailSupported;
        _client.Issues.AddRange([IssueOf(5), IssueOf(6)]);
        _repo.Git("remote", "add", "origin", GitHubUrl);
        var accounts = new HostAccounts(new Store([new HostAccount(HostingProvider.GitHub, "octocat", null, null)]), new Vault(), [_client]);
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, notifier: _notifier, confirmations: _confirmations, authentication: null, inputs: _inputs, preferences: null, accounts, _launcher);
        await tab.LoadAsync();
        await tab.RefActions.LoadRemotesAsync();
        await tab.Hosting.RefreshAsync();
        return tab;
    }

    [Fact]
    public async Task Opening_an_issue_takes_the_main_area_and_reads_its_detail()
    {
        using var tab = await OpenAsync();
        Assert.True(tab.ShowGraph);

        await tab.Hosting.ShowIssueAsync(5);

        Assert.False(tab.ShowGraph);
        var screen = tab.Issue;
        Assert.True(screen.IsOpen);
        Assert.False(screen.IsLoading);
        Assert.Equal("Issue 5", screen.Title);
        Assert.Equal("#5", screen.NumberText);
        Assert.Equal("Ouvert", screen.StateText);
        Assert.Equal(["bug", "cache"], screen.Labels.Select(l => l.Name));
        Assert.Equal("Assigné à : ada, linus", screen.AssigneesText);
        Assert.Equal("Jalon : v1.0", screen.MilestoneText);
        Assert.Equal("2 commentaires", screen.SummaryText);
        Assert.Empty(_launcher.Opened);
    }

    [Fact]
    public async Task The_description_comments_and_events_are_ready_to_draw()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;

        Assert.True(screen.HasDescription);
        Assert.IsType<MarkdownParagraph>(Assert.Single(screen.Description));
        Assert.Equal(["ada a commenté", "grace a ajouté le label « bug »", "octocat a commenté"], screen.Timeline.Select(t => t.ToString()));
        Assert.False(screen.Timeline[1].HasBody);
        Assert.True(screen.Timeline[2].HasReactions);
    }

    [Fact]
    public async Task A_closed_issue_says_whether_it_was_planned()
    {
        using var tab = await OpenAsync();
        _client.Closed = true;
        await tab.Hosting.ShowIssueAsync(5);
        Assert.Equal("Fermé", tab.Issue.StateText);

        _client.NotPlanned = true;
        await tab.Issue.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Fermé (non planifié)", tab.Issue.StateText);
    }

    [Fact]
    public async Task A_service_without_the_issue_screen_opens_the_browser()
    {
        using var tab = await OpenAsync(detailSupported: false);

        await tab.Hosting.ShowIssueAsync(5);

        Assert.False(tab.Issue.IsOpen);
        Assert.True(tab.ShowGraph);
        Assert.Equal(["https://github.com/atlas/atlas-api/issues/5"], _launcher.Opened);
    }

    [Fact]
    public async Task A_read_failure_is_shown_in_the_screen()
    {
        using var tab = await OpenAsync();
        _client.DetailFailure = "boom";

        await tab.Hosting.ShowIssueAsync(5);

        Assert.True(tab.Issue.IsOpen);
        Assert.Equal("Impossible de lire le ticket : boom", tab.Issue.Message);
        Assert.Null(tab.Issue.Detail);
    }

    [Fact]
    public async Task Closing_the_issue_gives_the_graph_back()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);

        tab.Issue.CloseCommand.Execute(null);

        Assert.False(tab.Issue.IsOpen);
        Assert.True(tab.ShowGraph);
        Assert.Empty(tab.Issue.Timeline);
    }

    [Fact]
    public async Task Opening_a_pull_request_closes_the_issue()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);

        await tab.ShowPullRequestAsync(new PullRequest(7, "Pull 7", "octocat", "fix", null, "main", false, Now, "https://github.com/atlas/atlas-api/pull/7"));

        Assert.False(tab.Issue.IsOpen);
    }

    [Fact]
    public async Task Only_the_signed_in_accounts_own_comments_can_be_changed()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);

        Assert.True(tab.Issue.CanComment);
        Assert.Equal([false, false, true], tab.Issue.Timeline.Select(t => t.CanModify));
        Assert.Equal([true, false, true], tab.Issue.Timeline.Select(t => t.CanReact));
    }

    [Fact]
    public async Task Writes_are_offered_only_when_the_service_supports_them()
    {
        using var tab = await OpenAsync();
        _client.SupportsWrite = false;
        await tab.Hosting.ShowIssueAsync(5);

        Assert.False(tab.Issue.CanComment);
        Assert.False(tab.Issue.AddCommentCommand.CanExecute(null));
        Assert.DoesNotContain(tab.Issue.Timeline, t => t.CanModify || t.CanReact);
    }

    [Fact]
    public async Task Adding_a_comment_appends_it_counts_it_and_clears_the_draft()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;
        Assert.False(screen.AddCommentCommand.CanExecute(null));

        screen.CommentDraft = "  Same here  ";
        Assert.True(screen.AddCommentCommand.CanExecute(null));
        await screen.AddCommentCommand.ExecuteAsync(null);

        Assert.Equal([(5, "Same here")], _client.Added);
        Assert.Equal(4, screen.Timeline.Count);
        Assert.True(screen.Timeline[^1].CanModify);
        Assert.Equal("3 commentaires", screen.SummaryText);
        Assert.Equal(string.Empty, screen.CommentDraft);
    }

    [Fact]
    public async Task The_buttons_of_the_issue_follow_the_write_in_progress()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;
        var changes = 0;
        screen.CloseIssueCommand.CanExecuteChanged += (_, _) => changes++;
        screen.CommentDraft = "Hello";

        await screen.AddCommentCommand.ExecuteAsync(null);

        // Busy, then idle again.
        Assert.Equal(2, changes);
        Assert.True(screen.IsIdle);
    }

    [Fact]
    public async Task A_refused_comment_keeps_the_draft_and_says_why()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;
        _client.WriteFailure = "forbidden";

        screen.CommentDraft = "Hello";
        await screen.AddCommentCommand.ExecuteAsync(null);

        Assert.Equal(3, screen.Timeline.Count);
        Assert.Equal("Hello", screen.CommentDraft);
        var toast = Assert.Single(_notifier.Items);
        Assert.Equal((NotificationSeverity.Error, "forbidden"), (toast.Severity, toast.Message));
    }

    [Fact]
    public async Task Editing_a_comment_replaces_its_text_and_cancelling_changes_nothing()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;
        var row = screen.Timeline[2];

        screen.BeginEditCommand.Execute(row);
        Assert.True(row.IsEditing);
        Assert.Equal("Mine", row.EditText);
        screen.CancelEditCommand.Execute(row);
        Assert.False(row.IsEditing);
        Assert.Empty(_client.Edits);

        screen.BeginEditCommand.Execute(row);
        row.EditText = "Mine, edited";
        await screen.SaveEditCommand.ExecuteAsync(row);

        Assert.Equal([(42L, "Mine, edited")], _client.Edits);
        Assert.Equal(3, screen.Timeline.Count);
        Assert.False(screen.Timeline[2].IsEditing);
        Assert.Equal("Mine, edited", screen.Timeline[2].Entry.Body);
    }

    [Fact]
    public async Task Deleting_a_comment_asks_first()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;
        var row = screen.Timeline[2];

        _confirmations.Answer = false;
        await screen.DeleteCommentCommand.ExecuteAsync(row);
        Assert.Empty(_client.Deleted);
        Assert.Equal(3, screen.Timeline.Count);

        _confirmations.Answer = true;
        await screen.DeleteCommentCommand.ExecuteAsync(row);
        Assert.Equal([42L], _client.Deleted);
        Assert.Equal(2, screen.Timeline.Count);
        Assert.Equal("1 commentaire", screen.SummaryText);
    }

    [Fact]
    public async Task Reacting_toggles_the_accounts_own_reaction_and_redraws_the_comment()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;

        await screen.ToggleReactionAsync(42, ReactionKind.Heart);

        Assert.Equal((42L, ReactionKind.Heart, "octocat"), Assert.Single(_client.Reactions));
        var changed = screen.Timeline.Single(r => r.Entry.Id == 42);
        Assert.Equal(ReactionKind.Heart, Assert.Single(changed.Reactions).Kind);
        Assert.True(changed.CanModify);
    }

    [Fact]
    public async Task Closing_an_issue_writes_the_state_then_reads_the_issue_again()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;
        Assert.True(screen.CanCloseIssue);
        Assert.False(screen.CanReopenIssue);

        await screen.CloseIssueCommand.ExecuteAsync(null);

        Assert.Equal([(5, false, IssueCloseReason.Completed)], _client.StateChanges);
        Assert.Equal("Fermé", screen.StateText);
        Assert.False(screen.CanCloseIssue);
        Assert.True(screen.CanReopenIssue);
    }

    [Fact]
    public async Task An_issue_can_be_closed_as_not_planned_and_reopened()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;

        await screen.CloseIssueAsNotPlannedCommand.ExecuteAsync(null);
        Assert.Equal("Fermé (non planifié)", screen.StateText);

        await screen.ReopenIssueCommand.ExecuteAsync(null);
        Assert.Equal("Ouvert", screen.StateText);
        Assert.Equal([(5, false, IssueCloseReason.NotPlanned), (5, true, IssueCloseReason.Completed)], _client.StateChanges);
    }

    [Fact]
    public async Task A_refused_state_change_keeps_the_state_and_shows_the_reason()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;
        _client.WriteFailure = "forbidden";

        await screen.CloseIssueCommand.ExecuteAsync(null);

        Assert.Equal("Ouvert", screen.StateText);
        Assert.True(screen.CanCloseIssue);
        var toast = Assert.Single(_notifier.Items);
        Assert.Equal((NotificationSeverity.Error, "Ticket non fermé", "forbidden"), (toast.Severity, toast.Title, toast.Message));
    }

    [Fact]
    public async Task Without_write_support_the_state_buttons_are_hidden()
    {
        _client.SupportsWrite = false;
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);

        Assert.False(tab.Issue.CanCloseIssue);
        Assert.False(tab.Issue.CanReopenIssue);
    }

    [Fact]
    public async Task Editing_the_text_asks_with_the_current_values_then_writes_and_reads_the_issue_again()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        var screen = tab.Issue;
        Assert.True(screen.CanEditIssue);
        _inputs.OnText = d =>
        {
            Assert.Equal("Issue 5", d.Value);
            Assert.Equal("Fixes **the cache**.", d.SecondValue);
            d.Name = "  Cache never expires ";
            d.SecondText = "Seen on main.\n";
        };

        await screen.EditTextCommand.ExecuteAsync(null);

        Assert.Equal([(5, "Cache never expires", "Seen on main.")], _client.TextEdits);
        Assert.Empty(_notifier.Items);
    }

    [Fact]
    public async Task Editing_the_text_without_changing_it_writes_nothing()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);

        await tab.Issue.EditTextCommand.ExecuteAsync(null);

        Assert.Empty(_client.TextEdits);
    }

    [Fact]
    public async Task A_canceled_text_edit_writes_nothing()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        _inputs.Answer = false;
        _inputs.OnText = d => d.Name = "Other";

        await tab.Issue.EditTextCommand.ExecuteAsync(null);

        Assert.Empty(_client.TextEdits);
    }

    [Fact]
    public async Task A_refused_text_edit_shows_the_reason()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        _inputs.OnText = d => d.Name = "Other";
        _client.WriteFailure = "forbidden";

        await tab.Issue.EditTextCommand.ExecuteAsync(null);

        var toast = Assert.Single(_notifier.Items);
        Assert.Equal((NotificationSeverity.Error, "Ticket non modifié", "forbidden"), (toast.Severity, toast.Title, toast.Message));
    }

    [Fact]
    public async Task The_metadata_dialog_starts_from_the_current_labels_assignees_and_milestone()
    {
        _client.DetailLabels = ["bug", "cache", "legacy"];
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        IssueMetadataDialogViewModel? seen = null;
        _inputs.OnMetadata = d =>
        {
            seen = d;
            _inputs.Answer = false;
        };

        await tab.Issue.EditMetadataCommand.ExecuteAsync(null);

        Assert.NotNull(seen);
        // "legacy" is on the issue but no longer offered by the repository: it stays listed while it is checked.
        Assert.Equal(["bug", "cache", "docs", "legacy"], seen.Labels.Select(l => l.Name));
        Assert.Equal(["bug", "cache", "legacy"], seen.Labels.Where(l => l.IsSelected).Select(l => l.Name));
        Assert.Equal(["ada", "linus"], seen.Assignees.Where(a => a.IsSelected).Select(a => a.Name));
        Assert.Equal([IssueMetadataDialogViewModel.NoMilestone, "v1.0", "v2.0"], seen.Milestones.Select(m => m.Title));
        Assert.Equal(3, seen.SelectedMilestone.Number);
        Assert.Empty(_client.MetadataChanges);
    }

    [Fact]
    public void The_metadata_dialog_filters_labels_and_assignees_but_never_hides_a_checked_one()
    {
        var options = new IssueMetadataOptions(["bug", "cache", "docs", "Bugfix"], ["ada", "linus", "grace"], []);
        var dialog = new IssueMetadataDialogViewModel("r · #5", options, new IssueMetadata(["docs"], ["grace"], null), null);

        Assert.Equal(["bug", "cache", "docs", "Bugfix"], dialog.VisibleLabels.Select(l => l.Name));

        dialog.LabelFilter = " BUG ";
        // "docs" does not match but is checked: it stays; the match is case-insensitive.
        Assert.Equal(["bug", "docs", "Bugfix"], dialog.VisibleLabels.Select(l => l.Name));

        dialog.AssigneeFilter = "lin";
        Assert.Equal(["linus", "grace"], dialog.VisibleAssignees.Select(a => a.Name));

        dialog.Assignees.Single(a => a.Name == "linus").IsSelected = true;
        dialog.AssigneeFilter = "zzz";
        Assert.Equal(["linus", "grace"], dialog.VisibleAssignees.Select(a => a.Name));

        dialog.LabelFilter = string.Empty;
        Assert.Equal(4, dialog.VisibleLabels.Count);
        // The filter only narrows the view: the result still covers every list.
        Assert.Equal(["docs"], dialog.Result.Labels);
        Assert.Equal(["linus", "grace"], dialog.Result.Assignees);
    }

    [Fact]
    public async Task Confirming_the_metadata_dialog_writes_the_new_sets()
    {
        _client.DetailLabels = ["bug", "cache", "legacy"];
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        _inputs.OnMetadata = d =>
        {
            d.Labels.Single(l => l.Name == "docs").IsSelected = true;
            d.Labels.Single(l => l.Name == "bug").IsSelected = false;
            d.Assignees.Single(a => a.Name == "ada").IsSelected = false;
            d.SelectedMilestone = d.Milestones.Single(m => m.Number == 4);
        };

        await tab.Issue.EditMetadataCommand.ExecuteAsync(null);

        var (number, metadata) = Assert.Single(_client.MetadataChanges);
        Assert.Equal(5, number);
        Assert.Equal(["cache", "docs", "legacy"], metadata.Labels);
        Assert.Equal(["linus"], metadata.Assignees);
        Assert.Equal(4, metadata.MilestoneNumber);
    }

    [Fact]
    public async Task Choosing_no_milestone_removes_it()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        _inputs.OnMetadata = d => d.SelectedMilestone = d.Milestones[0];

        await tab.Issue.EditMetadataCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(_client.MetadataChanges).Metadata.MilestoneNumber);
    }

    [Fact]
    public async Task Confirming_the_metadata_dialog_unchanged_writes_nothing()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);

        await tab.Issue.EditMetadataCommand.ExecuteAsync(null);

        Assert.Empty(_client.MetadataChanges);
    }

    [Fact]
    public async Task A_refused_options_read_shows_the_reason_and_opens_no_dialog()
    {
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);
        _client.WriteFailure = "forbidden";
        var shown = false;
        _inputs.OnMetadata = _ => shown = true;

        await tab.Issue.EditMetadataCommand.ExecuteAsync(null);

        Assert.False(shown);
        var toast = Assert.Single(_notifier.Items);
        Assert.Equal((NotificationSeverity.Error, "Options indisponibles", "forbidden"), (toast.Severity, toast.Title, toast.Message));
    }

    [Fact]
    public async Task Without_write_support_the_edit_buttons_are_hidden()
    {
        _client.SupportsWrite = false;
        using var tab = await OpenAsync();
        await tab.Hosting.ShowIssueAsync(5);

        Assert.False(tab.Issue.CanEditIssue);
    }

    private sealed class FakeInputs : IInputDialogService
    {
        public bool Answer { get; set; } = true;

        public Action<InputDialogViewModel>? OnText { get; set; }

        public Action<IssueMetadataDialogViewModel>? OnMetadata { get; set; }

        public Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            OnText?.Invoke(dialog);
            return Task.FromResult(Answer);
        }

        public Task<bool> ShowAsync(IssueMetadataDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            OnMetadata?.Invoke(dialog);
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeConfirmations : IConfirmationService
    {
        public bool Answer { get; set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer);
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }

    private sealed class StubClient : IHostingClient
    {
        public HostingProvider Provider => HostingProvider.GitHub;

        public bool SupportsDeviceFlow => false;

        public bool SupportsDetail { get; set; } = true;

        public bool SupportsIssueDetail => SupportsDetail;

        public bool SupportsWrite { get; set; } = true;

        public bool SupportsIssueWrite => SupportsWrite;

        public string? WriteFailure { get; set; }

        public List<(int Number, string Body)> Added { get; } = [];

        public List<(long Id, string Body)> Edits { get; } = [];

        public List<long> Deleted { get; } = [];

        public List<(long Id, ReactionKind Kind, string Login)> Reactions { get; } = [];

        // GitResult is a union struct: "no refusal" cannot be a null of it.
        private bool Refuses<T>(out GitResult<T> refusal)
        {
            if (WriteFailure is not { } reason)
            {
                refusal = new Failure("unused");
                return false;
            }

            refusal = new Failure(reason);
            return true;
        }

        private static TimelineEntry Stored(long id, string body, ReactionCount[]? reactions = null) =>
            new(TimelineKind.Comment, "octocat", body, Now, Id: id, Reactions: reactions);

        public Task<GitResult<TimelineEntry>> AddIssueCommentAsync(string token, HostRepository repository, Issue issue, string body, CancellationToken cancellationToken = default)
        {
            if (Refuses<TimelineEntry>(out var refusal)) return Task.FromResult(refusal);
            Added.Add((issue.Number, body));
            return Task.FromResult<GitResult<TimelineEntry>>(new Success<TimelineEntry>(Stored(99, body)));
        }

        public List<(int Number, bool Open, IssueCloseReason Reason)> StateChanges { get; } = [];

        public Task<GitResult<Done>> SetIssueOpenAsync(
            string token, HostRepository repository, Issue issue, bool open, IssueCloseReason reason, CancellationToken cancellationToken = default)
        {
            if (Refuses<Done>(out var refusal)) return Task.FromResult(refusal);
            StateChanges.Add((issue.Number, open, reason));
            Closed = !open;
            NotPlanned = !open && reason == IssueCloseReason.NotPlanned;
            return Task.FromResult<GitResult<Done>>(new Success<Done>(Done.Instance));
        }

        public Task<GitResult<TimelineEntry>> EditPullRequestCommentAsync(string token, HostRepository repository, long commentId, string body, CancellationToken cancellationToken = default)
        {
            if (Refuses<TimelineEntry>(out var refusal)) return Task.FromResult(refusal);
            Edits.Add((commentId, body));
            return Task.FromResult<GitResult<TimelineEntry>>(new Success<TimelineEntry>(Stored(commentId, body)));
        }

        public Task<GitResult<Done>> DeletePullRequestCommentAsync(string token, HostRepository repository, long commentId, CancellationToken cancellationToken = default)
        {
            if (Refuses<Done>(out var refusal)) return Task.FromResult(refusal);
            Deleted.Add(commentId);
            return Task.FromResult<GitResult<Done>>(new Success<Done>(Done.Instance));
        }

        public Task<GitResult<TimelineEntry>> ToggleCommentReactionAsync(
            string token, HostRepository repository, long commentId, ReactionKind reaction, string login, CancellationToken cancellationToken = default)
        {
            if (Refuses<TimelineEntry>(out var refusal)) return Task.FromResult(refusal);
            Reactions.Add((commentId, reaction, login));
            return Task.FromResult<GitResult<TimelineEntry>>(new Success<TimelineEntry>(Stored(commentId, "Mine", [new ReactionCount(reaction, 1)])));
        }

        public List<Issue> Issues { get; } = [];

        public string? DetailFailure { get; set; }

        public bool Closed { get; set; }

        public bool NotPlanned { get; set; }

        public string[] DetailLabels { get; set; } = ["bug", "cache"];

        public List<(int Number, string Title, string Body)> TextEdits { get; } = [];

        public List<(int Number, IssueMetadata Metadata)> MetadataChanges { get; } = [];

        public Task<GitResult<Done>> EditIssueTextAsync(
            string token, HostRepository repository, Issue issue, string title, string body, CancellationToken cancellationToken = default)
        {
            if (Refuses<Done>(out var refusal)) return Task.FromResult(refusal);
            TextEdits.Add((issue.Number, title, body));
            return Task.FromResult<GitResult<Done>>(new Success<Done>(Done.Instance));
        }

        public Task<GitResult<IssueMetadataOptions>> GetIssueMetadataOptionsAsync(string token, HostRepository repository, CancellationToken cancellationToken = default)
        {
            if (Refuses<IssueMetadataOptions>(out var refusal)) return Task.FromResult(refusal);
            return Task.FromResult<GitResult<IssueMetadataOptions>>(new Success<IssueMetadataOptions>(new IssueMetadataOptions(
                ["bug", "cache", "docs"], ["ada", "linus", "grace"], [new MilestoneOption(3, "v1.0"), new MilestoneOption(4, "v2.0")])));
        }

        public Task<GitResult<Done>> SetIssueMetadataAsync(
            string token, HostRepository repository, Issue issue, IssueMetadata metadata, CancellationToken cancellationToken = default)
        {
            if (Refuses<Done>(out var refusal)) return Task.FromResult(refusal);
            MetadataChanges.Add((issue.Number, metadata));
            return Task.FromResult<GitResult<Done>>(new Success<Done>(Done.Instance));
        }

        public HostRepository? ParseRepository(RemoteUrl url) =>
            url.Host == "github.com" && url.DisplayPath.Split('/') is [var owner, var name]
                ? new HostRepository(HostingProvider.GitHub, "github.com", owner, name)
                : null;

        public Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IReadOnlyList<PullRequest>>>(new Success<IReadOnlyList<PullRequest>>([]));

        public Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IReadOnlyList<Issue>>>(new Success<IReadOnlyList<Issue>>([.. Issues]));

        public Task<GitResult<IssueDetail>> GetIssueDetailAsync(string token, HostRepository repository, Issue issue, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IssueDetail>>(DetailFailure is not null
                ? new Failure(DetailFailure)
                : new Success<IssueDetail>(new IssueDetail(
                    issue, "Fixes **the cache**.", Closed ? IssueState.Closed : IssueState.Open, NotPlanned, DetailLabels, ["ada", "linus"], "v1.0",
                    [
                        new TimelineEntry(TimelineKind.Comment, "ada", "Looks bad", Now.AddMinutes(-30), Id: 40),
                        new TimelineEntry(TimelineKind.Event, "grace", "a ajouté le label « bug »", Now.AddMinutes(-20)),
                        new TimelineEntry(TimelineKind.Comment, "octocat", "Mine", Now.AddMinutes(-5), Id: 42,
                            Reactions: [new ReactionCount(ReactionKind.ThumbsUp, 2)]),
                    ])));

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
        public string? Retrieve(HostingProvider provider, string login, string profileId) => "ghp_token";

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
