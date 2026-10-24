using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Tab of an open repository: graph (with the work-in-progress row), details of the selected commit or the working
/// directory panel, file diff. Staging comes in J3.2.
/// </summary>
public sealed partial class RepositoryTabViewModel : TabViewModel, IDisposable
{
    public RepositoryTabViewModel(
        RepositoryInfo info,
        IGitService git,
        TimeProvider time,
        ILogger logger,
        IRepositoryWatcherFactory? watchers = null,
        IUserNotifier? notifier = null,
        IConfirmationService? confirmations = null,
        IAuthenticationPrompt? authentication = null,
        IInputDialogService? inputs = null,
        Preferences? preferences = null,
        HostAccounts? accounts = null,
        IUrlLauncher? launcher = null,
        Func<string, Task>? openRepository = null,
        GitProfiles? profiles = null,
        CommitMessageGenerator? commitMessages = null,
        TerminalService? terminal = null,
        IFileDialogs? fileDialogs = null,
        JsonHiddenRefsStore? hiddenRefs = null)
    {
        _git = git;
        Info = info;
        Title = info.Name;
        var path = info.WorkingDirectory ?? info.GitDirectory;
        // J6.16: git used the account's token and the service refused it; the command went on with the credential helpers.
        if (accounts is not null && notifier is not null)
        {
            _accounts = accounts;
            _tokenRejected = (_, e) =>
            {
                if (!string.Equals(e.RepositoryPath, path, StringComparison.OrdinalIgnoreCase)) return;
                foreach (var provider in e.Providers)
                {
                    var (service, rights) = provider == HostingProvider.GitHub ? ("GitHub", "repo") : ("Azure DevOps", "Code (Read & Write)");
                    notifier.Notify(NotificationSeverity.Warning, Loc.Get("Hosting.GitToken.Title"), Loc.Format("Hosting.GitToken.Message", service, rights));
                }
            };
            accounts.GitTokenRejected += _tokenRejected;
        }
        // Keyed by the Git directory: the same repository opened from a worktree or a subfolder shares its choice.
        Graph = new GraphViewModel(git, path, time, logger, new HiddenRefs(hiddenRefs, info.GitDirectory), preferences);
        Details = new CommitDetailsViewModel(git, path, Graph, time);
        WorkingTree = new WorkingTreeViewModel(git, info, watchers, logger, notifier, confirmations, profiles, commitMessages);
        Diff = new DiffViewModel(git, path, WorkingTree.Actions, preferences);
        Remote = new RemoteActionsViewModel(git, path, WorkingTree, Graph, notifier, logger, _disposed.Token, authentication, time, preferences);
        RefActions = new RefActionsViewModel(git, path, Graph, WorkingTree.Actions, Remote, WorkingTree.IsAvailable, inputs, notifier, _disposed.Token, preferences);
        Merge = new MergeActionsViewModel(git, path, Graph, WorkingTree, notifier, _disposed.Token);
        ConflictPrevention = new ConflictPreventionViewModel(git, path, WorkingTree, Graph, Merge, Remote, time, _disposed.Token);
        Stash = new StashActionsViewModel(git, path, Graph, WorkingTree, inputs, notifier, _disposed.Token);
        History = new HistoryActionsViewModel(git, path, Graph, WorkingTree, notifier, _disposed.Token);
        Patches = new PatchActionsViewModel(git, path, Graph, WorkingTree, fileDialogs, notifier, _disposed.Token);
        Folders = new FolderActionsViewModel(git, path, WorkingTree, inputs, notifier, _disposed.Token);
        Conflicts = new ConflictEditorViewModel(git, path, WorkingTree, Merge, preferences, notifier, _disposed.Token);
        Rebase = new InteractiveRebaseViewModel(git, path, Graph, WorkingTree, time, notifier, _disposed.Token);
        FileHistory = new FileHistoryViewModel(git, path, time, WorkingTree.IsAvailable, confirmations, notifier, _disposed.Token, preferences);
        Blame = new BlameViewModel(git, path, time, _disposed.Token);
        Undo = new UndoHistory(git, path, WorkingTree, Graph, notifier, _disposed.Token);
        WorkingTree.Actions.Undo = Undo;

        Hosting = new HostingPanelViewModel(accounts, RefActions, Graph, inputs, notifier, launcher, time, _disposed.Token, () => profiles?.IdFor(path));
        PullRequest = new PullRequestViewModel(accounts, Hosting, launcher, notifier, time, _disposed.Token, () => profiles?.IdFor(path), confirmations, preferences);
        Remote.FetchCompleted += (_, _) => _ = Hosting.RefreshAsync();
        Hosting.PullRequestOpener = ShowPullRequestAsync;
        Summary = new RepositorySummaryViewModel(git, path, info.Name, WorkingTree, launcher, time, _disposed.Token);
        Stats = new RepositoryStatsViewModel(git, path, WorkingTree, _disposed.Token);
        Issue =new IssueViewModel(accounts, Hosting, launcher, notifier, time, _disposed.Token, () => profiles?.IdFor(path), confirmations, inputs);
        WorkItem = new WorkItemViewModel(accounts, Hosting, launcher, notifier, time, _disposed.Token, () => profiles?.IdFor(path));
        Hosting.IssueOpener = ShowIssueAsync;
        Worktrees = new WorktreeActionsViewModel(git, path, Graph, WorkingTree.Actions, inputs, notifier, openRepository, _disposed.Token);
        Submodules = new SubmoduleActionsViewModel(git, path, Graph, WorkingTree.Actions, inputs, notifier, openRepository, _disposed.Token, preferences);
        Remote.HeadMoved = Submodules.UpdateAfterHeadMoveAsync;
        Stash.HeadMoved = Submodules.UpdateAfterHeadMoveAsync;
        Lfs = new LfsActionsViewModel(git, path, Graph, WorkingTree.Actions, inputs, notifier, _disposed.Token);
        GitFlow = new GitFlowActionsViewModel(git, path, Graph, WorkingTree.Actions, inputs, notifier, _disposed.Token);
        Refs = new RefsPanelViewModel(git, path, Graph, WorkingTree.IsAvailable ? Stash : null, RefActions, Merge, Hosting, Worktrees, Submodules, Lfs, GitFlow);
        Terminal = new TerminalPanelViewModel(terminal, path, info.Name, notifier);
        Search = new CommitSearchViewModel(Graph, time);
        BranchPicker = new BranchPickerViewModel(Graph, Refs, WorkingTree.IsAvailable);
        WorkingTree.CommitForm.PushAfterCommit = () => Remote.PushCommand.ExecuteAsync(null);
        Details.FileOpened += OnFileOpened;
        Details.StashFilesRequested += OnStashFilesRequested;
        Details.PropertyChanged += OnDetailsPropertyChanged;
        Graph.PropertyChanged += OnGraphPropertyChanged;
        Diff.PropertyChanged += OnDiffPropertyChanged;
        Conflicts.PropertyChanged += OnConflictsPropertyChanged;
        Rebase.PropertyChanged += OnRebasePropertyChanged;
        FileHistory.PropertyChanged += OnInspectionPropertyChanged;
        Blame.PropertyChanged += OnInspectionPropertyChanged;
        FileHistory.InspectionRequested += OnInspectionRequested;
        Blame.InspectionRequested += OnInspectionRequested;
        Blame.CommitShown += OnBlameCommitShown;
        PullRequest.PropertyChanged += OnPullRequestPropertyChanged;
        Issue.PropertyChanged += OnIssuePropertyChanged;
        WorkItem.PropertyChanged += OnWorkItemPropertyChanged;
        Summary.PropertyChanged += OnSummaryPropertyChanged;
        Summary.ShaRequested += OnSummaryShaRequested;
        Stats.PropertyChanged += OnStatsPropertyChanged;
        WorkingTree.InspectionRequested += OnInspectionRequested;
        Details.InspectionRequested += OnInspectionRequested;
        ConflictPrevention.InspectionRequested += OnInspectionRequested;
        WorkingTree.FileOpened += OnWorkingFileOpened;
        WorkingTree.StatusChanged += OnStatusChanged;
        WorkingTree.RefsChanged += OnRefsChanged;
    }

