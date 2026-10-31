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

/// <summary>Pull request screen (J6.9a): opening from the left panel, conversation, commits, files and their diff.</summary>
public sealed class PullRequestViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private const string GitHubUrl = "https://github.com/atlas/atlas-api.git";

    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();
    private readonly StubClient _client = new();
    private readonly Launcher _launcher = new();
    private readonly FakeConfirmations _confirmations = new() { Answer = true };
    private readonly RecordingNotifier _notifier = new();

    public PullRequestViewModelTests() => _repo.Commit("one", ("a.txt", "1\n"));

    public void Dispose()
    {
        foreach (var tab in _tabs) tab.Dispose();
        _repo.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PullRequest Pull(int number) =>
        new(number, $"Pull {number}", "octocat", "fix/cache", null, "main", false, Now, $"https://github.com/atlas/atlas-api/pull/{number}");

    private static PullRequestDetail DetailOf(
        PullRequest pull, PullRequestState state = PullRequestState.Open, bool draft = false, bool behind = false,
        MergeMethod? autoMerge = null, int thumbsUp = 0, IReadOnlyList<ReviewThread>? threads = null,
        string body = "Fixes **the cache**.", string comment = "Looks good") => new(
        pull with { IsDraft = draft }, body, state, ["bug"],
        [
            new TimelineEntry(TimelineKind.Comment, "ada", comment, Now.AddMinutes(-30)),
            new TimelineEntry(TimelineKind.Review, "grace", "Ship it", Now.AddMinutes(-20), ReviewVerdict.Approved),
            new TimelineEntry(TimelineKind.ReviewComment, "linus", "Why?", Now.AddMinutes(-10), Path: "src/cache.cs", Line: 3),
            new TimelineEntry(TimelineKind.Comment, "octocat", "Mine", Now.AddMinutes(-5), Id: 42,
                Reactions: thumbsUp > 0 ? [new ReactionCount(ReactionKind.ThumbsUp, thumbsUp)] : null),
        ],
        [new PullRequestCommit("abc1234def", "Fix the cache", "octocat", Now.AddDays(-1))],
        [
            new PullRequestFile("src/cache.cs", null, FileChangeKind.Modified, 2, 1, "@@ -1,2 +1,3 @@\n a\n-b\n+c\n+d"),
            new PullRequestFile("logo.png", null, FileChangeKind.Added, 0, 0, null),
            new PullRequestFile("broken.cs", null, FileChangeKind.Modified, 1, 0, "@@ nonsense @@\n+x"),
        ],
        NodeId: "PR_node", IsBehind: behind, AutoMerge: autoMerge, Threads: threads);

    private async Task<RepositoryTabViewModel> OpenAsync(bool detailSupported = true, TimeProvider? time = null)
    {
        _client.SupportsDetail = detailSupported;
        _client.PullRequests.AddRange([Pull(7), Pull(8)]);
        _repo.Git("remote", "add", "origin", GitHubUrl);
        var accounts = new HostAccounts(new Store([new HostAccount(HostingProvider.GitHub, "octocat", null, null)]), new Vault(), [_client]);
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, time ?? new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, notifier: _notifier, confirmations: _confirmations, authentication: null, inputs: null, preferences: null, accounts, _launcher);
        await tab.LoadAsync();
        await tab.RefActions.LoadRemotesAsync();
        await tab.Hosting.RefreshAsync();
        return tab;
    }

    // A clock whose timers fire when the test advances it: the checks refresh waits on it (Task.Delay with a TimeProvider).
    // Other view models of the tab (commit details, conflict prevention) wait on it too, from other threads: the list is locked.
    private sealed class SteppedClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<SteppedTimer> _timers = [];
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_timers) return _now;
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SteppedTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            List<SteppedTimer> due;
            lock (_timers)
            {
                _now += delta;
                due = [.. _timers.Where(t => t.Due <= _now)];
            }
            foreach (var timer in due) timer.Fire();
        }

        /// <summary>The checks refreshes waiting (their delay is ChecksRefreshInterval); the waits of the other view models are not counted.</summary>
        // A checks wait only starts once a read is fully shown, so waiting for it never stops on a half-shown read.
        public int Waiting
        {
            get
            {
                lock (_timers) return _timers.Count(t => t.Delay == PullRequestViewModel.ChecksRefreshInterval);
            }
        }

        private sealed class SteppedTimer(SteppedClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = DateTimeOffset.MaxValue;

            public TimeSpan Delay { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers)
                {
                    clock._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan) return true;
                    Due = clock._now + dueTime;
                    Delay = dueTime;
                    clock._timers.Add(this);
                    return true;
                }
            }

            public void Fire()
            {
                lock (clock._timers) clock._timers.Remove(this);
                callback(state);
            }

            public void Dispose()
            {
                lock (clock._timers) clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static CheckRun Check(string name, CheckState state, bool? required = null, int? seconds = null) =>
        new(name, state, null, $"https://ci.example/{name}", seconds is null ? null : Now, seconds is { } s ? Now.AddSeconds(s) : null, required);

    private static GitResult<PullRequestStatus> StatusOf(params CheckRun[] runs) =>
        new Success<PullRequestStatus>(new PullRequestStatus(PullRequestStatus.Summarize(runs), ReviewState.None, runs));

    // A snapshot: the graph finishing its load rebuilds the rows on a pool thread, which would break a plain enumeration of Rows.
    private static RefRow RowOf(RepositoryTabViewModel tab, int number) =>
        tab.Refs.SnapshotRows().Single(r => r.Key == $"pr:{number}");

    [Fact]
    public async Task Opening_a_pull_request_takes_the_main_area_and_reads_its_detail()
    {
        using var tab = await OpenAsync();
        Assert.True(tab.ShowGraph);

        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        Assert.False(tab.ShowGraph);
        var screen = tab.PullRequest;
        Assert.True(screen.IsOpen);
        Assert.False(screen.IsLoading);
        Assert.Equal("Pull 7", screen.Title);
        Assert.Equal("#7", screen.NumberText);
        Assert.Equal("fix/cache → main", screen.BranchText);
        Assert.Equal("Ouverte", screen.StateText);
        Assert.Equal(["bug"], screen.Labels.Select(l => l.Name));
        Assert.Equal("1 commit · 3 fichiers · +3 −1", screen.TotalsText);
        Assert.Empty(_launcher.Opened);
    }

    [Fact]
    public async Task The_screen_reads_the_checks_itself_and_sorts_failures_then_running_then_passed_then_skipped()
    {
        _client.StatusOf = _ => StatusOf(
            Check("lint", CheckState.Success, seconds: 133), Check("docs", CheckState.Skipped), Check("e2e", CheckState.Pending),
            Check("unit", CheckState.Failure, required: false), Check("build", CheckState.Failure, required: true), Check("deploy", CheckState.Canceled),
            Check("sast", CheckState.Success, required: true));
        using var tab = await OpenAsync();

        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        await WaitUntilAsync(() => screen.Checks.Count == 7);

        Assert.Equal(["build", "deploy", "unit", "e2e", "sast", "lint", "docs"], screen.Checks.Select(c => c.Name));
        Assert.Equal("2 réussis · 1 en cours · 3 en échec · 1 ignoré", screen.ChecksSummary);
        Assert.Equal(CheckState.Failure, screen.ChecksState);
        Assert.True(screen.HasRequiredFailure);
        Assert.True(_client.LastIncludeRequired);
        var lint = screen.Checks.Single(c => c.Name == "lint");
        Assert.Equal("2 min 13 s", lint.DurationText);
        Assert.True(screen.Checks[0].IsRequired);
        Assert.Equal("Requis", screen.Checks[0].RequiredText);

        await lint.OpenCommand.ExecuteAsync(null);
        Assert.Equal(["https://ci.example/lint"], _launcher.Opened);
    }

    [Fact]
    public async Task A_check_that_is_not_required_never_blocks_and_skipped_ones_count_for_nothing()
    {
        _client.StatusOf = _ => StatusOf(Check("lint", CheckState.Failure, required: false), Check("docs", CheckState.Skipped));
        using var tab = await OpenAsync();

        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        await tab.PullRequest.ChecksWatching;

        Assert.False(tab.PullRequest.HasRequiredFailure);
        Assert.Equal(CheckState.Failure, tab.PullRequest.ChecksState);

        _client.StatusOf = _ => StatusOf(Check("docs", CheckState.Skipped));
        await tab.PullRequest.RefreshChecksCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => tab.PullRequest.ChecksState == CheckState.None);
        Assert.Equal("1 ignoré", tab.PullRequest.ChecksSummary);
    }

    [Fact]
    public async Task While_a_check_runs_the_screen_reads_again_every_20_seconds_then_stops()
    {
        var clock = new SteppedClock(Now);
        var reads = 0;
        _client.StatusOf = _ => ++reads switch
        {
            1 => StatusOf(Check("build", CheckState.Pending, required: true)),
            2 => StatusOf(Check("build", CheckState.Pending, required: true), Check("lint", CheckState.Success)),
            _ => StatusOf(Check("build", CheckState.Success, required: true), Check("lint", CheckState.Success)),
        };
        using var tab = await OpenAsync(time: clock);
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        await WaitUntilAsync(() => screen.Checks.Count == 1 && clock.Waiting == 1);
        Assert.Equal((1, 1), (_client.ScreenReads, screen.Checks.Count));

        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Equal(1, _client.ScreenReads);

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => _client.ScreenReads == 2 && clock.Waiting == 1);
        Assert.Equal(2, screen.Checks.Count);

        clock.Advance(TimeSpan.FromSeconds(20));
        await screen.ChecksWatching;
        Assert.Equal(3, _client.ScreenReads);
        Assert.Equal(CheckState.Success, screen.ChecksState);
        Assert.Equal(0, clock.Waiting);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(3, _client.ScreenReads);
    }

    [Fact]
    public async Task Closing_the_screen_stops_the_refresh()
    {
        var clock = new SteppedClock(Now);
        _client.StatusOf = _ => StatusOf(Check("build", CheckState.Pending));
        using var tab = await OpenAsync(time: clock);
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        await WaitUntilAsync(() => tab.PullRequest.Checks.Count == 1 && clock.Waiting == 1);

        tab.PullRequest.Close();
        await tab.PullRequest.ChecksWatching;

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, _client.ScreenReads);
        Assert.Empty(tab.PullRequest.Checks);
        Assert.Equal(string.Empty, tab.PullRequest.ChecksSummary);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_previous_checks_and_an_authentication_failure_says_so_once()
    {
        var clock = new SteppedClock(Now);
        var reads = 0;
        _client.StatusOf = _ => ++reads switch
        {
            1 => StatusOf(Check("build", CheckState.Pending)),
            2 => new Failure("offline"),
            _ => new AuthenticationFailed("origin", "token revoked"),
        };
        using var tab = await OpenAsync(time: clock);
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        await WaitUntilAsync(() => screen.Checks.Count == 1 && clock.Waiting == 1);

        clock.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => _client.ScreenReads == 2 && clock.Waiting == 1);
        Assert.Equal(["build"], screen.Checks.Select(c => c.Name));
        Assert.Empty(_notifier.Items);

        clock.Advance(TimeSpan.FromSeconds(20));
        await screen.ChecksWatching;
        Assert.Equal(["build"], screen.Checks.Select(c => c.Name));
        var toast = Assert.Single(_notifier.Items);
        Assert.Equal(NotificationSeverity.Warning, toast.Severity);
        Assert.Equal(0, clock.Waiting);
    }

    [Fact]
    public async Task The_description_and_the_conversation_are_ready_to_draw()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        Assert.True(screen.HasDescription);
        Assert.IsType<MarkdownParagraph>(Assert.Single(screen.Description));
        Assert.Equal(["ada a commenté", "grace a approuvé", "linus a commenté le diff", "octocat a commenté"], screen.Timeline.Select(t => t.ToString()));
        Assert.True(screen.Timeline[1].IsApproved);
        Assert.Equal("src/cache.cs:3", screen.Timeline[2].Location);
        Assert.Equal(PullRequestTab.Conversation, screen.SelectedTab);
        Assert.Equal(["Fix the cache, abc1234"], screen.Commits.Select(c => c.ToString()));
    }

    [Fact]
    public async Task The_first_file_is_selected_and_its_patch_becomes_the_diff()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        Assert.Equal("src/cache.cs", screen.SelectedFile?.Path);
        var diff = Assert.IsType<FileDiff>(screen.Diff);
        Assert.Equal((1, 2, 1), (diff.Hunks.Count, diff.Additions, diff.Deletions));
        Assert.Null(screen.DiffMessage);
        Assert.Equal(1, screen.Files[0].CommentCount);
        Assert.Equal("M", screen.Files[0].StatusLetter);
    }

    [Fact]
    public async Task A_file_without_patch_or_with_an_unreadable_one_says_why()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        screen.SelectedFile = screen.Files[1];
        Assert.Null(screen.Diff);
        Assert.Contains("binaire", screen.DiffMessage, StringComparison.Ordinal);

        screen.SelectedFile = screen.Files[2];
        Assert.Null(screen.Diff);
        Assert.Contains("illisible", screen.DiffMessage, StringComparison.Ordinal);

        screen.SelectedFile = screen.Files[0];
        Assert.NotNull(screen.Diff);
        Assert.Null(screen.DiffMessage);
    }

    [Fact]
    public async Task Closing_goes_back_to_the_graph_and_forgets_the_content()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        tab.PullRequest.CloseCommand.Execute(null);

        Assert.True(tab.ShowGraph);
        Assert.False(tab.PullRequest.IsOpen);
        Assert.Empty(tab.PullRequest.Timeline);
        Assert.Empty(tab.PullRequest.Files);
        Assert.Null(tab.PullRequest.Detail);
    }

    [Fact]
    public async Task Opening_a_file_diff_or_an_inspection_closes_the_pull_request()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        await tab.InspectAsync(new FileInspection(FileInspectionKind.History, "a.txt"));

        Assert.False(tab.PullRequest.IsOpen);
        Assert.False(tab.ShowGraph);
    }

    [Fact]
    public async Task A_read_failure_is_shown_in_place_of_the_content()
    {
        using var tab = await OpenAsync();
        _client.DetailFailure = "boom";

        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        Assert.True(tab.PullRequest.IsOpen);
        Assert.Contains("boom", tab.PullRequest.Message, StringComparison.Ordinal);
        Assert.Null(tab.PullRequest.Detail);
    }

    [Fact]
    public async Task A_service_without_detail_opens_the_browser_instead()
    {
        using var tab = await OpenAsync(detailSupported: false);
        var row = RowOf(tab, 8);

        Assert.False(tab.Refs.MenuFor(row)!.CanShowPullRequest);
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(row);

        Assert.False(tab.PullRequest.IsOpen);
        Assert.Equal(["https://github.com/atlas/atlas-api/pull/8"], _launcher.Opened);
    }

    [Fact]
    public async Task The_menu_of_a_pull_request_offers_the_screen_and_the_browser()
    {
        using var tab = await OpenAsync();

        var menu = tab.Refs.MenuFor(RowOf(tab, 7))!;

        Assert.True(menu.CanShowPullRequest);
        Assert.Equal("https://github.com/atlas/atlas-api/pull/7", menu.OpenUrl);
    }

    [Fact]
    public async Task Another_pull_request_replaces_the_one_shown()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        tab.PullRequest.SelectTabCommand.Execute(PullRequestTab.Files);

        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 8));

        Assert.Equal("#8", tab.PullRequest.NumberText);
        Assert.Equal(PullRequestTab.Conversation, tab.PullRequest.SelectedTab);
        Assert.Equal(3, tab.PullRequest.Files.Count);
    }

    [Fact]
    public async Task The_side_by_side_layout_builds_both_sides_from_the_patch()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        Assert.Null(screen.Sides);

        await screen.ToggleLayoutCommand.ExecuteAsync(null);

        Assert.True(screen.IsSideBySide);
        Assert.Equal("Unifié", screen.LayoutToggleLabel);
        var sides = Assert.IsType<DiffSides>(screen.Sides);
        Assert.Equal(("a\nb", "a\nc\nd"), (sides.Original, sides.Modified));

        screen.SelectedFile = screen.Files[1];
        Assert.Null(screen.Sides);

        await screen.ToggleLayoutCommand.ExecuteAsync(null);
        Assert.False(screen.IsSideBySide);
    }

    [Fact]
    public async Task The_whole_files_replace_the_hunks_once_they_are_read()
    {
        _client.FullSides = new DiffSides("src/cache.cs", "src/cache.cs", "top\na\nb\nbottom", "top\na\nc\nd\nbottom");
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        await screen.ToggleLayoutCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => screen.Sides?.Original.StartsWith("top", StringComparison.Ordinal) == true);

        Assert.Equal("top\na\nc\nd\nbottom", screen.Sides!.Modified);
    }

    [Fact]
    public void Sides_from_hunks_keep_the_real_line_numbers()
    {
        var diff = UnifiedDiffParser.Parse("@@ -3,2 +3,2 @@\n x\n-old\n+new\n@@ -10,0 +10,1 @@\n+added", "f.txt", null);

        var sides = DiffSides.FromHunks(diff);

        // Two filler lines before the first hunk; the second hunk inserts at line 10, after nine lines on both sides.
        var original = sides.Original.Split('\n');
        var modified = sides.Modified.Split('\n');
        Assert.Equal("x", original[2]);
        Assert.Equal("old", original[3]);
        Assert.Equal("new", modified[3]);
        Assert.Equal("added", modified[9]);
        Assert.Equal(10, original.Length);
        Assert.Equal(10, modified.Length);
    }

    [Fact]
    public async Task Writes_are_offered_only_when_the_service_supports_them()
    {
        _client.SupportsWrite = false;
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        var screen = tab.PullRequest;
        Assert.False(screen.CanComment);
        Assert.False(screen.CanMerge);
        Assert.False(screen.CanClose);
        screen.CommentDraft = "hello";
        Assert.False(screen.AddCommentCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_open_pull_request_offers_close_draft_and_merge_but_not_reopen()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        var screen = tab.PullRequest;
        Assert.True(screen.CanComment);
        Assert.True(screen.CanClose);
        Assert.True(screen.CanMerge);
        Assert.True(screen.CanMakeDraft);
        Assert.False(screen.CanMarkReady);
        Assert.False(screen.CanReopen);
    }

    [Fact]
    public async Task Only_the_signed_in_accounts_own_comments_can_be_changed()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        Assert.Equal([false, false, false, true], tab.PullRequest.Timeline.Select(t => t.CanModify));
    }

    [Fact]
    public async Task Adding_a_comment_appends_it_and_clears_the_draft()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        Assert.False(screen.AddCommentCommand.CanExecute(null));

        screen.CommentDraft = "  Thanks!  ";
        Assert.True(screen.AddCommentCommand.CanExecute(null));
        await screen.AddCommentCommand.ExecuteAsync(null);

        Assert.Equal(["Thanks!"], _client.Comments);
        Assert.Equal(5, screen.Timeline.Count);
        Assert.True(screen.Timeline[^1].CanModify);
        Assert.Equal(string.Empty, screen.CommentDraft);
    }

    [Fact]
    public async Task A_refused_comment_keeps_the_draft_and_says_why()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        _client.WriteFailure = "forbidden";

        screen.CommentDraft = "Hello";
        await screen.AddCommentCommand.ExecuteAsync(null);

        Assert.Equal(4, screen.Timeline.Count);
        Assert.Equal("Hello", screen.CommentDraft);
        var toast = Assert.Single(_notifier.Items);
        Assert.Equal((NotificationSeverity.Error, "forbidden"), (toast.Severity, toast.Message));
    }

    [Fact]
    public async Task Editing_a_comment_replaces_its_text()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        var row = screen.Timeline[3];

        screen.BeginEditCommand.Execute(row);
        Assert.True(row.IsEditing);
        Assert.Equal("Mine", row.EditText);
        row.EditText = "Mine, edited";
        await screen.SaveEditCommand.ExecuteAsync(row);

        Assert.Equal([(42L, "Mine, edited")], _client.Edits);
        Assert.Equal(4, screen.Timeline.Count);
        Assert.False(screen.Timeline[3].IsEditing);
        Assert.Equal("Mine, edited", screen.Timeline[3].Entry.Body);
    }

    [Fact]
    public async Task Cancelling_an_edit_changes_nothing()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var row = tab.PullRequest.Timeline[3];

        tab.PullRequest.BeginEditCommand.Execute(row);
        tab.PullRequest.CancelEditCommand.Execute(row);

        Assert.False(row.IsEditing);
        Assert.Empty(_client.Edits);
    }

    [Fact]
    public async Task Deleting_a_comment_asks_first()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        var row = screen.Timeline[3];

        _confirmations.Answer = false;
        await screen.DeleteCommentCommand.ExecuteAsync(row);
        Assert.Empty(_client.Deleted);
        Assert.Equal(4, screen.Timeline.Count);

        _confirmations.Answer = true;
        await screen.DeleteCommentCommand.ExecuteAsync(row);
        Assert.Equal([42L], _client.Deleted);
        Assert.Equal(3, screen.Timeline.Count);
    }

    [Fact]
    public async Task Closing_then_reopening_follows_the_state_of_the_service()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        await screen.ClosePullRequestCommand.ExecuteAsync(null);
        Assert.Equal("Fermée", screen.StateText);
        Assert.True(screen.CanReopen);
        Assert.False(screen.CanClose);
        Assert.False(screen.CanMerge);

        await screen.ReopenPullRequestCommand.ExecuteAsync(null);
        Assert.Equal("Ouverte", screen.StateText);
        Assert.Equal([false, true], _client.OpenStates);
    }

    [Fact]
    public async Task The_draft_state_can_be_toggled()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        await screen.MakeDraftCommand.ExecuteAsync(null);
        Assert.Equal("Brouillon", screen.StateText);
        Assert.True(screen.CanMarkReady);
        Assert.False(screen.CanMerge);

        await screen.MarkReadyCommand.ExecuteAsync(null);
        Assert.Equal("Ouverte", screen.StateText);
        Assert.Equal([true, false], _client.DraftStates);
    }

    [Fact]
    public async Task Updating_the_branch_is_only_offered_when_it_is_behind_and_clears_the_state()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        Assert.False(screen.CanUpdateBranch);

        _client.Behind = true;
        await screen.RefreshCommand.ExecuteAsync(null);
        Assert.True(screen.CanUpdateBranch);

        await screen.UpdateBranchCommand.ExecuteAsync(null);
        Assert.Equal(1, _client.BranchUpdates);
        Assert.False(screen.CanUpdateBranch);
    }

    [Fact]
    public async Task Auto_merge_is_enabled_with_a_method_and_disabled_again()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        Assert.True(screen.CanEnableAutoMerge);
        Assert.False(screen.CanDisableAutoMerge);

        await screen.EnableAutoMergeAsync(MergeMethod.Squash);
        Assert.Equal(MergeMethod.Squash, _client.AutoMerge);
        Assert.False(screen.CanEnableAutoMerge);
        Assert.True(screen.CanDisableAutoMerge);
        Assert.Equal("Fusion automatique : squash", screen.AutoMergeText);

        await screen.DisableAutoMergeCommand.ExecuteAsync(null);
        Assert.Null(_client.AutoMerge);
        Assert.True(screen.CanEnableAutoMerge);
        Assert.Equal(string.Empty, screen.AutoMergeText);
    }

    [Fact]
    public async Task Auto_merge_is_not_offered_on_a_draft()
    {
        using var tab = await OpenAsync();
        _client.Draft = true;
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        Assert.False(tab.PullRequest.CanEnableAutoMerge);
    }

    [Fact]
    public async Task Reacting_to_a_comment_replaces_its_row_with_the_service_answer()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        var mine = screen.Timeline.Single(r => r.Entry.Id == 42);
        Assert.True(mine.CanReact);
        Assert.False(mine.HasReactions);
        Assert.False(screen.Timeline.First().CanReact);

        await screen.ToggleReactionAsync(42, ReactionKind.ThumbsUp);

        var changed = screen.Timeline.Single(r => r.Entry.Id == 42);
        Assert.Equal((42L, ReactionKind.ThumbsUp, "octocat"), Assert.Single(_client.Reactions));
        Assert.Equal("\U0001F44D 1", Assert.Single(changed.Reactions).Text);
        Assert.True(changed.CanReact);

        await screen.ToggleReactionAsync(42, ReactionKind.ThumbsUp);
        Assert.False(screen.Timeline.Single(r => r.Entry.Id == 42).HasReactions);
    }

    [Fact]
    public async Task A_refused_reaction_leaves_the_comment_as_it_was()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        var before = screen.Timeline.Single(r => r.Entry.Id == 42);

        _client.WriteFailure = "forbidden";
        await screen.ToggleReactionAsync(42, ReactionKind.Heart);

        Assert.Same(before, screen.Timeline.Single(r => r.Entry.Id == 42));
    }

    [Fact]
    public async Task A_refused_branch_update_keeps_the_button()
    {
        using var tab = await OpenAsync();
        _client.Behind = true;
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        _client.WriteFailure = "conflicts";
        await screen.UpdateBranchCommand.ExecuteAsync(null);

        Assert.Equal(0, _client.BranchUpdates);
        Assert.True(screen.CanUpdateBranch);
    }

    [Fact]
    public async Task Merging_needs_a_confirmation_and_marks_the_pull_request_merged()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        _confirmations.Answer = false;
        await screen.MergeAsync(MergeMethod.Squash);
        Assert.Empty(_client.Merges);
        Assert.Equal("Ouverte", screen.StateText);

        _confirmations.Answer = true;
        await screen.MergeAsync(MergeMethod.Squash);
        Assert.Equal([MergeMethod.Squash], _client.Merges);
        Assert.Equal("Fusionnée", screen.StateText);
        Assert.False(screen.CanMerge);
        Assert.False(screen.CanReopen);
    }

    [Fact]
    public async Task A_refused_merge_leaves_the_pull_request_as_it_was()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        _client.WriteFailure = "not mergeable";

        await tab.PullRequest.MergeAsync(MergeMethod.Merge);

        Assert.Equal("Ouverte", tab.PullRequest.StateText);
        Assert.Equal("not mergeable", Assert.Single(_notifier.Items).Message);
    }

    // J6.15: the remote is made unreachable (a refused proxy) so that the fetch after the deletion fails fast and offline.
    private async Task<PullRequestViewModel> OpenMergedAsync(string? headRepository, bool branchOnService = true, bool open = false)
    {
        _repo.Git("config", "http.proxy", "http://127.0.0.1:1");
        _repo.Git("config", "credential.helper", "");
        _repo.Branch("fix/cache", checkout: false);
        _client.State = open ? PullRequestState.Open : PullRequestState.Merged;
        _client.HeadRepository = headRepository;
        if (branchOnService) _client.Branches.Add("fix/cache");
        var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        await tab.PullRequest.SourceBranchCheck;
        _tabs.Add(tab);
        return tab.PullRequest;
    }

    private readonly List<RepositoryTabViewModel> _tabs = [];

    [Fact]
    public async Task The_source_branch_of_a_merged_pull_request_is_deleted_on_the_service_and_locally()
    {
        var screen = await OpenMergedAsync("atlas/atlas-api");
        Assert.True(screen.CanDeleteSourceBranch);

        await screen.DeleteSourceBranchCommand.ExecuteAsync(null);

        Assert.Equal(["fix/cache"], _client.DeletedBranches);
        Assert.False(screen.CanDeleteSourceBranch);
        Assert.Equal(string.Empty, _repo.Git("branch", "--list", "fix/cache").Trim());
    }

    [Fact]
    public async Task Declining_the_confirmation_keeps_the_source_branch()
    {
        var screen = await OpenMergedAsync("atlas/atlas-api");
        _confirmations.Answer = false;

        await screen.DeleteSourceBranchCommand.ExecuteAsync(null);

        Assert.Empty(_client.DeletedBranches);
        Assert.True(screen.CanDeleteSourceBranch);
    }

    [Theory]
    [InlineData("someone/atlas-api", true, false)] // a fork's branch is not ours
    [InlineData("atlas/atlas-api", false, false)] // already gone
    [InlineData("atlas/atlas-api", true, true)] // the pull request is still open
    public async Task The_source_branch_is_not_offered_for_a_fork_a_missing_branch_or_an_open_pull_request(string head, bool onService, bool open)
    {
        var screen = await OpenMergedAsync(head, onService, open);

        Assert.False(screen.CanDeleteSourceBranch);
    }

    private static ReviewThread ThreadOn(string path, string id = "T1", bool resolved = false) => new(
        id, path, 3, ReviewSide.New, resolved, false, [new ReviewThreadComment(11, "linus", "Why a lock?", Now.AddMinutes(-10))]);

    [Fact]
    public async Task The_threads_of_the_selected_file_are_listed_and_follow_the_selection()
    {
        using var tab = await OpenAsync();
        _client.Threads.AddRange([ThreadOn("src/cache.cs"), ThreadOn("broken.cs", "T2", resolved: true)]);
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        var thread = Assert.Single(screen.FileThreads);
        Assert.Equal(("Ligne 3", string.Empty, "Marquer comme résolue"), (thread.Location, thread.StatusText, thread.ResolveLabel));
        Assert.Equal("linus", Assert.Single(thread.Comments).Author);

        screen.SelectedFile = screen.Files.Single(f => f.Path == "broken.cs");

        var other = Assert.Single(screen.FileThreads);
        Assert.Equal(("Résolue", "Rouvrir"), (other.StatusText, other.ResolveLabel));
        screen.SelectedFile = screen.Files.Single(f => f.Path == "logo.png");
        Assert.Empty(screen.FileThreads);
    }

    [Fact]
    public async Task Each_file_shows_the_start_of_its_threads_under_its_name()
    {
        using var tab = await OpenAsync();
        _client.Threads.AddRange([ThreadOn("src/cache.cs"), ThreadOn("broken.cs", "T2", resolved: true)]);
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        // Every file keeps its previews, not only the selected one.
        var cache = screen.Files.Single(f => f.Path == "src/cache.cs");
        var preview = Assert.Single(cache.Previews);
        Assert.Equal(("T1", "linus", "L3", "Why a lock?"), (preview.ThreadId, preview.Author, preview.LineText, preview.Snippet));
        Assert.True(cache.HasComments);
        Assert.True(Assert.Single(screen.Files.Single(f => f.Path == "broken.cs").Previews).IsResolved);
        Assert.Empty(screen.Files.Single(f => f.Path == "logo.png").Previews);
    }

    [Fact]
    public async Task A_long_first_comment_is_cut_to_its_first_line_in_the_preview()
    {
        using var tab = await OpenAsync();
        var body = "First line\nsecond line";
        _client.Threads.Add(ThreadOn("src/cache.cs") with { Comments = [new ReviewThreadComment(11, "linus", body, Now)] });
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        Assert.Equal("First line", Assert.Single(tab.PullRequest.Files.Single(f => f.Path == "src/cache.cs").Previews).Snippet);
    }

    [Fact]
    public async Task The_files_are_shown_as_a_tree_of_folders_that_fold()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        Assert.Equal(["broken.cs", "logo.png", "src/cache.cs"], screen.FileRows.Select(r => r.Path));

        screen.ShowAsTree = true;

        Assert.Equal(["src", "src/cache.cs", "broken.cs", "logo.png"], screen.FileRows.Select(r => r.Path));
        var folder = Assert.IsType<FolderRow>(screen.FileRows[0]);
        screen.ToggleFolderCommand.Execute(folder);
        Assert.Equal(["src", "broken.cs", "logo.png"], screen.FileRows.Select(r => r.Path));
        Assert.Equal("src/cache.cs", screen.SelectedFile?.Path);
    }

    [Fact]
    public async Task Opening_a_preview_selects_its_file_and_asks_the_editor_to_scroll_to_the_thread()
    {
        using var tab = await OpenAsync();
        _client.Threads.Add(ThreadOn("broken.cs", "T2"));
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        var revision = screen.ReviewRevision;
        var token = screen.RevealToken;

        screen.OpenThreadCommand.Execute(screen.Files.Single(f => f.Path == "broken.cs").Previews[0]);

        Assert.Equal("broken.cs", screen.SelectedFile?.Path);
        Assert.Equal("T2", screen.RevealThreadId);
        Assert.NotEqual(token, screen.RevealToken);
        Assert.NotEqual(revision, screen.ReviewRevision);

        // The same file again: asked once more, the editor being told by a new token.
        token = screen.RevealToken;
        screen.OpenThreadCommand.Execute(screen.Files.Single(f => f.Path == "broken.cs").Previews[0]);
        Assert.NotEqual(token, screen.RevealToken);
    }

    [Fact]
    public async Task Replying_answers_the_first_comment_and_adds_the_reply_to_the_thread()
    {
        using var tab = await OpenAsync();
        _client.Threads.Add(ThreadOn("src/cache.cs"));
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        var row = Assert.Single(screen.FileThreads);

        row.ReplyText = "  Because of threads  ";
        await screen.ReplyCommand.ExecuteAsync(row);

        Assert.Equal((11L, "Because of threads"), Assert.Single(_client.Replies));
        Assert.Equal(["linus", "octocat"], Assert.Single(screen.FileThreads).Comments.Select(c => c.Author));
        Assert.Empty(_notifier.Items);
    }

    [Fact]
    public async Task A_refused_reply_keeps_the_text_for_another_try()
    {
        using var tab = await OpenAsync();
        _client.Threads.Add(ThreadOn("src/cache.cs"));
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        var row = Assert.Single(screen.FileThreads);
        row.ReplyText = "Hello";

        _client.WriteFailure = "locked";
        await screen.ReplyCommand.ExecuteAsync(row);

        Assert.Same(row, Assert.Single(screen.FileThreads));
        Assert.Equal("Hello", row.ReplyText);
        Assert.Equal("locked", Assert.Single(_notifier.Items).Message);
    }

    [Fact]
    public async Task Resolving_and_reopening_a_thread_changes_its_state_in_place()
    {
        using var tab = await OpenAsync();
        _client.Threads.Add(ThreadOn("src/cache.cs"));
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        await screen.ToggleResolvedCommand.ExecuteAsync(Assert.Single(screen.FileThreads));
        Assert.True(Assert.Single(screen.FileThreads).IsResolved);

        await screen.ToggleResolvedCommand.ExecuteAsync(Assert.Single(screen.FileThreads));
        Assert.False(Assert.Single(screen.FileThreads).IsResolved);
        Assert.Equal([("T1", true), ("T1", false)], _client.Resolutions);
        // The screen stayed on the same file: nothing was read again.
        Assert.Equal("src/cache.cs", screen.SelectedFile?.Path);
    }

    [Fact]
    public async Task A_comment_on_a_line_the_diff_does_not_show_is_refused()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        screen.DraftLine = 40;
        screen.DraftText = "Nope";
        screen.AddDraftCommand.Execute(null);

        Assert.Empty(screen.Drafts);
        Assert.Equal(NotificationSeverity.Warning, Assert.Single(_notifier.Items).Severity);
        Assert.Equal("Nope", screen.DraftText);
    }

    [Fact]
    public async Task Comments_on_lines_are_kept_on_either_side_until_the_review_is_sent()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        Assert.False(screen.AddDraftCommand.CanExecute(null));

        // New line 3 is the added "d"; old line 2 is the deleted "b".
        screen.DraftLine = 3;
        screen.DraftText = "Rename this";
        Assert.True(screen.AddDraftCommand.CanExecute(null));
        screen.AddDraftCommand.Execute(null);
        screen.DraftLine = 2;
        screen.DraftOnOldSide = true;
        screen.DraftText = "Why remove it?";
        screen.AddDraftCommand.Execute(null);

        Assert.Equal(["src/cache.cs : ligne 3", "src/cache.cs : ligne 2 (ancienne version)"], screen.Drafts.Select(d => d.Location));
        Assert.Equal("2 commentaires en attente", screen.DraftsText);
        Assert.Equal(string.Empty, screen.DraftText);

        screen.RemoveDraftCommand.Execute(screen.Drafts[0]);
        Assert.Equal("1 commentaire en attente", screen.DraftsText);
    }

    [Fact]
    public async Task Sending_the_review_sends_the_comments_clears_them_and_stays_on_the_file()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.SelectedFile = screen.Files.Single(f => f.Path == "src/cache.cs");
        screen.SelectedTab = PullRequestTab.Files;
        screen.DraftLine = 3;
        screen.DraftText = "Rename this";
        screen.AddDraftCommand.Execute(null);
        screen.ReviewText = " Mostly fine ";

        await screen.SubmitReviewAsync(ReviewDecision.Approve);

        var (decision, body, comments) = Assert.Single(_client.Reviews);
        Assert.Equal((ReviewDecision.Approve, "Mostly fine"), (decision, body));
        Assert.Equal(new NewReviewComment("src/cache.cs", 3, ReviewSide.New, "Rename this"), Assert.Single(comments));
        Assert.Empty(screen.Drafts);
        Assert.Equal(string.Empty, screen.ReviewText);
        Assert.Equal(PullRequestTab.Files, screen.SelectedTab);
        Assert.Equal("src/cache.cs", screen.SelectedFile?.Path);
        Assert.Equal(NotificationSeverity.Success, Assert.Single(_notifier.Items).Severity);
    }

    [Fact]
    public async Task A_refused_review_keeps_the_comments()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.DraftLine = 3;
        screen.DraftText = "Rename this";
        screen.AddDraftCommand.Execute(null);

        _client.WriteFailure = "Can not approve your own pull request";
        await screen.SubmitReviewAsync(ReviewDecision.Approve);

        Assert.Empty(_client.Reviews);
        Assert.Single(screen.Drafts);
        Assert.Equal("Can not approve your own pull request", Assert.Single(_notifier.Items).Message);
    }

    [Theory]
    [InlineData(ReviewDecision.RequestChanges)]
    [InlineData(ReviewDecision.Comment)]
    public async Task A_review_that_says_nothing_is_not_sent(ReviewDecision decision)
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        await tab.PullRequest.SubmitReviewAsync(decision);

        Assert.Empty(_client.Reviews);
        Assert.Equal(NotificationSeverity.Warning, Assert.Single(_notifier.Items).Severity);
    }

    [Fact]
    public async Task A_request_for_changes_needs_text_even_with_comments_on_lines()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.DraftLine = 3;
        screen.DraftText = "Rename this";
        screen.AddDraftCommand.Execute(null);

        await screen.SubmitReviewAsync(ReviewDecision.RequestChanges);
        Assert.Empty(_client.Reviews);

        // A review with comments but no text is fine as a plain comment.
        await screen.SubmitReviewAsync(ReviewDecision.Comment);
        Assert.Equal(ReviewDecision.Comment, Assert.Single(_client.Reviews).Decision);
    }

    [Fact]
    public async Task Comments_kept_for_a_review_survive_a_refresh_but_not_another_pull_request()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.DraftLine = 3;
        screen.DraftText = "Rename this";
        screen.AddDraftCommand.Execute(null);

        await screen.RefreshCommand.ExecuteAsync(null);
        Assert.Single(screen.Drafts);

        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 8));
        Assert.Empty(screen.Drafts);
        Assert.False(screen.HasDrafts);
    }

    [Fact]
    public async Task Reviewing_is_not_offered_when_the_service_cannot_be_written()
    {
        using var tab = await OpenAsync();
        _client.SupportsWrite = false;
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        Assert.False(screen.CanReview);
        Assert.False(screen.CanSubmitReview);
        screen.DraftLine = 3;
        screen.DraftText = "x";
        Assert.False(screen.AddDraftCommand.CanExecute(null));
    }

    [Fact]
    public async Task Threads_can_be_answered_and_resolved_when_only_the_review_is_writable()
    {
        using var tab = await OpenAsync();
        _client.SupportsWrite = false;
        _client.SupportsReview = true;
        _client.Threads.Add(ThreadOn("src/cache.cs"));
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        var row = Assert.Single(screen.FileThreads);

        Assert.False(screen.CanComment);
        Assert.True(screen.CanReview);
        row.ReplyText = "Because";
        await screen.ReplyCommand.ExecuteAsync(row);
        await screen.ToggleResolvedCommand.ExecuteAsync(screen.FileThreads[0]);

        Assert.Single(_client.Replies);
        Assert.Equal(("T1", true), Assert.Single(_client.Resolutions));
    }

    [Fact]
    public async Task Merging_is_offered_without_the_other_writes()
    {
        using var tab = await OpenAsync();
        _client.SupportsWrite = false;
        _client.SupportsMerge = true;
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        Assert.True(screen.CanMerge);
        Assert.False(screen.CanUpdateBranch);
        Assert.False(screen.CanEnableAutoMerge);

        _confirmations.Answer = true;
        await screen.MergeAsync(MergeMethod.Rebase);

        Assert.Equal([MergeMethod.Rebase], _client.Merges);
    }

    [Fact]
    public async Task Commenting_and_state_changes_are_offered_without_merging_or_editing()
    {
        using var tab = await OpenAsync();
        _client.SupportsWrite = false;
        _client.SupportsComment = true;
        _client.SupportsState = true;
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        Assert.True(screen.CanComment);
        Assert.True(screen.CanClose);
        Assert.True(screen.CanMakeDraft);
        Assert.False(screen.CanMerge);
        Assert.False(screen.CanUpdateBranch);

        screen.CommentDraft = "Looks good";
        Assert.True(screen.AddCommentCommand.CanExecute(null));
        await screen.AddCommentCommand.ExecuteAsync(null);
        await screen.ClosePullRequestCommand.ExecuteAsync(null);

        Assert.Equal("Looks good", Assert.Single(_client.Comments));
        Assert.False(Assert.Single(_client.OpenStates));
        Assert.All(screen.Timeline, row => Assert.False(row.CanModify));
    }

    [Fact]
    public async Task Voting_is_offered_when_the_service_has_votes_and_is_recorded()
    {
        using var tab = await OpenAsync();
        _client.SupportsVote = true;
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        Assert.True(screen.CanVote);
        await screen.VoteAsync(ReviewVote.WaitingForAuthor);

        Assert.Equal(ReviewVote.WaitingForAuthor, Assert.Single(_client.Votes));
        Assert.Equal(NotificationSeverity.Success, Assert.Single(_notifier.Items).Severity);
    }

    [Fact]
    public async Task A_refused_vote_is_reported()
    {
        using var tab = await OpenAsync();
        _client.SupportsVote = true;
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));

        _client.WriteFailure = "TF401027: You need the PullRequestContribute permission";
        await tab.PullRequest.VoteAsync(ReviewVote.Approved);

        Assert.Empty(_client.Votes);
        Assert.Equal(NotificationSeverity.Error, Assert.Single(_notifier.Items).Severity);
    }

    [Fact]
    public async Task Voting_is_not_offered_without_votes_or_on_a_closed_pull_request()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        Assert.False(tab.PullRequest.CanVote);

        _client.SupportsVote = true;
        _client.State = PullRequestState.Closed;
        await tab.PullRequest.RefreshCommand.ExecuteAsync(null);
        Assert.False(tab.PullRequest.CanVote);

        await tab.PullRequest.VoteAsync(ReviewVote.Approved);
        Assert.Empty(_client.Votes);
    }

    [Fact]
    public async Task Discussions_of_a_file_stay_readable_when_the_service_cannot_review()
    {
        using var tab = await OpenAsync();
        _client.SupportsWrite = false;
        _client.Threads.Add(new ReviewThread("9", "src/cache.cs", 3, ReviewSide.New, false, false, [new ReviewThreadComment(1, "ada", "Rename this", Now)]));
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.SelectedFile = screen.Files.Single(f => f.Path == "src/cache.cs");

        Assert.False(screen.CanReview);
        Assert.False(screen.ShowReviewPanel); // the discussions are drawn in the diff (J6.11); the panel only sends a review
        var row = Assert.Single(screen.FileThreads);
        Assert.False(row.CanReview);
    }

    [Fact]
    public async Task A_comment_written_in_the_editor_is_published_at_once_with_its_selection()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.SelectedFile = screen.Files.Single(f => f.Path == "src/cache.cs");
        var comment = new NewReviewComment("src/cache.cs", 3, ReviewSide.New, " Rename this ", StartLine: 2, StartColumn: 4, EndColumn: 9);

        Assert.True(await screen.AddLineCommentAsync(comment, asDraft: false));

        var (decision, body, comments) = Assert.Single(_client.Reviews);
        Assert.Equal((ReviewDecision.Comment, string.Empty), (decision, body));
        Assert.Equal(comment with { Body = "Rename this" }, Assert.Single(comments));
        Assert.Empty(screen.Drafts);
    }

    [Fact]
    public async Task A_comment_written_in_the_editor_can_be_kept_for_the_review()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.SelectedFile = screen.Files.Single(f => f.Path == "src/cache.cs");
        var revision = screen.ReviewRevision;

        Assert.True(await screen.AddLineCommentAsync(new NewReviewComment("src/cache.cs", 3, ReviewSide.New, "Later"), asDraft: true));

        Assert.Empty(_client.Reviews);
        Assert.Equal("Later", Assert.Single(screen.Drafts).Text);
        Assert.NotEqual(revision, screen.ReviewRevision);

        screen.RemoveDraftAt(0);
        Assert.Empty(screen.Drafts);
    }

    [Fact]
    public async Task A_comment_on_a_line_the_diff_does_not_show_is_refused_on_GitHub()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.SelectedFile = screen.Files.Single(f => f.Path == "src/cache.cs");

        Assert.False(await screen.AddLineCommentAsync(new NewReviewComment("src/cache.cs", 999, ReviewSide.New, "Nowhere"), asDraft: false));

        Assert.Empty(_client.Reviews);
        Assert.Equal(NotificationSeverity.Warning, Assert.Single(_notifier.Items).Severity);
    }

    [Fact]
    public async Task A_thread_is_replied_to_and_resolved_by_its_id()
    {
        using var tab = await OpenAsync();
        _client.Threads.Add(new ReviewThread("9", "src/cache.cs", 3, ReviewSide.New, false, false, [new ReviewThreadComment(1, "ada", "Rename this", Now)]));
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;
        screen.SelectedFile = screen.Files.Single(f => f.Path == "src/cache.cs");

        Assert.True(await screen.ReplyToThreadAsync("9", " Done "));
        Assert.Equal((1L, "Done"), Assert.Single(_client.Replies));
        Assert.Equal(2, Assert.Single(screen.FileThreads).Comments.Count);

        Assert.True(await screen.SetThreadResolvedAsync("9", true));
        Assert.Equal(("9", true), Assert.Single(_client.Resolutions));
        Assert.True(Assert.Single(screen.FileThreads).IsResolved);

        Assert.False(await screen.ReplyToThreadAsync("unknown", "Lost"));
    }

    [Fact]
    public async Task Completion_proposals_come_from_the_service_when_it_has_them()
    {
        using var tab = await OpenAsync();
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        Assert.False(screen.CanCompleteReferences);
        Assert.Empty(await screen.SearchReferencesAsync(ReferenceKind.Issue, "cache", TestContext.Current.CancellationToken));

        _client.SupportsReferences = true;
        Assert.True(screen.CanCompleteReferences);
        var proposal = Assert.Single(await screen.SearchReferencesAsync(ReferenceKind.Issue, "cache", TestContext.Current.CancellationToken));
        Assert.Equal("#7", proposal.Insert);
        Assert.Equal((ReferenceKind.Issue, "cache"), Assert.Single(_client.ReferenceQueries));
    }

    [Fact]
    public async Task People_mentioned_by_identifier_are_named_once()
    {
        const string Ada = "9d6f3b2a-1c4e-4f7a-8b21-5e0c7a1d2f34";
        using var tab = await OpenAsync();
        _client.SupportsReferences = true;
        _client.MentionIds[Ada] = "Ada Lovelace";
        _client.Threads.Add(new ReviewThread("9", "src/cache.cs", 3, ReviewSide.New, false, false,
            [new ReviewThreadComment(1, "linus", $"Ping @<{Ada}> and @<00000000-0000-0000-0000-000000000000>", Now)]));
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        // The lookup may end before or after the file is selected (it starts when the pull request is read): wait for the
        // card to show the name, the unknown person keeping the placeholder.
        screen.SelectedFile = screen.Files.Single(f => f.Path == "src/cache.cs");
        string[] Mentions() =>
            screen.FileThreads.SingleOrDefault()?.Comments.Single().Body.OfType<MarkdownParagraph>().SingleOrDefault()?.Content
                .OfType<MarkdownEmphasis>().Select(e => Assert.IsType<MarkdownText>(Assert.Single(e.Content)).Text).ToArray() ?? [];
        await WaitUntilAsync(() => Mentions().Contains("@Ada Lovelace"));
        Assert.Equal(["@Ada Lovelace", "@" + Loc.Get("ReviewEditor.UnknownPerson")], Mentions());

        screen.SelectedFile = screen.Files.First(f => f.Path != "src/cache.cs");
        screen.SelectedFile = screen.Files.Single(f => f.Path == "src/cache.cs");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, _client.MentionLookups);
    }

    [Fact]
    public async Task People_mentioned_by_identifier_in_the_conversation_are_named_once_read()
    {
        const string Ada = "9d6f3b2a-1c4e-4f7a-8b21-5e0c7a1d2f34";
        const string Grace = "ad6f3b2a-1c4e-4f7a-8b21-5e0c7a1d2f34";
        using var tab = await OpenAsync();
        _client.SupportsReferences = true;
        _client.MentionIds[Ada] = "Ada Lovelace";
        _client.MentionIds[Grace] = "Grace Hopper";
        _client.Body = $"For @<{Ada}>";
        _client.Comment = $"Thanks @<{Grace}>";
        await tab.Refs.ShowPullRequestCommand.ExecuteAsync(RowOf(tab, 7));
        var screen = tab.PullRequest;

        static string? Mention(IReadOnlyList<MarkdownBlock> blocks) =>
            Assert.IsType<MarkdownParagraph>(Assert.Single(blocks)).Content.OfType<MarkdownEmphasis>()
                .Select(e => Assert.IsType<MarkdownText>(Assert.Single(e.Content)).Text).SingleOrDefault();

        await WaitUntilAsync(() => Mention(screen.Description) == "@Ada Lovelace" && Mention(screen.Timeline[0].Body) == "@Grace Hopper");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, _client.MentionLookups);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
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

        public bool SupportsPullRequestDetail => SupportsDetail;

        public List<PullRequest> PullRequests { get; } = [];

        // Checks of the pull request screen (J6.12): what each read answers, and how many times the screen (not the left panel) asked.
        public Func<int, GitResult<PullRequestStatus>>? StatusOf { get; set; }

        public int ScreenReads { get; private set; }

        public bool? LastIncludeRequired { get; private set; }

        public Task<GitResult<PullRequestStatus>> GetPullRequestStatusAsync(
            string token, HostRepository repository, PullRequest pullRequest, bool includeRequired = false, CancellationToken cancellationToken = default)
        {
            if (!includeRequired) return Task.FromResult<GitResult<PullRequestStatus>>(new Success<PullRequestStatus>(new PullRequestStatus(CheckState.None, ReviewState.None, [])));
            ScreenReads++;
            LastIncludeRequired = includeRequired;
            return Task.FromResult(StatusOf?.Invoke(pullRequest.Number) ?? new Success<PullRequestStatus>(new PullRequestStatus(CheckState.None, ReviewState.None, [])));
        }

        public string? DetailFailure { get; set; }

        public DiffSides? FullSides { get; set; }

        public bool SupportsPullRequestSides => FullSides is not null;

        public Task<GitResult<DiffSides>> GetPullRequestFileSidesAsync(
            string token, HostRepository repository, PullRequest pullRequest, PullRequestFile file, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<DiffSides>>(FullSides is { } sides ? new Success<DiffSides>(sides) : new Failure("no sides"));

        public bool SupportsWrite { get; set; } = true;

        public bool SupportsPullRequestWrite => SupportsWrite;

        // Azure DevOps comments, closes and changes the draft state, but does not edit comments or merge (yet).
        public bool? SupportsComment { get; set; }

        public bool SupportsPullRequestComment => SupportsComment ?? SupportsWrite;

        public bool? SupportsState { get; set; }

        public bool SupportsPullRequestState => SupportsState ?? SupportsWrite;

        public bool? SupportsMerge { get; set; }

        public bool SupportsPullRequestMerge => SupportsMerge ?? SupportsWrite;

        public bool? SupportsReview { get; set; }

        // Completion after #, ! and @ (J6.12).
        public bool SupportsReferences { get; set; }

        public bool SupportsReferenceSearch => SupportsReferences;

        public List<(ReferenceKind Kind, string Query)> ReferenceQueries { get; } = [];

        public Dictionary<string, string> MentionIds { get; } = [];

        public string Body { get; set; } = "Fixes **the cache**.";

        public string Comment { get; set; } = "Looks good";

        public int MentionLookups { get; private set; }

        public Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchReferencesAsync(
            string token, HostRepository repository, ReferenceKind kind, string query, CancellationToken cancellationToken = default) =>
            Write<IReadOnlyList<ReferenceSuggestion>>(() =>
            {
                ReferenceQueries.Add((kind, query));
                return [new ReferenceSuggestion("Fix the cache", "#7 · issue · open", "#7")];
            });

        public Task<GitResult<IReadOnlyDictionary<string, string>>> ResolveMentionsAsync(
            string token, HostRepository repository, IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
            Write<IReadOnlyDictionary<string, string>>(() =>
            {
                MentionLookups++;
                return ids.Where(MentionIds.ContainsKey).ToDictionary(id => id, id => MentionIds[id]);
            });

        public bool SupportsPullRequestReview => SupportsReview ?? SupportsWrite;

        public bool SupportsVote { get; set; }

        public bool SupportsPullRequestVote => SupportsVote;

        public List<ReviewVote> Votes { get; } = [];

        public Task<GitResult<Done>> SetPullRequestVoteAsync(
            string token, HostRepository repository, PullRequest pullRequest, ReviewVote vote, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Votes.Add(vote);
                return Done.Instance;
            });

        // Threads the service holds, changed by the review writes below.
        public List<ReviewThread> Threads { get; } = [];

        public List<(long InReplyTo, string Body)> Replies { get; } = [];

        public List<(string ThreadId, bool Resolved)> Resolutions { get; } = [];

        public List<(ReviewDecision Decision, string Body, IReadOnlyList<NewReviewComment> Comments)> Reviews { get; } = [];

        public Task<GitResult<ReviewThreadComment>> ReplyToReviewThreadAsync(
            string token, HostRepository repository, PullRequest pullRequest, ReviewThread thread, string body, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Replies.Add((thread.Comments[0].Id, body));
                return new ReviewThreadComment(500 + Replies.Count, "octocat", body, Now);
            });

        public Task<GitResult<Done>> SetReviewThreadResolvedAsync(
            string token, HostRepository repository, PullRequest pullRequest, ReviewThread thread, bool resolved, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Resolutions.Add((thread.Id, resolved));
                return Done.Instance;
            });

        public Task<GitResult<Done>> SubmitReviewAsync(
            string token, HostRepository repository, PullRequest pullRequest, ReviewDecision decision, string body,
            IReadOnlyList<NewReviewComment> comments, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Reviews.Add((decision, body, comments));
                return Done.Instance;
            });

        // What the service holds, changed by the writes below.
        public PullRequestState State { get; set; } = PullRequestState.Open;

        public bool Draft { get; set; }

        public bool Behind { get; set; }

        public int BranchUpdates { get; private set; }

        public MergeMethod? AutoMerge { get; set; }

        public int ThumbsUp { get; set; }

        public List<(long Id, ReactionKind Kind, string Login)> Reactions { get; } = [];

        public string? WriteFailure { get; set; }

        public List<string> Comments { get; } = [];

        public List<(long Id, string Body)> Edits { get; } = [];

        public List<long> Deleted { get; } = [];

        public List<bool> OpenStates { get; } = [];

        public List<bool> DraftStates { get; } = [];

        public List<MergeMethod> Merges { get; } = [];

        private Task<GitResult<T>> Write<T>(Func<T> apply) =>
            Task.FromResult<GitResult<T>>(WriteFailure is { } failure ? new Failure(failure) : new Success<T>(apply()));

        public Task<GitResult<TimelineEntry>> AddPullRequestCommentAsync(
            string token, HostRepository repository, PullRequest pullRequest, string body, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Comments.Add(body);
                return new TimelineEntry(TimelineKind.Comment, "octocat", body, Now, Id: 100 + Comments.Count);
            });

        public Task<GitResult<TimelineEntry>> EditPullRequestCommentAsync(
            string token, HostRepository repository, long commentId, string body, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Edits.Add((commentId, body));
                return new TimelineEntry(TimelineKind.Comment, "octocat", body, Now, Id: commentId);
            });

        public Task<GitResult<Done>> DeletePullRequestCommentAsync(
            string token, HostRepository repository, long commentId, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Deleted.Add(commentId);
                return Done.Instance;
            });

        public Task<GitResult<Done>> SetPullRequestOpenAsync(
            string token, HostRepository repository, PullRequest pullRequest, bool open, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                OpenStates.Add(open);
                State = open ? PullRequestState.Open : PullRequestState.Closed;
                return Done.Instance;
            });

        public Task<GitResult<Done>> SetPullRequestDraftAsync(
            string token, HostRepository repository, PullRequestDetail detail, bool draft, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                DraftStates.Add(draft);
                Draft = draft;
                return Done.Instance;
            });

        public Task<GitResult<Done>> MergePullRequestAsync(
            string token, HostRepository repository, PullRequest pullRequest, MergeMethod method, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Merges.Add(method);
                State = PullRequestState.Merged;
                return Done.Instance;
            });

        public Task<GitResult<Done>> UpdatePullRequestBranchAsync(
            string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                BranchUpdates++;
                Behind = false;
                return Done.Instance;
            });

        // Source branches of closed pull requests (J6.15): the head repository the listed pull requests report, what exists, what was deleted.
        public string? HeadRepository { get; set; }

        public HashSet<string> Branches { get; } = [];

        public List<string> DeletedBranches { get; } = [];

        public bool SupportsBranchDelete => SupportsWrite;

        public Task<GitResult<bool>> BranchExistsAsync(
            string token, HostRepository repository, string branch, CancellationToken cancellationToken = default) =>
            Write(() => Branches.Contains(branch));

        public Task<GitResult<Done>> DeleteBranchAsync(
            string token, HostRepository repository, string branch, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Branches.Remove(branch);
                DeletedBranches.Add(branch);
                return Done.Instance;
            });

        public Task<GitResult<Done>> SetPullRequestAutoMergeAsync(
            string token, HostRepository repository, PullRequestDetail detail, MergeMethod? method, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                AutoMerge = method;
                return Done.Instance;
            });

        public Task<GitResult<TimelineEntry>> ToggleCommentReactionAsync(
            string token, HostRepository repository, long commentId, ReactionKind reaction, string login, CancellationToken cancellationToken = default) =>
            Write(() =>
            {
                Reactions.Add((commentId, reaction, login));
                ThumbsUp = ThumbsUp == 0 ? 1 : 0;
                return new TimelineEntry(TimelineKind.Comment, "octocat", "Mine", Now, Id: commentId,
                    Reactions: ThumbsUp > 0 ? [new ReactionCount(reaction, ThumbsUp)] : []);
            });

        public HostRepository? ParseRepository(RemoteUrl url) =>
            url.Host == "github.com" && url.DisplayPath.Split('/') is [var owner, var name]
                ? new HostRepository(HostingProvider.GitHub, "github.com", owner, name)
                : null;

        public Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IReadOnlyList<PullRequest>>>(new Success<IReadOnlyList<PullRequest>>([.. PullRequests]));

        public Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IReadOnlyList<Issue>>>(new Success<IReadOnlyList<Issue>>([]));

        public Task<GitResult<string>> GetDefaultBranchAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<string>>(new Success<string>("main"));

        public Task<GitResult<PullRequest>> CreatePullRequestAsync(string token, HostRepository repository, NewPullRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitResult<PullRequestDetail>> GetPullRequestDetailAsync(
            string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<PullRequestDetail>>(DetailFailure is null
                ? new Success<PullRequestDetail>(DetailOf(pullRequest with { HeadRepository = HeadRepository ?? pullRequest.HeadRepository }, State, Draft, Behind, AutoMerge, ThumbsUp, [.. Threads], Body, Comment))
                : new Failure(DetailFailure));

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
