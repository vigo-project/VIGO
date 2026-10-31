using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Pull request screen (J6.9a, read only), shown in place of the graph: header (title, state, branches, labels, totals),
/// then the conversation (description, comments, reviews, comments on the diff), the commits and the changed files with
/// their diff in the code editor. Opened from the Pull requests section of the left panel; GitHub first.
/// </summary>
public sealed partial class PullRequestViewModel : HostedDiscussionViewModel, IDisposable
{
    private readonly IUrlLauncher? _launcher;
    private readonly Preferences? _preferences;
    private readonly ChecksPoller _checksPoller;
    private CancellationTokenSource? _loading;

    public PullRequestViewModel(
        HostAccounts? accounts, HostingPanelViewModel hosting, IUrlLauncher? launcher, IUserNotifier? notifier, TimeProvider time,
        CancellationToken lifetime, Func<string?>? profileId = null, IConfirmationService? confirmations = null, Preferences? preferences = null)
        : base(accounts, hosting, notifier, time, profileId, confirmations, lifetime)
    {
        _preferences = preferences;
        _checksPoller = new ChecksPoller(time, ChecksRefreshInterval, lifetime);
        Layout = preferences?.Current.DiffLayout ?? DiffLayout.Unified;
        _launcher = launcher;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why nothing is shown (read error); null otherwise.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>The pull request as listed; null while closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(NumberText), nameof(BranchText), nameof(Author))]
    public partial PullRequest? Pull { get; private set; }

    public string Title => Pull?.Title ?? string.Empty;

    public string NumberText => Pull is { } p ? $"#{p.Number}" : string.Empty;

    public string Author => Pull?.Author ?? string.Empty;

    /// <summary>"feature/x → main".</summary>
    public string BranchText => Pull is { } p ? $"{p.HeadBranch} → {p.BaseBranch}" : string.Empty;

    /// <summary>What was read; null while loading or after an error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(State), nameof(TotalsText), nameof(IsTruncated))]
    [NotifyPropertyChangedFor(nameof(CanComment), nameof(CanClose), nameof(CanReopen), nameof(CanMarkReady), nameof(CanMakeDraft), nameof(CanMerge), nameof(CanUpdateBranch))]
    [NotifyPropertyChangedFor(nameof(CanEnableAutoMerge), nameof(CanDisableAutoMerge), nameof(AutoMergeText), nameof(CanDeleteSourceBranch))]
    [NotifyPropertyChangedFor(nameof(CanReview), nameof(CanSubmitReview), nameof(CanVote), nameof(CanCastVote), nameof(VoteToolTip), nameof(ShowReviewPanel))]
    public partial PullRequestDetail? Detail { get; private set; }

    /// <summary>The vote button works: Azure DevOps refuses votes on a draft, which must be marked ready first.</summary>
    public bool CanCastVote => IsIdle && Detail is { Summary.IsDraft: false };

    public string VoteToolTip => Detail is { Summary.IsDraft: true }
        ? Loc.Get("Pr.Vote.DraftTooltip")
        : Loc.Get("Pr.Vote.Tooltip");

    /// <summary>The service has votes and the pull request is open: the screen offers to vote (J6.9h).</summary>
    public bool CanVote => Detail is not null && Accounts is not null && Hosting.Repository is { } r
        && Accounts.SupportsPullRequestVote(r.Provider) && State == PullRequestState.Open;

    /// <summary>The "Revue" panel under the diff: to write a review, or at least to read the discussions of the file.</summary>
    public bool ShowReviewPanel => CanSubmitReview;

    /// <summary>The service lets the screen reply to threads on the diff, resolve them and send a review (J6.9e).</summary>
    public bool CanReview => Detail is not null && Accounts is not null && Hosting.Repository is { } r && Accounts.SupportsPullRequestReview(r.Provider);

    /// <summary>A review can be sent: the pull request is open (GitHub refuses a review of the author's own pull request).</summary>
    public bool CanSubmitReview => CanReview && State == PullRequestState.Open;

    /// <summary>The hosting service lets the screen write (comment, close, merge…) and the pull request is read.</summary>
    private bool CanWrite => Detail is not null && Accounts is not null && Hosting.Repository is { } r && Accounts.SupportsPullRequestWrite(r.Provider);

    /// <summary>The service lets the screen comment on the pull request (Azure DevOps does, without the other writes: J6.9j).</summary>
    public override bool CanComment => Detail is not null && Accounts is not null && Hosting.Repository is { } r && Accounts.SupportsPullRequestComment(r.Provider);

    /// <summary>The service lets the screen close, reopen and change the draft state.</summary>
    private bool CanChangeState => Detail is not null && Accounts is not null && Hosting.Repository is { } r && Accounts.SupportsPullRequestState(r.Provider);

    public bool CanClose => CanChangeState && State == PullRequestState.Open;

    public bool CanReopen => CanChangeState && State == PullRequestState.Closed;

    // The client refuses on its own when the service lacks what it needs (GitHub's node id), so the buttons only follow the state.
    public bool CanMarkReady => CanChangeState && State == PullRequestState.Open && Detail is { Summary.IsDraft: true };

    public bool CanMakeDraft => CanChangeState && State == PullRequestState.Open && Detail is { Summary.IsDraft: false };

    public bool CanMerge => Detail is not null && Accounts is not null && Hosting.Repository is { } r && Accounts.SupportsPullRequestMerge(r.Provider)
        && State == PullRequestState.Open && Detail is { Summary.IsDraft: false };

    /// <summary>The head branch is behind its base: the service can merge the base into it.</summary>
    public bool CanUpdateBranch => CanWrite && State == PullRequestState.Open && Detail is { IsBehind: true };

    /// <summary>The service can merge the pull request by itself once the checks and reviews allow it (J6.9d).</summary>
    public bool CanEnableAutoMerge => CanWrite && State == PullRequestState.Open && Detail is { Summary.IsDraft: false, AutoMerge: null, NodeId.Length: > 0 };

    public bool CanDisableAutoMerge => CanWrite && State == PullRequestState.Open && Detail is { AutoMerge: not null, NodeId.Length: > 0 };

    /// <summary>"Fusion automatique : squash"; empty when it is not enabled.</summary>
    public string AutoMergeText => Detail?.AutoMerge switch
    {
        MergeMethod.Squash => Loc.Get("Pr.AutoMerge.Squash"),
        MergeMethod.Rebase => Loc.Get("Pr.AutoMerge.Rebase"),
        MergeMethod.Merge => Loc.Get("Pr.AutoMerge.Merge"),
        _ => string.Empty,
    };

    /// <summary>The source branch of the closed or merged pull request is in this repository and still on the service (J6.15).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSourceBranch))]
    public partial bool SourceBranchExists { get; private set; }

    public bool CanDeleteSourceBranch => SourceBranchExists && IsIdle && State != PullRequestState.Open;

    /// <summary>The running read of <see cref="SourceBranchExists"/> (tests await it).</summary>
    internal Task SourceBranchCheck { get; private set; } = Task.CompletedTask;

    // Asks the service whether the branch is still there; a fork's branch is not ours to delete.
    private async Task CheckSourceBranchAsync(PullRequestDetail detail)
    {
        if (detail.State == PullRequestState.Open || Accounts is null || Repository is not { } repository
            || !Accounts.SupportsBranchDelete(repository.Provider)
            || !string.Equals(detail.Summary.HeadRepository, repository.FullName, StringComparison.OrdinalIgnoreCase)) return;
        var result = await Accounts.BranchExistsAsync(repository, detail.Summary.HeadBranch, ProfileId?.Invoke(), Lifetime);
        if (Pull?.Number == detail.Summary.Number) SourceBranchExists = result.Value is Success<bool> { Value: true };
    }

    /// <summary>
    /// Deletes the source branch on the service after a confirmation that also offers the local branch of the same name (checked when all its
    /// commits are on another branch), then fetches the remote with pruning so that <c>origin/&lt;branch&gt;</c> leaves the graph.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSourceBranchAsync()
    {
        if (!CanDeleteSourceBranch || Pull is not { } pull || Repository is not { } repository || Accounts is null || Confirmations is null) return;
        var branch = pull.HeadBranch;
        var remote = Hosting.RemoteName;
        var local = remote is null ? null : await Hosting.RefActions.FindLocalBranchAsync(branch, remote);
        var answer = await Confirmations.ConfirmAsync(new ConfirmationRequest(
            Loc.Format("Pr.DeleteBranch.Title", branch), Loc.Format("Pr.DeleteBranch.Message", branch), Loc.Get("Pr.DeleteBranch.Confirm"),
            OptionText: local is null ? null : Loc.Format("Pr.DeleteBranch.AlsoLocal", branch), OptionChecked: local is { Merged: true }, IsDestructive: true), Lifetime);
        if (!answer.Confirmed) return;

        var done = await WriteAsync(Loc.Get("Pr.Failure.BranchNotDeleted"), ct => Accounts.DeleteBranchAsync(repository, branch, ProfileId?.Invoke(), ct));
        if (done is null) return;
        SourceBranchExists = false;
        if (remote is not null) await Hosting.RefActions.CleanUpAfterRemoteDeleteAsync(answer.OptionChecked ? local?.Branch : null, remote);
    }

    public PullRequestState State => Detail?.State ?? PullRequestState.Open;

    /// <summary>"Ouverte", "Brouillon", "Fusionnée", "Fermée".</summary>
    public string StateText => Detail is not { } d ? string.Empty : d.State switch
    {
        PullRequestState.Merged => Loc.Get("Pr.State.Merged"),
        PullRequestState.Closed => Loc.Get("Pr.State.Closed"),
        _ => Loc.Get(d.Summary.IsDraft ? "Pr.State.Draft" : "Pr.State.Open"),
    };

    /// <summary>Labels of the pull request.</summary>
    public ObservableCollection<PullRequestLabelRow> Labels { get; } = [];

    public bool HasLabels => Labels.Count > 0;

    /// <summary>"3 commits · 7 fichiers · +120 −35".</summary>
    public string TotalsText => Detail is not { } d
        ? string.Empty
        : Loc.Format("Pr.Totals", CommitsText(d.Commits.Count), FilesText(d.Files.Count), Files.Sum(f => f.Additions), Files.Sum(f => f.Deletions));

    /// <summary>A list was cut at the page limit: the rest is on the hosting service.</summary>
    public bool IsTruncated => Detail?.IsTruncated == true;

    /// <summary>Description of the pull request.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial IReadOnlyList<MarkdownBlock> Description { get; private set; } = [];

    public bool HasDescription => Description.Count > 0;

    public ObservableCollection<PullRequestCommitRow> Commits { get; } = [];

    public ObservableCollection<PullRequestFileRow> Files { get; } = [];

    /// <summary>Rows of the files list: the files, or folders and files in tree mode.</summary>
    public ObservableCollection<FileListRow> FileRows { get; } = [];

    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    /// <summary>The files are shown as a tree of folders rather than as paths (shared layout setting).</summary>
    public bool ShowAsTree
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            ArrangeFiles();
        }
    }

    /// <summary>Collapses or expands a folder of the tree.</summary>
    [RelayCommand]
    private void ToggleFolder(FolderRow folder)
    {
        if (!_collapsed.Remove(folder.Path)) _collapsed.Add(folder.Path);
        ArrangeFiles();
    }

    /// <summary>Collapses every folder of the tree.</summary>
    [RelayCommand]
    private void CollapseAllFolders()
    {
        _collapsed.UnionWith(FileListLayout.FolderPaths(Files));
        ArrangeFiles();
    }

    /// <summary>Expands every folder of the tree.</summary>
    [RelayCommand]
    private void ExpandAllFolders()
    {
        _collapsed.Clear();
        ArrangeFiles();
    }

    private void ArrangeFiles() => FileListLayout.Sync(FileRows, FileListLayout.Arrange(Files, ShowAsTree, _collapsed));

    /// <summary>Thread the editor must scroll to once it shows it (set by <see cref="OpenThread"/>); null when none was asked.</summary>
    public string? RevealThreadId { get; private set; }

    /// <summary>Changes at each request to reveal a thread, so that the editor does it once per request.</summary>
    public int RevealToken { get; private set; }

    /// <summary>Shows the file of a preview and asks the editor to scroll to its thread.</summary>
    [RelayCommand]
    private void OpenThread(FileCommentPreview preview)
    {
        var row = Files.FirstOrDefault(f => f.Path == preview.Thread.Path);
        if (row is null) return;
        RevealThreadId = preview.ThreadId;
        RevealToken++;
        if (SelectedFile == row) BumpReviewRevision();
        else SelectedFile = row;
    }

    /// <summary>How long the screen waits before it reads the checks again while one is still running (J6.12).</summary>
    public static readonly TimeSpan ChecksRefreshInterval = TimeSpan.FromSeconds(20);

    /// <summary>Checks of the head commit: shown at once from the left panel's read, then read by the screen itself (empty when unknown).</summary>
    public ObservableCollection<PullRequestCheckRow> Checks { get; } = [];

    public bool HasChecks => Checks.Count > 0;

    /// <summary>"4 réussis · 1 en cours · 1 en échec"; empty without checks.</summary>
    [ObservableProperty]
    public partial string ChecksSummary { get; private set; } = string.Empty;

    /// <summary>Worst state of the checks (header icon).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChecksFailed), nameof(ChecksRunning), nameof(ChecksPassed), nameof(ChecksNeutral))]
    public partial CheckState ChecksState { get; private set; }

    public bool ChecksFailed => ChecksState == CheckState.Failure;

    public bool ChecksRunning => ChecksState == CheckState.Pending;

    public bool ChecksPassed => ChecksState == CheckState.Success;

    public bool ChecksNeutral => !ChecksFailed && !ChecksRunning && !ChecksPassed;

    /// <summary>A required check failed: the merge is blocked until it passes.</summary>
    [ObservableProperty]
    public partial bool HasRequiredFailure { get; private set; }

    /// <summary>The checks are being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshChecks))]
    public partial bool IsRefreshingChecks { get; private set; }

    public bool CanRefreshChecks => !IsRefreshingChecks;

    /// <summary>The running read-and-wait loop of the checks (tests await it); completed when nothing is running any more.</summary>
    internal Task ChecksWatching => _checksPoller.Running;

    // Reads the checks now, then again every ChecksRefreshInterval while one is running; stops with the screen.
    private void StartChecksWatch() => _checksPoller.Start(ReadChecksAsync);

    /// <returns>Whether to read again later: a check is still running. A failed read keeps the list shown.</returns>
    private async Task<bool> ReadChecksAsync(CancellationToken cancellationToken)
    {
        if (Accounts is null || Hosting.Repository is not { } repository || Pull is not { } pull) return false;

        IsRefreshingChecks = true;
        try
        {
            var result = await Accounts.GetPullRequestStatusAsync(repository, pull, ProfileId?.Invoke(), includeRequired: true, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return false;
            switch (result)
            {
                case Success<PullRequestStatus> { Value: var status }:
                    ShowChecks(status);
                    return Checks.Any(c => c.IsPending);
                case AuthenticationFailed a:
                    Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Pr.Checks.ReadFailed"), a.Message);
                    return false;
                default:
                    return Checks.Any(c => c.IsPending);
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsRefreshingChecks = false;
        }
    }

    private void ShowChecks(PullRequestStatus status)
    {
        var runs = status.Runs
            .OrderBy(PullRequestCheckRow.Rank)
            .ThenByDescending(r => r.IsRequired == true)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Checks.Clear();
        foreach (var run in runs) Checks.Add(new PullRequestCheckRow(run, OpenCheckUrlAsync));
        OnPropertyChanged(nameof(HasChecks));

        ChecksState = PullRequestStatus.Summarize(runs);
        HasRequiredFailure = runs.Any(r => r.IsRequired == true && r.State is CheckState.Failure or CheckState.Canceled);
        ChecksSummary = SummarizeChecks(runs);
    }

    internal static string SummarizeChecks(IReadOnlyList<CheckRun> runs)
    {
        var passed = runs.Count(r => r.State == CheckState.Success);
        var pending = runs.Count(r => r.State == CheckState.Pending);
        var failed = runs.Count(r => r.State is CheckState.Failure or CheckState.Canceled);
        var skipped = runs.Count(r => r.State == CheckState.Skipped);

        var parts = new List<string>();
        if (passed > 0) parts.Add(Loc.Format(passed == 1 ? "Pr.Checks.Summary.Passed.One" : "Pr.Checks.Summary.Passed.Other", passed));
        if (pending > 0) parts.Add(Loc.Format("Pr.Checks.Summary.Pending", pending));
        if (failed > 0) parts.Add(Loc.Format("Pr.Checks.Summary.Failed", failed));
        if (skipped > 0) parts.Add(Loc.Format(skipped == 1 ? "Pr.Checks.Summary.Skipped.One" : "Pr.Checks.Summary.Skipped.Other", skipped));
        return string.Join(" · ", parts);
    }

    private async Task OpenCheckUrlAsync(string url)
    {
        if (_launcher is null) return;
        if (!await _launcher.OpenAsync(url)) Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Issue.Failure.BrowserFailed"), url);
    }

    [RelayCommand]
    private Task RefreshChecksAsync()
    {
        StartChecksWatch();
        return Task.CompletedTask;
    }

    /// <summary>Discussion threads on the diff of <see cref="SelectedFile"/> (J6.9e).</summary>
    public ObservableCollection<ThreadRow> FileThreads { get; } = [];

    public bool HasFileThreads => FileThreads.Count > 0;

    /// <summary>Raised (as a property change) whenever the threads of the file or the comments kept for the review change: the editor draws them again.</summary>
    public int ReviewRevision { get; private set; }

    /// <summary>The service proposes issues, pull requests and people while a comment is typed.</summary>
    public bool CanCompleteReferences => Accounts is not null && Repository is { } r && Accounts.SupportsReferenceSearch(r.Provider);

    /// <summary>Proposals for what follows a <c>#</c>, <c>!</c> or <c>@</c> typed in a comment; empty when the service has none or fails.</summary>
    public async Task<IReadOnlyList<ReferenceSuggestion>> SearchReferencesAsync(ReferenceKind kind, string query, CancellationToken cancellationToken)
    {
        if (!CanCompleteReferences || Accounts is null || Repository is not { } repository) return [];
        var result = await Accounts.SearchReferencesAsync(repository, kind, query, ProfileId?.Invoke(), cancellationToken);
        return result.Value is Success<IReadOnlyList<ReferenceSuggestion>> { Value: var items } ? items : [];
    }

    // Azure DevOps stores a mention as @<identity id>: the names are read once and kept (an empty name: not found, not asked again).
    private readonly Dictionary<string, string> _mentionNames = new(StringComparer.OrdinalIgnoreCase);

    // The rows of the threads read the names again each time the cards are drawn (ReviewRevision).
    protected override IReadOnlyDictionary<string, string> MentionNames => _mentionNames;

    [System.Text.RegularExpressions.GeneratedRegex("@<([0-9a-fA-F-]{36})>")]
    private static partial System.Text.RegularExpressions.Regex MentionPattern();

    // Identifiers being looked up: a second lookup started meanwhile (threads of the first file, conversation) skips them.
    private readonly HashSet<string> _mentionsAsked = new(StringComparer.OrdinalIgnoreCase);

    private async Task ResolveMentionsAsync()
    {
        if (!CanCompleteReferences || Accounts is null || Repository is not { } repository) return;
        var ids = FileThreads.SelectMany(t => t.Comments).Select(c => c.Comment.Body).Concat(Drafts.Select(d => d.Comment.Body))
            .Concat(Timeline.Select(t => t.Entry.Body)).Append(Detail?.Body ?? string.Empty)
            .SelectMany(body => MentionPattern().Matches(body).Select(m => m.Groups[1].Value))
            .Where(id => !_mentionNames.ContainsKey(id) && !_mentionsAsked.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0) return;

        _mentionsAsked.UnionWith(ids);
        var result = await Accounts.ResolveMentionsAsync(repository, ids, ProfileId?.Invoke(), Lifetime);
        var names = result.Value is Success<IReadOnlyDictionary<string, string>> success ? success.Value : null;
        // Names recorded before the ids leave the asked set: a call in between would otherwise ask them again.
        if (names is not null) foreach (var id in ids) _mentionNames[id] = names.TryGetValue(id, out var name) ? name : string.Empty;
        _mentionsAsked.ExceptWith(ids);
        if (names is null) return;
        BumpReviewRevision();
        // The conversation shows the names too: its texts that mention someone are read again.
        if (Detail is { } detail && MentionPattern().IsMatch(detail.Body)) Description = MarkdownParser.Parse(detail.Body, References);
        foreach (var row in Timeline.Where(r => MentionPattern().IsMatch(r.Entry.Body))) row.ParseAgain();
    }

    private void BumpReviewRevision()
    {
        ReviewRevision++;
        OnPropertyChanged(nameof(ReviewRevision));
    }

    /// <summary>The service has reviews that gather comments before they are published (GitHub); Azure DevOps publishes each comment at once.</summary>
    public bool CanDraftReviewComments => Repository?.Provider == HostingProvider.GitHub;

    /// <summary>Comments on lines waiting for the review to be sent (kept on the screen only, until it is sent).</summary>
    public ObservableCollection<ReviewDraftRow> Drafts { get; } = [];

    public bool HasDrafts => Drafts.Count > 0;

    /// <summary>"3 commentaires en attente"; empty without any.</summary>
    public string DraftsText => Drafts.Count switch
    {
        0 => string.Empty,
        1 => Loc.Get("Pr.Drafts.One"),
        var n => Loc.Format("Pr.Drafts.Many", n),
    };

    /// <summary>Line (of the diff of the selected file) the new comment is about.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDraftCommand))]
    public partial double DraftLine { get; set; }

    /// <summary>The new comment is about the old version of the line (a deleted line) rather than the new one.</summary>
    [ObservableProperty]
    public partial bool DraftOnOldSide { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDraftCommand))]
    public partial string DraftText { get; set; } = string.Empty;

    /// <summary>Text of the review (its summary), sent with the comments on lines.</summary>
    [ObservableProperty]
    public partial string ReviewText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConversation), nameof(IsCommits), nameof(IsFiles))]
    public partial PullRequestTab SelectedTab { get; set; }

    public bool IsConversation => SelectedTab == PullRequestTab.Conversation;

    public bool IsCommits => SelectedTab == PullRequestTab.Commits;

    public bool IsFiles => SelectedTab == PullRequestTab.Files;

    /// <summary>File whose diff is shown in the Files tab.</summary>
    [ObservableProperty]
    public partial PullRequestFileRow? SelectedFile { get; set; }

    /// <summary>Diff of <see cref="SelectedFile"/> (the patch of the hosting service); null when there is none to show.</summary>
    [ObservableProperty]
    public partial FileDiff? Diff { get; private set; }

    /// <summary>Unified or side by side; remembered in the preferences, shared with the diff view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSideBySide), nameof(LayoutToggleLabel))]
    public partial DiffLayout Layout { get; private set; }

    public bool IsSideBySide => Layout == DiffLayout.SideBySide;

    public string LayoutToggleLabel => Loc.Get(IsSideBySide ? "Diff.Layout.Unified" : "Diff.Layout.SideBySide");

    /// <summary>
    /// The two sides of <see cref="Diff"/> in the side-by-side layout: only what the hunks show (the service gives no whole files),
    /// at the real line numbers. Null in the unified layout or without a diff.
    /// </summary>
    [ObservableProperty]
    public partial DiffSides? Sides { get; private set; }

    partial void OnDiffChanged(FileDiff? value)
    {
        UpdateSides();
        AddDraftCommand.NotifyCanExecuteChanged();
    }

    protected override bool CanModifyComments => CanWrite;

    protected override void OnBusyChanged()
    {
        OnPropertyChanged(nameof(CanCastVote));
        OnPropertyChanged(nameof(CanDeleteSourceBranch));
    }

    protected override Task<GitResult<TimelineEntry>> PostCommentAsync(HostRepository repository, string body, CancellationToken cancellationToken) =>
        Pull is { } pull && Accounts is not null
            ? Accounts.AddPullRequestCommentAsync(repository, pull, body, ProfileId?.Invoke(), cancellationToken)
            : Task.FromResult<GitResult<TimelineEntry>>(Canceled.Instance);

    partial void OnDetailChanged(PullRequestDetail? value)
    {
        AddDraftCommand.NotifyCanExecuteChanged();
        AddCommentCommand.NotifyCanExecuteChanged();
    }

    partial void OnLayoutChanged(DiffLayout value) => UpdateSides();

    // Whole versions already read, by path (the hunks stand in until they arrive, or when they cannot be read).
    private readonly Dictionary<string, DiffSides> _fullSides = [];
    private CancellationTokenSource? _sidesLoading;

    private void UpdateSides()
    {
        _sidesLoading?.Cancel();
        if (!IsSideBySide || Diff is not { } diff || SelectedFile is not { } row)
        {
            Sides = null;
            return;
        }
        if (_fullSides.TryGetValue(row.Path, out var full))
        {
            Sides = full;
            return;
        }
        Sides = DiffSides.FromHunks(diff);
        _ = LoadFullSidesAsync(row);
    }

    private async Task LoadFullSidesAsync(PullRequestFileRow row)
    {
        if (Accounts is null || Repository is not { } repository || Pull is not { } pull) return;
        var source = _sidesLoading = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        var result = await Accounts.GetPullRequestFileSidesAsync(repository, pull, row.File, ProfileId?.Invoke(), source.Token);
        if (source.IsCancellationRequested || result.Value is not Success<DiffSides> { Value: var full }) return;
        _fullSides[row.Path] = full;
        if (SelectedFile == row && IsSideBySide) Sides = full;
    }

    [RelayCommand]
    private async Task ToggleLayoutAsync()
    {
        var layout = Layout = IsSideBySide ? DiffLayout.Unified : DiffLayout.SideBySide;
        if (_preferences is not null) await _preferences.UpdateAsync(p => p with { DiffLayout = layout });
    }

    /// <summary>Why no diff is shown for the selected file (binary, too big, unreadable); null otherwise.</summary>
    [ObservableProperty]
    public partial string? DiffMessage { get; private set; }

    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _checksPoller.Dispose();
    }

    /// <summary>Shows <paramref name="pull"/>: the screen opens at once, its content follows.</summary>
    public async Task OpenAsync(PullRequest pull)
    {
        if (Accounts is null || Hosting.Repository is not { } repository) return;

        _loading?.Cancel();
        _loading?.Dispose();
        var source = _loading = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        Reset();
        // A comment kept for a review stays across a refresh of the same pull request, not on another one.
        if (!IsOpen || Pull?.Number != pull.Number)
        {
            Drafts.Clear();
            OnDraftsChanged();
        }
        Pull = pull;
        IsOpen = true;
        IsLoading = true;
        try
        {
            var result = await Accounts.GetPullRequestDetailAsync(repository, pull, ProfileId?.Invoke(), source.Token);
            if (source.IsCancellationRequested) return;
            switch (result)
            {
                case Success<PullRequestDetail> { Value: var detail }:
                    Apply(detail);
                    break;
                case AuthenticationFailed a:
                    Message = a.Message;
                    break;
                case Failure f:
                    Message = Loc.Format("Pr.ReadFailed", f.Message);
                    break;
                case Canceled or Conflict:
                    break;
            }
        }
        finally
        {
            if (_loading == source) IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => Pull is { } pull ? OpenAsync(pull) : Task.CompletedTask;

    [RelayCommand]
    private Task ClosePullRequestAsync() => ChangeStateAsync(Loc.Get("Pr.Failure.NotClosed"), (a, r, d, p, ct) => a.SetPullRequestOpenAsync(r, d.Summary, false, p, ct));

    [RelayCommand]
    private Task ReopenPullRequestAsync() => ChangeStateAsync(Loc.Get("Pr.Failure.NotReopened"), (a, r, d, p, ct) => a.SetPullRequestOpenAsync(r, d.Summary, true, p, ct));

    [RelayCommand]
    private Task MarkReadyAsync() => ChangeStateAsync(Loc.Get("Pr.Failure.NotMarkedReady"), (a, r, d, p, ct) => a.SetPullRequestDraftAsync(r, d, false, p, ct));

    [RelayCommand]
    private Task MakeDraftAsync() => ChangeStateAsync(Loc.Get("Pr.Failure.NotMadeDraft"), (a, r, d, p, ct) => a.SetPullRequestDraftAsync(r, d, true, p, ct));

    [RelayCommand]
    private Task UpdateBranchAsync() =>
        ChangeStateAsync(Loc.Get("Pr.Failure.BranchNotUpdated"), (a, r, d, p, ct) => a.UpdatePullRequestBranchAsync(r, d.Summary, p, ct));

    /// <summary>Merges the pull request after a confirmation (the default button of the dialog cancels).</summary>
    public async Task MergeAsync(MergeMethod method)
    {
        if (!CanMerge || Pull is not { } pull) return;
        var how = method switch
        {
            MergeMethod.Squash => Loc.Get("Pr.Merge.How.Squash"),
            MergeMethod.Rebase => Loc.Get("Pr.Merge.How.Rebase"),
            _ => Loc.Get("Pr.Merge.How.Merge"),
        };
        if (Confirmations is null || !await Confirmations.ConfirmAsync(
                Loc.Format("Pr.Merge.Title", NumberText), Loc.Format("Pr.Merge.Message", pull.Title, how, pull.BaseBranch), Loc.Get("Pr.Merge.Confirm"), Lifetime)) return;
        await ChangeStateAsync(Loc.Get("Pr.Failure.NotMerged"), (a, r, d, p, ct) => a.MergePullRequestAsync(r, d.Summary, method, p, ct));
    }

    /// <summary>Enables the automatic merge with <paramref name="method"/>: the service merges once the checks and reviews pass.</summary>
    public async Task EnableAutoMergeAsync(MergeMethod method)
    {
        if (!CanEnableAutoMerge) return;
        await ChangeStateAsync(Loc.Get("Pr.Failure.AutoMergeNotEnabled"), (a, r, d, p, ct) => a.SetPullRequestAutoMergeAsync(r, d, method, p, ct));
    }

    [RelayCommand]
    private Task DisableAutoMergeAsync() =>
        CanDisableAutoMerge
            ? ChangeStateAsync(Loc.Get("Pr.Failure.AutoMergeNotDisabled"), (a, r, d, p, ct) => a.SetPullRequestAutoMergeAsync(r, d, null, p, ct))
            : Task.CompletedTask;

    private bool CanAddDraft() => CanReview && !string.IsNullOrWhiteSpace(DraftText) && Diff is not null;

    /// <summary>Keeps a comment on a line of the diff until the review is sent.</summary>
    [RelayCommand(CanExecute = nameof(CanAddDraft))]
    private void AddDraft()
    {
        if (SelectedFile is not { } file || Diff is not { } diff) return;
        var side = DraftOnOldSide ? ReviewSide.Old : ReviewSide.New;
        var line = (int)Math.Round(DraftLine);
        // The service refuses a comment on a line the diff does not show.
        if (!DiffShowsLine(diff, side, line))
        {
            Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Pr.LineMissing.Title"), LineMissing(file.Path, line, side));
            return;
        }
        Drafts.Add(new ReviewDraftRow(new NewReviewComment(file.Path, line, side, DraftText.Trim())));
        DraftText = string.Empty;
        OnDraftsChanged();
    }

    [RelayCommand]
    private void RemoveDraft(ReviewDraftRow draft)
    {
        Drafts.Remove(draft);
        OnDraftsChanged();
    }

    private void OnDraftsChanged()
    {
        OnPropertyChanged(nameof(HasDrafts));
        OnPropertyChanged(nameof(DraftsText));
        BumpReviewRevision();
    }

    /// <summary>
    /// A comment written in the editor box (a line or a selection of the diff): published at once, or kept for the review
    /// (<paramref name="asDraft"/>, services with reviews only). False when it was not taken (the reason is shown).
    /// </summary>
    public async Task<bool> AddLineCommentAsync(NewReviewComment comment, bool asDraft)
    {
        if (!CanSubmitReview || Pull is not { } pull || Repository is not { } repository || Accounts is null || string.IsNullOrWhiteSpace(comment.Body)) return false;
        // GitHub refuses a comment on a line the diff does not show; Azure DevOps anchors it on any line of the file.
        if (repository.Provider == HostingProvider.GitHub && Diff is { } diff && !DiffShowsLine(diff, comment.Side, comment.Line))
        {
            Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Pr.LineMissing.Title"), LineMissing(comment.Path, comment.Line, comment.Side));
            return false;
        }
        var trimmed = comment with { Body = comment.Body.Trim() };
        if (asDraft && CanDraftReviewComments)
        {
            Drafts.Add(new ReviewDraftRow(trimmed));
            OnDraftsChanged();
            return true;
        }
        var done = await WriteAsync(Loc.Get("Issue.Failure.CommentNotPosted"),
            ct => Accounts.SubmitReviewAsync(repository, pull, ReviewDecision.Comment, string.Empty, [trimmed], ProfileId?.Invoke(), ct));
        if (done is null) return false;
        await RefreshThreadsAsync();
        return true;
    }

    /// <summary>Removes the comment kept for the review at <paramref name="index"/>.</summary>
    public void RemoveDraftAt(int index)
    {
        if (index < 0 || index >= Drafts.Count) return;
        Drafts.RemoveAt(index);
        OnDraftsChanged();
    }

    // Reads the discussions again after a comment was published: only the threads are swapped, the diff on screen stays.
    private async Task RefreshThreadsAsync()
    {
        if (Pull is not { } pull || Repository is not { } repository || Accounts is null || Detail is not { } detail) return;
        var result = await Accounts.GetPullRequestDetailAsync(repository, pull, ProfileId?.Invoke(), Lifetime);
        switch (result.Value)
        {
            case Success<PullRequestDetail> { Value: var fresh }:
                Detail = detail with { Threads = [.. fresh.ReviewThreads] };
                RebuildFileThreads();
                break;
            case AuthenticationFailed a:
                Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Pr.Failure.ThreadsNotRefreshed"), a.Message);
                break;
            case Failure f:
                Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Pr.Failure.ThreadsNotRefreshed"), f.Message);
                break;
            case Canceled or Conflict:
                break;
        }
    }

    internal static bool DiffShowsLine(FileDiff diff, ReviewSide side, int line) =>
        diff.Hunks.Any(h => h.Lines.Any(l => l.Kind != DiffLineKind.NoNewline && (side == ReviewSide.Old ? l.OldNumber : l.NewNumber) == line));

    /// <summary>Sends the review: its decision, its text and the comments on lines kept so far, then reads the pull request again.</summary>
    public async Task SubmitReviewAsync(ReviewDecision decision)
    {
        if (!CanSubmitReview || Pull is not { } pull || Repository is not { } repository || Accounts is null) return;
        var text = ReviewText.Trim();
        if (decision == ReviewDecision.RequestChanges && text.Length == 0)
        {
            Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Pr.Failure.ReviewNotSent"), Loc.Get("Pr.Review.NeedChangesText"));
            return;
        }
        if (decision == ReviewDecision.Comment && text.Length == 0 && Drafts.Count == 0)
        {
            Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Pr.Failure.ReviewNotSent"), Loc.Get("Pr.Review.NeedContent"));
            return;
        }
        var comments = Drafts.Select(d => d.Comment).ToList();
        var done = await WriteAsync(Loc.Get("Pr.Failure.ReviewNotSent"),
            ct => Accounts.SubmitReviewAsync(repository, pull, decision, text, comments, ProfileId?.Invoke(), ct));
        if (done is null) return;
        Drafts.Clear();
        OnDraftsChanged();
        ReviewText = string.Empty;
        Notifier?.Notify(NotificationSeverity.Success, Loc.Get("Pr.Review.Sent"));
        await ReloadKeepingViewAsync();
    }

    /// <summary>Records the signed-in account's vote, then reads the pull request again (the vote shows in the conversation).</summary>
    public async Task VoteAsync(ReviewVote vote)
    {
        if (!CanVote || !CanCastVote || Pull is not { } pull || Repository is not { } repository || Accounts is null) return;
        var done = await WriteAsync(Loc.Get("Pr.Failure.VoteNotSaved"),
            ct => Accounts.SetPullRequestVoteAsync(repository, pull, vote, ProfileId?.Invoke(), ct));
        if (done is null) return;
        Notifier?.Notify(NotificationSeverity.Success, Loc.Get(vote == ReviewVote.None ? "Pr.Vote.Removed" : "Pr.Vote.Saved"));
        await ReloadKeepingViewAsync();
    }

    /// <summary>Adds the reply typed in <paramref name="row"/> to its thread.</summary>
    [RelayCommand]
    private async Task ReplyAsync(ThreadRow row) => await ReplyToThreadAsync(row.Thread.Id, row.ReplyText);

    /// <summary>Adds a reply to the thread <paramref name="threadId"/>; false when it was not published (the reason is shown).</summary>
    public async Task<bool> ReplyToThreadAsync(string threadId, string text)
    {
        if (Pull is not { } pull || Repository is not { } repository || Accounts is null || string.IsNullOrWhiteSpace(text)) return false;
        if (Detail?.ReviewThreads.FirstOrDefault(t => t.Id == threadId) is not { Comments.Count: > 0 } thread || !CanReview) return false;
        var body = text.Trim();
        var reply = await WriteAsync(Loc.Get("Pr.Failure.ReplyNotPosted"),
            ct => Accounts.ReplyToReviewThreadAsync(repository, pull, thread, body, ProfileId?.Invoke(), ct));
        if (reply is null) return false;
        ReplaceThread(thread with { Comments = [.. thread.Comments, reply] });
        return true;
    }

    /// <summary>The comments of the review threads take reactions (GitHub).</summary>
    public bool CanReactInThreads => CanReview && Hosting.Repository is { } r && Accounts!.SupportsReviewCommentReactions(r.Provider);

    /// <summary>Adds the signed-in account's reaction to a comment of a review thread, or removes it (as in the conversation).</summary>
    public async Task ToggleThreadReactionAsync(long commentId, ReactionKind kind)
    {
        if (Repository is not { } repository || Accounts is null || !CanReactInThreads) return;
        if (Detail?.ReviewThreads.FirstOrDefault(t => t.Comments.Any(c => c.Id == commentId)) is not { } thread) return;
        var comment = await WriteAsync(Loc.Get("Issue.Failure.ReactionNotSaved"),
            ct => Accounts.ToggleReviewCommentReactionAsync(repository, commentId, kind, ProfileId?.Invoke(), ct));
        if (comment is null) return;
        // The service gives the comment back: its text and author are kept, only the reactions are taken.
        ReplaceThread(thread with { Comments = [.. thread.Comments.Select(c => c.Id == commentId ? c with { Reactions = comment.Reactions } : c)] });
    }

    /// <summary>Marks the thread resolved, or opens it again.</summary>
    [RelayCommand]
    private async Task ToggleResolvedAsync(ThreadRow row) => await SetThreadResolvedAsync(row.Thread.Id, !row.Thread.IsResolved);

    /// <summary>Marks the thread <paramref name="threadId"/> resolved (or open again); false when the service refused.</summary>
    public async Task<bool> SetThreadResolvedAsync(string threadId, bool resolved)
    {
        if (Pull is not { } pull || Repository is not { } repository || Accounts is null || !CanReview) return false;
        if (Detail?.ReviewThreads.FirstOrDefault(t => t.Id == threadId) is not { Comments.Count: > 0 } thread) return false;
        var done = await WriteAsync(Loc.Get(resolved ? "Pr.Failure.ThreadNotResolved" : "Pr.Failure.ThreadNotReopened"),
            ct => Accounts.SetReviewThreadResolvedAsync(repository, pull, thread, resolved, ProfileId?.Invoke(), ct));
        if (done is null) return false;
        ReplaceThread(thread with { IsResolved = resolved });
        return true;
    }

    // The screen keeps what the service now says without reading everything again: the thread is swapped in the detail.
    private void ReplaceThread(ReviewThread changed)
    {
        if (Detail is not { } detail) return;
        Detail = detail with { Threads = [.. detail.ReviewThreads.Select(t => t.Id == changed.Id ? changed : t)] };
        RebuildFileThreads();
    }

    private void RebuildFileThreads()
    {
        if (Detail is { } all)
        {
            foreach (var row in Files)
                row.SetThreads(all.ReviewThreads.Where(t => t.Path == row.Path));
        }

        FileThreads.Clear();
        if (Detail is { } detail && SelectedFile is { } file)
        {
            var now = Time.GetUtcNow();
            foreach (var thread in detail.ReviewThreads.Where(t => t.Path == file.Path))
                FileThreads.Add(new ThreadRow(thread,
                    [.. thread.Comments.Select(c => new ThreadCommentRow(c, RelativeTime.Format(c.CreatedAt, now, Time.LocalTimeZone), CanReactInThreads, References))], CanReview));
        }
        OnPropertyChanged(nameof(HasFileThreads));
        BumpReviewRevision();
        _ = ResolveMentionsAsync();
    }

    // After a write that changes the discussion, reads everything again but stays on the same tab and file.
    private async Task ReloadKeepingViewAsync()
    {
        if (Pull is not { } pull) return;
        var tab = SelectedTab;
        var path = SelectedFile?.Path;
        await OpenAsync(pull);
        if (Detail is null) return;
        SelectedFile = Files.FirstOrDefault(f => f.Path == path) ?? SelectedFile;
        SelectedTab = tab;
    }

    /// <summary>Changes the state of the pull request on the service, then reads it again (and the left panel's list).</summary>
    private async Task ChangeStateAsync(
        string failureTitle, Func<HostAccounts, HostRepository, PullRequestDetail, string?, CancellationToken, Task<GitResult<Done>>> action)
    {
        if (Pull is not { } pull || Detail is not { } detail || Repository is not { } repository || Accounts is null) return;
        var done = await WriteAsync(failureTitle, ct => action(Accounts, repository, detail, ProfileId?.Invoke(), ct));
        if (done is null) return;
        await OpenAsync(pull);
        await Hosting.RefreshAsync();
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        _loading?.Cancel();
        IsOpen = false;
        IsLoading = false;
        Reset();
        Drafts.Clear();
        OnDraftsChanged();
        DraftText = string.Empty;
        ReviewText = string.Empty;
        Pull = null;
    }

    /// <summary>Opens the pull request in the browser (to comment, review or merge there until the app can).</summary>
    [RelayCommand]
    private async Task OpenInBrowserAsync()
    {
        if (Pull is not { } pull || _launcher is null) return;
        if (!await _launcher.OpenAsync(pull.Url)) Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Issue.Failure.BrowserFailed"), pull.Url);
    }

    [RelayCommand]
    private void SelectTab(PullRequestTab tab) => SelectedTab = tab;

    partial void OnSelectedFileChanged(PullRequestFileRow? value)
    {
        ShowDiff(value);
        RebuildFileThreads();
    }

    private void Reset()
    {
        FileThreads.Clear();
        OnPropertyChanged(nameof(HasFileThreads));
        Message = null;
        Detail = null;
        CommentDraft = string.Empty;
        Description = [];
        Timeline.Clear();
        Commits.Clear();
        Files.Clear();
        FileRows.Clear();
        _collapsed.Clear();
        RevealThreadId = null;
        _checksPoller.Cancel();
        Checks.Clear();
        OnPropertyChanged(nameof(HasChecks));
        ChecksSummary = string.Empty;
        ChecksState = CheckState.None;
        HasRequiredFailure = false;
        IsRefreshingChecks = false;
        SourceBranchExists = false;
        Labels.Clear();
        OnPropertyChanged(nameof(HasLabels));
        _sidesLoading?.Cancel();
        _diffComputing?.Cancel();
        _statsComputing?.Cancel();
        _fullSides.Clear();
        SelectedFile = null;
        Diff = null;
        DiffMessage = null;
        SelectedTab = PullRequestTab.Conversation;
    }

    private void Apply(PullRequestDetail detail)
    {
        var now = Time.GetUtcNow();
        Detail = detail;
        Description = MarkdownParser.Parse(detail.Body, References);
        foreach (var label in detail.Labels) Labels.Add(new PullRequestLabelRow(label));
        OnPropertyChanged(nameof(HasLabels));
        foreach (var entry in detail.Timeline) Timeline.Add(new TimelineRow(entry, RelativeTime.Format(entry.CreatedAt, now, Time.LocalTimeZone), IsOwn(entry) && CanWrite, CanWrite, References));
        foreach (var commit in detail.Commits) Commits.Add(new PullRequestCommitRow(commit, RelativeTime.Format(commit.Date, now, Time.LocalTimeZone)));

        var comments = detail.Timeline.Where(t => t.Kind == TimelineKind.ReviewComment && t.Path is not null)
            .GroupBy(t => t.Path!).ToDictionary(g => g.Key, g => g.Count());
        foreach (var file in detail.Files) Files.Add(new PullRequestFileRow(file, comments.GetValueOrDefault(file.Path)));

        // What the left panel already read is shown at once; the screen then reads the checks itself (details, required, refresh).
        if (Hosting.Statuses.TryGetValue(detail.Summary.Number, out var status)) ShowChecks(status);
        StartChecksWatch();
        SourceBranchCheck = CheckSourceBranchAsync(detail);

        ArrangeFiles();
        SelectedFile = Files.FirstOrDefault();
        if (Accounts is not null && Hosting.Repository is { } hosted && Accounts.ComputesPullRequestDiffs(hosted.Provider)) _ = ComputeAllStatsAsync();
        _ = ResolveMentionsAsync();
    }

    private void ShowDiff(PullRequestFileRow? row)
    {
        _diffComputing?.Cancel();
        Diff = null;
        DiffMessage = null;
        if (row is null) return;

        // Some services (Azure DevOps) never give a patch: the diff is computed from the two versions of the file.
        if (row.File.Patch is null && Accounts is not null && Hosting.Repository is { } hosted && Accounts.ComputesPullRequestDiffs(hosted.Provider))
        {
            DiffMessage = Loc.Get("Pr.Diff.Loading");
            _ = ComputeDiffAsync(row);
            return;
        }

        // The service gives no patch for a binary file, an empty change (rename, mode) or a file too big to diff.
        if (row.File.Patch is not { Length: > 0 } patch)
        {
            DiffMessage = row.File.Kind == FileChangeKind.Renamed && row.File.Additions + row.File.Deletions == 0
                ? Loc.Get("Pr.Diff.RenamedNoChange")
                : Loc.Get("Pr.Diff.NoTextDiff");
            return;
        }
        try
        {
            Diff = UnifiedDiffParser.Parse(patch, row.File.Path, row.File.OldPath);
        }
        catch (FormatException)
        {
            DiffMessage = Loc.Get("Pr.Diff.Unreadable");
        }
    }

    private CancellationTokenSource? _diffComputing;

    private async Task ComputeDiffAsync(PullRequestFileRow row)
    {
        _diffComputing?.Cancel();
        var source = _diffComputing = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        if (Accounts is null || Repository is not { } repository || Pull is not { } pull) return;

        if (!_fullSides.TryGetValue(row.Path, out var sides))
        {
            var result = await Accounts.GetPullRequestFileSidesAsync(repository, pull, row.File, ProfileId?.Invoke(), source.Token);
            if (source.IsCancellationRequested || SelectedFile != row) return;
            switch (result.Value)
            {
                case Success<DiffSides> { Value: var read }:
                    sides = read;
                    break;
                case Failure f:
                    DiffMessage = f.Message;
                    return;
                case AuthenticationFailed a:
                    DiffMessage = a.Message;
                    return;
                default:
                    return;
            }
        }

        var compared = await Task.Run(() => TextDiff.Compare(sides.Original, sides.Modified), CancellationToken.None);
        if (source.IsCancellationRequested || SelectedFile != row) return;
        if (compared is null)
        {
            DiffMessage = Loc.Get("Pr.Diff.TooBig");
            return;
        }
        row.SetStats(compared.Additions, compared.Deletions);
        OnPropertyChanged(nameof(TotalsText));
        if (compared.Patch.Length == 0)
        {
            DiffMessage = row.File.Kind == FileChangeKind.Renamed
                ? Loc.Get("Pr.Diff.RenamedNoChange")
                : Loc.Get("Pr.Diff.NoContentChange");
            return;
        }

        try
        {
            var diff = UnifiedDiffParser.Parse(compared.Patch, row.File.Path, row.File.OldPath);
            // The versions are kept for the side-by-side layout, so that it does not read them a second time.
            _fullSides[row.Path] = sides;
            DiffMessage = null;
            Diff = diff;
        }
        catch (FormatException)
        {
            DiffMessage = Loc.Get("Pr.Diff.ComputedUnreadable");
        }
    }

    private CancellationTokenSource? _statsComputing;

    // Azure DevOps gives no counts: they are computed for every file in the background, one file at a time, so that the list
    // does not show "+0" until each file is opened. The versions are not kept (memory); a failure leaves the file at +0.
    private async Task ComputeAllStatsAsync()
    {
        _statsComputing?.Cancel();
        var source = _statsComputing = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        if (Accounts is null || Repository is not { } repository || Pull is not { } pull) return;

        foreach (var row in Files.ToList())
        {
            if (source.IsCancellationRequested) return;
            if (row.HasStats || row.File.Patch is not null) continue;
            var result = await Accounts.GetPullRequestFileSidesAsync(repository, pull, row.File, ProfileId?.Invoke(), source.Token);
            if (source.IsCancellationRequested) return;
            if (result.Value is not Success<DiffSides> { Value: var sides }) continue;
            var compared = await Task.Run(() => TextDiff.Compare(sides.Original, sides.Modified), CancellationToken.None);
            if (source.IsCancellationRequested) return;
            if (compared is null || row.HasStats) continue;
            row.SetStats(compared.Additions, compared.Deletions);
            OnPropertyChanged(nameof(TotalsText));
        }
    }

    private static string CommitsText(int count) => count == 1 ? Loc.Get("Common.Commits.One") : Loc.Format("Common.Commits.Many", count);

    private static string FilesText(int count) => count == 1 ? Loc.Get("Pr.Files.One") : Loc.Format("Pr.Files.Many", count);

    // The message of a refused line comment: GitHub accepts only the lines the diff shows.
    private static string LineMissing(string path, int line, ReviewSide side) =>
        Loc.Format("Pr.LineMissing.Message", path, line, Loc.Get(side == ReviewSide.Old ? "Pr.Side.Old" : "Pr.Side.New"));
}