    [ObservableProperty]
    public partial RepositoryInfo Info { get; set; }

    public GraphViewModel Graph { get; }

    /// <summary>Search box of the toolbar: narrows the graph to the matching commits.</summary>
    public CommitSearchViewModel Search { get; }

    /// <summary>"Branche" selector of the toolbar: filterable list of branches and tags to check out.</summary>
    public BranchPickerViewModel BranchPicker { get; }

    public CommitDetailsViewModel Details { get; }

    public DiffViewModel Diff { get; }

    public WorkingTreeViewModel WorkingTree { get; }

    /// <summary>Fetch, pull and push (toolbar).</summary>
    public RemoteActionsViewModel Remote { get; }

    /// <summary>Create, rename, delete and push branches and tags; manage remotes (menus, Ctrl+B, Ctrl+T).</summary>
    public RefActionsViewModel RefActions { get; }

    /// <summary>Merge and rebase (menus, drag and drop, banner of an operation stopped on conflicts).</summary>
    public MergeActionsViewModel Merge { get; }

    /// <summary>Warning of the toolbar's branch button about a conflict with the default remote branch (J7.11).</summary>
    public ConflictPreventionViewModel ConflictPrevention { get; }

    /// <summary>Stash, apply, pop and delete stashes; checkout carrying the local changes (toolbar, panels, Ctrl+Maj+S).</summary>
    public StashActionsViewModel Stash { get; }

    /// <summary>Cherry-pick, revert and reset (menu of a commit row).</summary>
    public HistoryActionsViewModel History { get; }

    /// <summary>Create and apply patch files (J7.12, ADR-079).</summary>
    public PatchActionsViewModel Patches { get; }

    /// <summary>Menu of a folder of the working directory panel (discard, ignore, new file).</summary>
    public FolderActionsViewModel Folders { get; }

    /// <summary>Conflict editor (J5.4), shown in place of the graph.</summary>
    public ConflictEditorViewModel Conflicts { get; }

    /// <summary>Interactive rebase editor (J5.5), shown in place of the graph.</summary>
    public InteractiveRebaseViewModel Rebase { get; }

    /// <summary>History of a file (J5.6), shown in place of the graph.</summary>
    public FileHistoryViewModel FileHistory { get; }

    /// <summary>Blame of a file (J5.6), shown in place of the graph.</summary>
    public BlameViewModel Blame { get; }

    /// <summary>Pull request screen (J6.9a), shown in place of the graph.</summary>
    public PullRequestViewModel PullRequest { get; }

    /// <summary>Issue screen (J6.10a), shown in place of the graph.</summary>
    public IssueViewModel Issue { get; }

    /// <summary>Work item screen (J6.14, Azure DevOps), shown in place of the graph.</summary>
    public WorkItemViewModel WorkItem { get; }

    /// <summary>"Résumé" page (J7.13, ADR-080), shown in place of the graph and of the right panel.</summary>
    public RepositorySummaryViewModel Summary { get; }

    /// <summary>"Statistiques" page (J7.14, ADR-080), shown in place of the graph and of the right panel.</summary>
    public RepositoryStatsViewModel Stats { get; }

    /// <summary>
    /// The main area shows the graph: neither a diff, the conflict editor, the interactive rebase, a file history, a
    /// blame, a pull request, an issue, the summary nor the statistics is open.
    /// </summary>
    public bool ShowGraph => !Diff.IsOpen && !Conflicts.IsOpen && !Rebase.IsOpen && !FileHistory.IsOpen && !Blame.IsOpen && !PullRequest.IsOpen && !Issue.IsOpen
        && !WorkItem.IsOpen && !Summary.IsOpen && !Stats.IsOpen;

    /// <summary>The right column is shown: the summary and statistics pages take the whole width.</summary>
    public bool ShowRightPanel => !Summary.IsOpen && !Stats.IsOpen;

    /// <summary>Undo / Redo of the operations made in this tab (toolbar, Ctrl+Z / Ctrl+Y).</summary>
    public UndoHistory Undo { get; }

    /// <summary>Pull requests and issues of the hosting service behind the remote; creation of a pull request (J6.2).</summary>
    public HostingPanelViewModel Hosting { get; }

    /// <summary>Linked working directories: list, create, open in a tab, remove, prune (J7.1).</summary>
    public WorktreeActionsViewModel Worktrees { get; }

    /// <summary>Nested repositories: list, initialize / update, open in a tab, deinitialize, add (J7.2).</summary>
    public SubmoduleActionsViewModel Submodules { get; }

    /// <summary>Git LFS: tracked patterns, files, track / untrack, download of the missing objects (J7.5).</summary>
    public LfsActionsViewModel Lfs { get; }

    /// <summary>Git Flow: initialization, start and finish of features, releases and hotfixes (J7.8).</summary>
    public GitFlowActionsViewModel GitFlow { get; }

    /// <summary>Integrated terminal (J7.10): shells in the working directory, panel at the bottom of the tab.</summary>
    public TerminalPanelViewModel Terminal { get; }

    /// <summary>Branches, remotes, tags, stashes, worktrees, submodules and Git LFS (left panel).</summary>
    public RefsPanelViewModel Refs { get; }

    /// <summary>
    /// Double click on a branch badge of the graph: checkout of the branch (the local one for a remote branch), then
    /// pull when it has an upstream. False when <paramref name="reference"/> is not a branch.
    /// </summary>
    public bool TryCheckoutAndPull(GitRef reference)
    {
        if (reference.Kind is not (GitRefKind.LocalBranch or GitRefKind.RemoteBranch) || !WorkingTree.IsAvailable) return false;
        _ = CheckoutAndPullAsync(reference);
        return true;
    }

    private async Task CheckoutAndPullAsync(GitRef reference)
    {
        await Refs.CheckoutRefAsync(reference);
        if (_disposed.IsCancellationRequested || WorkingTree.HasConflicts) return;

        // Pull only if the branch the user double-clicked is the one checked out (a failed or declined checkout stays quiet).
        var onBranch = WorkingTree.Status is { } status && (reference.Kind == GitRefKind.LocalBranch
            ? status.Branch == reference.Name
            : status.Upstream == reference.Name);
        if (onBranch && Remote.CanPull) await Remote.PullCommand.ExecuteAsync(null);
    }

    private readonly CancellationTokenSource _disposed = new();

    private readonly HostAccounts? _accounts;

    private readonly EventHandler<GitTokenRejectedEventArgs>? _tokenRejected;

    private readonly IGitService _git;

    /// <summary>Icon chosen for this repository (a Fluent System Icons name); null keeps the folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconName))]
    public partial string? CustomIcon { get; set; }

    public override string IconName => CustomIcon ?? "folder";

    public string HeadDisplay => Info.HeadName ?? "main";

    partial void OnInfoChanged(RepositoryInfo value) => OnPropertyChanged(nameof(HeadDisplay));

    /// <summary>The right panel shows the working directory: no commit selected (or the "// WIP" row).</summary>
    public bool ShowWorkingTree => WorkingTree.IsAvailable && !Details.HasSelection;

    /// <summary>Loads the graph and the working-directory status; call from the UI thread each time the tab is shown.</summary>
    public async Task LoadAsync()
    {
        // Everything starts at once: awaiting the worktree and submodule lists (one git.exe each) before the graph delayed
        // the first paint of both panels. Their continuations run on the UI thread, which serializes the panel rebuilds.
        await Task.WhenAll(Worktrees.LoadAsync(), Submodules.LoadAsync(), Lfs.LoadAsync(), GitFlow.LoadAsync(), Graph.LoadAsync(), WorkingTree.StartAsync());
    }

    /// <summary>Stops loading and watching, and frees the graph when the tab is closed.</summary>
    public void Dispose()
    {
        if (_accounts is not null && _tokenRejected is not null) _accounts.GitTokenRejected -= _tokenRejected;
        Terminal.Dispose();
        Search.Dispose();
        Details.FileOpened -= OnFileOpened;
        Details.StashFilesRequested -= OnStashFilesRequested;
        Details.PropertyChanged -= OnDetailsPropertyChanged;
        Graph.PropertyChanged -= OnGraphPropertyChanged;
        Diff.PropertyChanged -= OnDiffPropertyChanged;
        Conflicts.PropertyChanged -= OnConflictsPropertyChanged;
        Rebase.PropertyChanged -= OnRebasePropertyChanged;
        FileHistory.PropertyChanged -= OnInspectionPropertyChanged;
        Blame.PropertyChanged -= OnInspectionPropertyChanged;
        FileHistory.InspectionRequested -= OnInspectionRequested;
        Blame.InspectionRequested -= OnInspectionRequested;
        Blame.CommitShown -= OnBlameCommitShown;
        PullRequest.PropertyChanged -= OnPullRequestPropertyChanged;
        Issue.PropertyChanged -= OnIssuePropertyChanged;
        WorkItem.PropertyChanged -= OnWorkItemPropertyChanged;
        Summary.PropertyChanged -= OnSummaryPropertyChanged;
        Summary.ShaRequested -= OnSummaryShaRequested;
        Stats.PropertyChanged -= OnStatsPropertyChanged;
        WorkingTree.InspectionRequested -= OnInspectionRequested;
        Details.InspectionRequested -= OnInspectionRequested;
        ConflictPrevention.InspectionRequested -= OnInspectionRequested;
        WorkingTree.FileOpened -= OnWorkingFileOpened;
        WorkingTree.StatusChanged -= OnStatusChanged;
        WorkingTree.RefsChanged -= OnRefsChanged;
        _disposed.Cancel();
        ConflictPrevention.Dispose();
        Undo.Dispose();
        Conflicts.Dispose();
        Refs.Dispose();
        Worktrees.Dispose();
        Submodules.Dispose();
        Lfs.Dispose();
        GitFlow.Dispose();
        PullRequest.Dispose();
        Issue.Dispose();
        WorkItem.Dispose();
        Summary.Dispose();
        Stats.Dispose();
        Hosting.Dispose();
        Stash.Dispose();
        Merge.Dispose();
        Remote.Dispose();
        WorkingTree.Dispose();
        Diff.Dispose();
        Details.Dispose();
        Graph.Dispose();
    }

    private async void OnStashFilesRequested(object? sender, StashFilesRequest request) => await Stash.ApplyFilesAsync(request.Stash, request.Files);

    private async void OnFileOpened(object? sender, ChangedFile file)
    {
        if (Details.Details is not { } commit || !await Conflicts.ConfirmCloseAsync()) return;
        Conflicts.Close();
        Rebase.Close();
        CloseInspections();
        await Diff.OpenAsync(commit, file);
    }

    private async void OnWorkingFileOpened(object? sender, WorkingFileItem item)
    {
        // The choices made in the conflict editor are lost when another file is opened: asked first.
        if (item.Path == Conflicts.Path || !await Conflicts.ConfirmCloseAsync()) return;
        Rebase.Close();
        CloseInspections();
        if (item.IsConflicted)
        {
            Diff.CloseCommand.Execute(null);
            await Conflicts.OpenAsync(item.Entry);
            return;
        }
        Conflicts.Close();
        WorkingTree.MarkOpened(item.Path, item.Side);
        await Diff.OpenWorkingAsync(item.Entry, item.Side);
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        Graph.SetWorkInProgress(WorkingTree.ChangeCount, Merge.WorkInProgressText);

        // HEAD moved (commit, amend, checkout, reset, in VIGO or elsewhere): the graph and the tab follow.
        if (WorkingTree.Status is { } status)
        {
            _ = Graph.SyncHeadAsync(status.Head, status.Branch);
            var headName = status.Branch ?? status.Head?.ToShortString();
            if (headName != Info.HeadName || status.Head?.ToString() != Info.HeadSha)
            {
                Info = Info with
                {
                    HeadName = headName,
                    HeadSha = status.Head?.ToString(),
                    IsHeadDetached = status.Branch is null && status.Head is not null,
                    IsEmpty = status.Head is null,
                };
            }
        }

        // A working-directory diff follows the file: reread it, or close it once that side has no change left.
        if (Diff.WorkingFile is not { } shown) return;
        var entry = WorkingTree.Status?.Entries.FirstOrDefault(e => e.Path == shown.Entry.Path);
        var stillChanged = shown.Side == WorkingDiffSide.Staged ? entry?.IsStaged : entry?.IsUnstaged;
        if (entry is null || stillChanged != true || entry.IsConflicted)
        {
            // Staged (or unstaged) from the preview: go on with the next file of the same list rather than back to the graph.
            var next = entry?.IsConflicted == true ? null : WorkingTree.NextAfter(shown.Entry.Path, shown.Side);
            if (next is null)
            {
                Diff.CloseCommand.Execute(null);
            }
            else
            {
                WorkingTree.MarkOpened(next.Path, shown.Side);
                _ = Diff.OpenWorkingAsync(next.Entry, shown.Side);
            }
        }
        else
            _ = Diff.OpenWorkingAsync(entry, shown.Side, refresh: true);
    }

    // Fetch, tag, branch or stash made anywhere: badges and left panel follow; the history only if it changed.
    private void OnRefsChanged(object? sender, EventArgs e) => _ = Graph.RefreshAsync();

    // A commit diff belongs to the selected commit: selecting another commit (or the WIP row) closes it. A
    // working-directory diff stays open while the WIP row is selected.
    private void OnGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Choosing a commit (graph, left panel) brings the graph back from the summary page.
        if (e.PropertyName == nameof(GraphViewModel.SelectedSha))
        {
            Summary.Close();
            Stats.Close();
        }
        if (e.PropertyName != nameof(GraphViewModel.SelectedSha) || !Diff.IsOpen) return;
        if (Diff.WorkingFile is null || Graph.SelectedSha is not null)
            Diff.CloseCommand.Execute(null);
    }

    private void OnDetailsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommitDetailsViewModel.HasSelection))
            OnPropertyChanged(nameof(ShowWorkingTree));
    }

    private void OnDiffPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DiffViewModel.IsOpen)) return;
        if (!Diff.IsOpen && !Conflicts.IsOpen) WorkingTree.MarkOpened(null, WorkingDiffSide.Unstaged);
        if (Diff.IsOpen) CloseInspections();
        OnPropertyChanged(nameof(ShowGraph));
    }

    // The conflict editor can also open from the banner: it takes the main area.
    private void OnConflictsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConflictEditorViewModel.IsOpen)) return;
        if (Conflicts.IsOpen) CloseInspections();
        OnPropertyChanged(nameof(ShowGraph));
    }

    // The interactive rebase takes the main area: an open diff is closed (no conflict editor without an operation).
    private void OnRebasePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InteractiveRebaseViewModel.IsOpen)) return;
        if (Rebase.IsOpen)
        {
            Diff.CloseCommand.Execute(null);
            Conflicts.Close();
            CloseInspections();
        }
        OnPropertyChanged(nameof(ShowGraph));
    }

    private void OnInspectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileHistoryViewModel.IsOpen) or nameof(BlameViewModel.IsOpen)) OnPropertyChanged(nameof(ShowGraph));
    }

    // What a file inspection or the pull request screen shows in the main area gives way to a diff, the conflict editor or the rebase.
    private void CloseInspections()
    {
        FileHistory.Close();
        Blame.Close();
        PullRequest.Close();
        Issue.Close();
        WorkItem.Close();
        Summary.Close();
        Stats.Close();
    }

    private void OnSummaryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RepositorySummaryViewModel.IsOpen)) return;
        OnPropertyChanged(nameof(ShowGraph));
        OnPropertyChanged(nameof(ShowRightPanel));
    }

    private void OnStatsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RepositoryStatsViewModel.IsOpen)) return;
        OnPropertyChanged(nameof(ShowGraph));
        OnPropertyChanged(nameof(ShowRightPanel));
    }

    /// <summary>Shows the statistics page in place of the graph (left panel); whatever else had the main area is closed (the conflict editor asks first).</summary>
    public async Task ShowStatsAsync()
    {
        if (!await Conflicts.ConfirmCloseAsync()) return;
        Conflicts.Close();
        Rebase.Close();
        Diff.CloseCommand.Execute(null);
        FileHistory.Close();
        Blame.Close();
        PullRequest.Close();
        Issue.Close();
        WorkItem.Close();
        Summary.Close();
        await Stats.OpenAsync();
    }

    // A branch, tag or commit of the page: back to the graph, on that commit when it is loaded.
    private void OnSummaryShaRequested(object? sender, GitSha sha)
    {
        Summary.Close();
        _ = Graph.TrySelectLoadingAsync(sha);
    }

    /// <summary>Shows the summary page in place of the graph (left panel); whatever else had the main area is closed (the conflict editor asks first).</summary>
    public async Task ShowSummaryAsync()
    {
        if (!await Conflicts.ConfirmCloseAsync()) return;
        Conflicts.Close();
        Rebase.Close();
        Diff.CloseCommand.Execute(null);
        FileHistory.Close();
        Blame.Close();
        PullRequest.Close();
        Issue.Close();
        WorkItem.Close();
        Stats.Close();
        await Summary.OpenAsync();
    }

    private void OnIssuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IssueViewModel.IsOpen)) OnPropertyChanged(nameof(ShowGraph));
    }

    private void OnPullRequestPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PullRequestViewModel.IsOpen)) OnPropertyChanged(nameof(ShowGraph));
    }

    /// <summary>
    /// Shows a pull request in place of the graph (left panel, palette); whatever else had the main area is closed (the
    /// conflict editor asks first).
    /// </summary>
    public async Task ShowPullRequestAsync(PullRequest pull)
    {
        if (!await Conflicts.ConfirmCloseAsync()) return;
        Conflicts.Close();
        Rebase.Close();
        Diff.CloseCommand.Execute(null);
        FileHistory.Close();
        Blame.Close();
        Issue.Close();
        WorkItem.Close();
        Summary.Close();
        Stats.Close();
        await PullRequest.OpenAsync(pull);
    }

    /// <summary>Shows an issue in place of the graph (left panel); whatever else had the main area is closed (the conflict editor asks first).</summary>
    public async Task ShowIssueAsync(Issue issue)
    {
        if (!await Conflicts.ConfirmCloseAsync()) return;
        Conflicts.Close();
        Rebase.Close();
        Diff.CloseCommand.Execute(null);
        FileHistory.Close();
        Blame.Close();
        PullRequest.Close();
        Summary.Close();
        Stats.Close();
        // Azure DevOps calls them work items and has a screen of its own for their fields, links and history (J6.14).
        if (Hosting.Repository is { Provider: HostingProvider.AzureDevOps })
        {
            Issue.Close();
            await WorkItem.OpenAsync(issue);
        }
        else
        {
            WorkItem.Close();
            await Issue.OpenAsync(issue);
        }
    }

    private void OnWorkItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkItemViewModel.IsOpen)) OnPropertyChanged(nameof(ShowGraph));
    }

    private async void OnInspectionRequested(object? sender, FileInspection request) => await InspectAsync(request);

    /// <summary>
    /// Shows the history or the blame of a file in place of the graph (menus of the file lists, palette, links between
    /// the two screens); whatever else had the main area is closed (the conflict editor asks first).
    /// </summary>
    public async Task InspectAsync(FileInspection request)
    {
        if (!await Conflicts.ConfirmCloseAsync()) return;
        Conflicts.Close();
        Rebase.Close();
        PullRequest.Close();
        Issue.Close();
        WorkItem.Close();
        Summary.Close();
        Stats.Close();
        Diff.CloseCommand.Execute(null);
        if (request.Kind == FileInspectionKind.History)
        {
            Blame.Close();
            await FileHistory.OpenAsync(request.Path);
        }
        else
        {
            FileHistory.Close();
            await Blame.OpenAsync(request.Path, request.Revision);
        }
    }

    // "Afficher dans le graphe" of the blame: back to the graph, on that commit when it is loaded.
    private void OnBlameCommitShown(object? sender, GitSha sha)
    {
        Blame.Close();
        _ = Graph.TrySelectLoadingAsync(sha);
    }
}
