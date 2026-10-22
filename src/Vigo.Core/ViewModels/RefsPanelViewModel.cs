using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Kind of row of the left panel (mockup: sections, folders, remotes, refs).</summary>
public enum RefRowKind
{
    Section,
    Folder,
    Remote,
    LocalBranch,
    DetachedHead,
    RemoteBranch,
    Tag,
    Stash,

    /// <summary>Pull request of the hosting service (J6.2); <see cref="RefRow.Ref"/> is its remote branch when that is known.</summary>
    PullRequest,

    Issue,

    /// <summary>Line of text under a hosting section: loading, sign-in needed, error, nothing open.</summary>
    Hint,

    /// <summary>Working directory of the repository (J7.1); <see cref="RefRow.Worktree"/> is it.</summary>
    Worktree,

    /// <summary>Nested repository of the repository (J7.2); <see cref="RefRow.Submodule"/> is it.</summary>
    Submodule,

    /// <summary>File pattern stored with Git LFS (J7.5); <see cref="RefRow.LfsPattern"/> is it.</summary>
    LfsPattern,

    /// <summary>Feature, release or hotfix branch in progress (J7.8); <see cref="RefRow.Ref"/> is the local branch.</summary>
    FlowBranch,
}

/// <summary>Collapsible sections of the left panel.</summary>
public enum RefSection
{
    Local,
    Remote,
    Tags,
    Stashes,
    PullRequests,
    Issues,
    Worktrees,
    Submodules,
    Lfs,
    GitFlow,
}

/// <summary>
/// One row of the left panel. Immutable: a row whose content changes is replaced in <see cref="RefsPanelViewModel.Rows"/>.
/// </summary>
/// <param name="Key">Identity across refreshes ("local:refs/heads/main", "folder:local/feature"…).</param>
/// <param name="Depth">Nesting level inside its section (0 for a section header and its direct children).</param>
/// <param name="Meta">Text on the right: count, ahead/behind, remote host…</param>
/// <param name="ColorIndex">Lane palette color of the icon (branches: the lane of their tip in the graph).</param>
/// <param name="Ref">The reference of a branch, tag or stash row.</param>
/// <param name="Url">Page of a pull request or an issue in the browser (double click, Enter, menu).</param>
/// <param name="Worktree">The working directory of a worktree row.</param>
/// <param name="Submodule">The submodule of a submodule row.</param>
/// <param name="LfsPattern">The pattern of an LFS pattern row.</param>
/// <param name="ItemType">Work item type of an Azure DevOps work item row (picks its icon).</param>
/// <param name="HideKey">Key of the ref in <see cref="HiddenRefs"/> when the row can be hidden from the graph; null otherwise.</param>
/// <param name="IsHidden">The ref is hidden from the graph: the row is dimmed and its eye crossed out.</param>
public sealed record RefRow(
    string Key,
    RefRowKind Kind,
    string Label,
    int Depth,
    string Meta = "",
    bool IsExpanded = true,
    bool IsHead = false,
    bool IsMetaAccent = false,
    int? ColorIndex = null,
    string ToolTip = "",
    GitRef? Ref = null,
    RefSection Section = RefSection.Local,
    string? Url = null,
    Worktree? Worktree = null,
    Submodule? Submodule = null,
    LfsPattern? LfsPattern = null,
    string? ItemType = null,
    string? HideKey = null,
    bool IsHidden = false)
{
    /// <summary>The row has an eye to hide its ref from the graph (not the checked-out branch, which the graph always shows).</summary>
    public bool CanHide => HideKey is not null;

    public bool IsExpandable => Kind is RefRowKind.Section or RefRowKind.Folder or RefRowKind.Remote;

    public bool IsSection => Kind == RefRowKind.Section;

    /// <summary>Branch, tag or detached HEAD: can be checked out.</summary>
    public bool IsCheckoutable => Kind is RefRowKind.LocalBranch or RefRowKind.RemoteBranch or RefRowKind.Tag or RefRowKind.FlowBranch;

    /// <summary>Pull request or issue: has a page to open in the browser.</summary>
    public bool IsOpenable => Url is not null && Kind is RefRowKind.PullRequest or RefRowKind.Issue;

    /// <summary>Left padding in pixels (mockup: 10 px for a header, 28 px for its rows, +16 px per level).</summary>
    public double Indent => IsSection ? 10 : 28 + 16 * Depth;

    /// <summary>
    /// Name read by the narrator: a list item without a name of its own is announced by the ToString of its data.
    /// </summary>
    public override string ToString() =>
        (IsHead ? Loc.Format("Refs.Narrator.Head", Label) : Label) + (Meta.Length > 0 ? ", " + Meta : string.Empty);
}

/// <summary>
/// Context menu entries of a left panel row (mockup: checkout, merge / rebase (J4.3), rename F2, delete Suppr).
/// A null label or a false flag hides the entry.
/// </summary>
public sealed record RefRowMenu
{
    public string? CheckoutLabel { get; init; }

    /// <summary>"Fusionner x dans &lt;branche extraite&gt;" (J4.3).</summary>
    public MergePlan? Merge { get; init; }

    /// <summary>"Rebaser &lt;branche extraite&gt; sur x" (J4.3).</summary>
    public RebasePlan? Rebase { get; init; }

    /// <summary>"Appliquer" and "Pop" of a stash row (J4.4).</summary>
    public bool CanApplyStash { get; init; }

    public bool CanCreateBranch { get; init; }

    public bool CanCreateTag { get; init; }

    public string? PushLabel { get; init; }

    public bool CanRename { get; init; }

    public string? DeleteLabel { get; init; }

    /// <summary>The delete entry is enabled (shown disabled for the checked-out branch).</summary>
    public bool CanDelete { get; init; }

    public bool CanAddRemote { get; init; }

    /// <summary>"Créer une pull request…" on a local branch (J6.2).</summary>
    public bool CanCreatePullRequest { get; init; }

    /// <summary>"Afficher la pull request" on a pull request row: the screen of the app (J6.9a), for a service that gives the detail.</summary>
    public bool CanShowPullRequest { get; init; }

    /// <summary>"Afficher le ticket" is offered (J6.10a).</summary>
    public bool CanShowIssue { get; init; }

    /// <summary>Text of Loc.Get("Refs.Menu.OpenInBrowser") and the page it opens (pull request, issue, hosting section); null hides it.</summary>
    public string? OpenLabel { get; init; }

    public string? OpenUrl { get; init; }

    /// <summary>"Nouvelle issue…" on the issue section header (J6.10c).</summary>
    public bool CanCreateIssue { get; init; }

    /// <summary>"Actualiser" on a pull request or issue section.</summary>
    public bool CanRefreshHosting { get; init; }

    /// <summary>Entries of the "Filtrer" sub-menu of the issue section header (J6.10a); null hides it.</summary>
    public IReadOnlyList<IssueFilterEntry>? IssueFilters { get; init; }

    /// <summary>Text of "Copier …" when it is not the name of a ref (link of a pull request); null: the default text.</summary>
    public string? CopyLabel { get; init; }

    /// <summary>Remote of a remote row (edit / remove entries).</summary>
    public string? RemoteName { get; init; }

    public bool CanEditRemote { get; init; }

    public bool CanRemoveRemote { get; init; }

    public string? CopyText { get; init; }

    /// <summary>"Nouveau worktree…" (section header) or "Ouvrir dans un nouveau worktree…" (local branch).</summary>
    public bool CanAddWorktree { get; init; }

    /// <summary>"Ouvrir le worktree dans un onglet": the worktree of the row, when it is not the one of this tab.</summary>
    public bool CanOpenWorktree { get; init; }

    public bool CanRemoveWorktree { get; init; }

    /// <summary>"Nettoyer les worktrees" on the section header, when one of them lost its directory.</summary>
    public bool CanPruneWorktrees { get; init; }

    /// <summary>"Ouvrir le submodule dans un onglet" (J7.2): an initialized submodule, when the tab can open repositories.</summary>
    public bool CanOpenSubmodule { get; init; }

    /// <summary>Text of "Initialiser et mettre à jour" / "Mettre à jour" on a submodule row; null hides it.</summary>
    public string? UpdateSubmoduleLabel { get; init; }

    /// <summary>"Mettre à jour les submodules" on the section header.</summary>
    public bool CanUpdateAllSubmodules { get; init; }

    /// <summary>"Ajouter un submodule…" on the section header or a submodule row.</summary>
    public bool CanAddSubmodule { get; init; }

    /// <summary>"Désinitialiser…" on an initialized submodule.</summary>
    public bool CanDeinitSubmodule { get; init; }

    /// <summary>"Suivre avec Git LFS…" on the section header or a pattern row (J7.5).</summary>
    public bool CanTrackLfs { get; init; }

    /// <summary>"Ne plus suivre" on an LFS pattern row.</summary>
    public bool CanUntrackLfs { get; init; }

    /// <summary>"Récupérer les fichiers LFS" on the section header, when some files are only pointers.</summary>
    public bool CanPullLfs { get; init; }

    /// <summary>"Initialiser Git Flow…" on the local branches header, while the repository does not use it (J7.8).</summary>
    public bool CanInitGitFlow { get; init; }

    /// <summary>"Démarrer une feature / release / hotfix…" on the Git Flow section and its branches.</summary>
    public bool CanStartGitFlow { get; init; }

    /// <summary>Text of "Terminer la feature x…" on a Git Flow branch row; null hides it.</summary>
    public string? FinishGitFlowLabel { get; init; }
}

/// <summary>Entry of the issue filter sub-menu (J6.10a): a toggle with a check mark, or an action.</summary>
/// <param name="SeparatorBefore">A separator is drawn above the entry.</param>
/// <param name="Run">What the entry does; reads the issues again.</param>
public sealed record IssueFilterEntry(string Label, bool IsChecked, bool SeparatorBefore, Func<Task> Run, bool IsEnabled = true);

/// <summary>
/// Item of <see cref="RefsPanelViewModel.Rows"/>: keeps its list container while its <see cref="Row"/> changes (a
/// replaced item gets a new container, which the list animates in from its top-left corner).
/// </summary>
/// <param name="toggleHidden">What the eye of the row does (<see cref="RefsPanelViewModel"/> hides or shows the ref in the graph).</param>
public sealed partial class RefRowItem(RefRow row, Action<RefRow>? toggleHidden = null) : ObservableObject
{
    [ObservableProperty]
    public partial RefRow Row { get; set; } = row;

    /// <summary>The pointer is over the row: its eye is shown (set by the panel).</summary>
    [ObservableProperty]
    public partial bool IsHovered { get; set; }

    /// <summary>The eye of the row: hides its ref from the graph, or shows it again.</summary>
    [RelayCommand]
    private void ToggleHidden() => toggleHidden?.Invoke(Row);

    /// <summary>Name read by the narrator.</summary>
    public override string ToString() => Row.ToString();
}

/// <summary>
/// Left panel of a repository tab: local branches (in folders), remotes and their branches, tags, stashes; filter;
/// selection of the commit of a ref; checkout. Built from <see cref="GraphViewModel.Refs"/>, so it follows every
/// refresh of the refs (watcher, operations).
/// </summary>
public sealed partial class RefsPanelViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly StashActionsViewModel? _stash;
    private readonly HashSet<string> _collapsed = [RefRowBuilder.SectionKey(RefSection.Tags), RefRowBuilder.SectionKey(RefSection.Issues), RefRowBuilder.SectionKey(RefSection.Worktrees), RefRowBuilder.SectionKey(RefSection.Submodules), RefRowBuilder.SectionKey(RefSection.Lfs)];
    private readonly HostingPanelViewModel? _hosting;
    private readonly WorktreeActionsViewModel? _worktrees;
    private readonly SubmoduleActionsViewModel? _submodules;
    private readonly LfsActionsViewModel? _lfs;
    private readonly GitFlowActionsViewModel? _gitFlow;
    private bool _remotesRequested;

    /// <summary>Keys of the hint rows of the issue section that act when clicked (J6.10a).</summary>
    internal const string MoreIssuesKey = "more:issues";
    internal const string IssueFilterKey = "filter:issues";

    /// <param name="stash">Checkout (carrying the local changes when needed) and stashes; null without working directory (bare repository).</param>
    /// <param name="refActions">Branch, tag and remote management; owns the list of remotes.</param>
    /// <param name="merge">Merge and rebase (menus, drag and drop).</param>
    /// <param name="hosting">Pull requests and issues of the hosting service (J6.2); null without hosting support.</param>
    /// <param name="worktrees">Linked working directories (J7.1); null hides the section.</param>
    /// <param name="submodules">Nested repositories (J7.2); null hides the section.</param>
    /// <param name="lfs">Git LFS (J7.5); null hides the section.</param>
    /// <param name="gitFlow">Git Flow (J7.8); null hides the section.</param>
    public RefsPanelViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, StashActionsViewModel? stash, RefActionsViewModel refActions, MergeActionsViewModel merge,
        HostingPanelViewModel? hosting = null, WorktreeActionsViewModel? worktrees = null, SubmoduleActionsViewModel? submodules = null,
        LfsActionsViewModel? lfs = null, GitFlowActionsViewModel? gitFlow = null)
    {
        _gitFlow = gitFlow;
        if (_gitFlow is not null) _gitFlow.Changed += OnGitFlowChanged;
        _worktrees = worktrees;
        if (_worktrees is not null) _worktrees.Changed += OnWorktreesChanged;
        _submodules = submodules;
        if (_submodules is not null) _submodules.Changed += OnSubmodulesChanged;
        _lfs = lfs;
        if (_lfs is not null) _lfs.Changed += OnLfsChanged;
        Merge = merge;
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _stash = stash;
        RefActions = refActions;
        _hosting = hosting;
        _graph.RefsChanged += OnGraphRefsChanged;
        _graph.PropertyChanged += OnGraphPropertyChanged;
        _graph.Hidden.Changed += OnHiddenChanged;
        RefActions.RemotesChanged += OnRemotesChanged;
        if (_hosting is not null) _hosting.Changed += OnHostingChanged;
        Rebuild();
    }

    /// <summary>The eye of a row: hides its ref from the graph, or shows it again (kept between sessions).</summary>
    private void ToggleHidden(RefRow row)
    {
        if (row.HideKey is { } key) _graph.Hidden.Toggle(key);
    }

    private void OnHiddenChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>Merge and rebase (menus, drag and drop of a branch on another one).</summary>
    public MergeActionsViewModel Merge { get; }

    /// <summary>Create, rename, delete, push (menus, F2, Delete).</summary>
    public RefActionsViewModel RefActions { get; }

    /// <summary>Rows shown, in order; updated in place (unchanged rows are kept) when the refs or the filter change.</summary>
    public ObservableCollection<RefRowItem> Rows { get; } = [];

    /// <summary>Case-insensitive text filter on ref names; every section is expanded while it is set.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>The filter hides every ref.</summary>
    [ObservableProperty]
    public partial bool HasNoMatch { get; private set; }

    partial void OnFilterTextChanged(string value) => Rebuild();

    /// <summary>
    /// Click on a row: expands or collapses a section, folder or remote; selects the commit of a branch or tag in the
    /// graph (when it is loaded).
    /// </summary>
    [RelayCommand]
    private void Activate(RefRow row)
    {
        switch (row.Key)
        {
            case MoreIssuesKey:
                _ = _hosting?.LoadMoreIssuesAsync();
                return;
            case IssueFilterKey:
                _ = _hosting?.ClearIssueFilterAsync();
                return;
        }

        if (row.IsExpandable)
        {
            if (!_collapsed.Remove(row.Key)) _collapsed.Add(row.Key);
            Rebuild();
            return;
        }

        if (row.Kind == RefRowKind.DetachedHead && _graph.Head is { } head) _ = _graph.TrySelectLoadingAsync(head);
        else if (row.Worktree is { Head: { } worktreeHead }) _ = _graph.TrySelectLoadingAsync(worktreeHead);
        else if (row.Ref is { } reference) _ = _graph.TrySelectLoadingAsync(reference.Target);
    }

    /// <summary>
    /// Double click, Enter or menu: switches to a local branch; for a remote branch, to the local branch of the same
    /// name tracking it (created when missing); for a tag, to its commit (detached HEAD). Local changes are kept when
    /// they do not conflict; otherwise VIGO offers to carry them (stash, checkout, pop).
    /// </summary>
    [RelayCommand]
    private async Task CheckoutAsync(RefRow row)
    {
        if (_stash is null || ToCheckoutTarget(row, _graph.Refs) is not { } target) return;
        // The branch that ends up checked out: the local one for a remote branch.
        var name = target.Value switch
        {
            CheckoutBranch b => b.Name,
            CheckoutRemoteBranch r => r.LocalName,
            _ => row.Ref!.Name,
        };
        await _stash.CheckoutAsync(target, name);
    }

    /// <summary>Text of the checkout menu entry of <paramref name="row"/>; null when it cannot be checked out.</summary>
    public string? CheckoutLabel(RefRow row) => _stash is null ? null : ToCheckoutTarget(row, _graph.Refs)?.Value switch
    {
        CheckoutBranch b => Loc.Format("Refs.Checkout.Branch", b.Name),
        CheckoutRemoteBranch r => Loc.Format("Refs.Checkout.Remote", r.LocalName, r.RemoteBranch),
        CheckoutCommit => Loc.Format("Refs.Checkout.Commit", row.Label),
        _ => null,
    };

    /// <summary>Entries of the context menu of <paramref name="row"/> (null: no menu, e.g. a folder).</summary>
    public RefRowMenu? MenuFor(RefRow row)
    {
        var actions = RefActions;
        var prompt = actions.CanPrompt;
        switch (row.Kind)
        {
            case RefRowKind.Section:
                return row.Section switch
                {
                    RefSection.Local when prompt => new RefRowMenu { CanCreateBranch = true, CanInitGitFlow = _gitFlow?.CanInitialize == true },
                    RefSection.GitFlow when _gitFlow is { CanPrompt: true } => new RefRowMenu { CanStartGitFlow = true },
                    RefSection.Tags when prompt => new RefRowMenu { CanCreateTag = true },
                    RefSection.Remote when prompt => new RefRowMenu { CanAddRemote = true },
                    RefSection.Worktrees when _worktrees is { CanPrompt: true } => new RefRowMenu
                    {
                        CanAddWorktree = true,
                        CanPruneWorktrees = _worktrees.HasPrunable,
                    },
                    RefSection.Submodules when _submodules is not null => new RefRowMenu
                    {
                        CanAddSubmodule = _submodules.CanPrompt,
                        CanUpdateAllSubmodules = _submodules.HasSubmodules,
                    },
                    RefSection.Lfs when _lfs is not null => new RefRowMenu
                    {
                        CanTrackLfs = _lfs.CanPrompt,
                        CanPullLfs = _lfs.CanPull,
                    },
                    RefSection.PullRequests or RefSection.Issues when _hosting is { Repository: { } hosted } => new RefRowMenu
                    {
                        CanRefreshHosting = _hosting.IsConnected,
                        CanCreateIssue = row.Section == RefSection.Issues && _hosting.CanCreateIssue,
                        OpenLabel = Loc.Format("Refs.Menu.OpenOn", hosted.Host),
                        OpenUrl = row.Section == RefSection.PullRequests ? hosted.PullRequestsUrl : hosted.IssuesUrl,
                        IssueFilters = row.Section == RefSection.Issues && _hosting.CanFilterIssues ? IssueFilterEntries(_hosting) : null,
                    },
                    _ => null,
                };

            case RefRowKind.Remote:
                return new RefRowMenu { CanAddRemote = prompt, RemoteName = row.Label, CanEditRemote = prompt, CanRemoveRemote = true, CopyText = row.Label };

            case RefRowKind.PullRequest or RefRowKind.Issue when row.Url is { } url:
                return new RefRowMenu
                {
                    CheckoutLabel = row.Kind == RefRowKind.PullRequest ? CheckoutLabel(row) : null,
                    CanShowPullRequest = row.Kind == RefRowKind.PullRequest && _hosting?.CanShowPullRequest == true,
                    CanShowIssue = row.Kind == RefRowKind.Issue && _hosting?.CanShowIssue == true,
                    OpenLabel = "Ouvrir dans le navigateur",
                    OpenUrl = url,
                    CopyText = url,
                    CopyLabel = Loc.Get("Refs.Menu.CopyLink"),
                };

            case RefRowKind.Worktree when row.Worktree is { } worktree && _worktrees is not null:
                var isCurrent = _worktrees.IsCurrent(worktree);
                return new RefRowMenu
                {
                    CanOpenWorktree = _worktrees.CanOpen && !isCurrent && !worktree.IsBare,
                    CanAddWorktree = _worktrees.CanPrompt,
                    CanRemoveWorktree = !worktree.IsMain && !isCurrent,
                    CopyText = worktree.Path,
                    CopyLabel = Loc.Get("Refs.Menu.CopyPath"),
                };

            case RefRowKind.Submodule when row.Submodule is { } submodule && _submodules is not null:
                return new RefRowMenu
                {
                    CanOpenSubmodule = _submodules.CanOpen && submodule.IsInitialized,
                    UpdateSubmoduleLabel = Loc.Get(submodule.IsInitialized ? "Refs.Menu.UpdateSubmodule" : "Refs.Menu.InitUpdateSubmodule"),
                    CanAddSubmodule = _submodules.CanPrompt,
                    CanDeinitSubmodule = submodule.IsInitialized,
                    CopyText = submodule.Path,
                    CopyLabel = Loc.Get("Refs.Menu.CopyPath"),
                };

            case RefRowKind.LfsPattern when row.LfsPattern is { } pattern && _lfs is not null:
                return new RefRowMenu
                {
                    CanTrackLfs = _lfs.CanPrompt,
                    CanUntrackLfs = true,
                    CanPullLfs = _lfs.CanPull,
                    CopyText = pattern.Pattern,
                    CopyLabel = Loc.Get("Refs.Menu.CopyPattern"),
                };

            case RefRowKind.FlowBranch when row.Ref is { } flowBranch && _gitFlow is { Config: { } flow } && flow.Parse(flowBranch.Name) is { } parsed:
                return new RefRowMenu
                {
                    CheckoutLabel = CheckoutLabel(row),
                    CanStartGitFlow = _gitFlow.CanPrompt,
                    FinishGitFlowLabel = _gitFlow.CanPrompt ? Loc.Format(parsed.Kind switch { GitFlowKind.Feature => "Refs.Menu.FinishFeature", GitFlowKind.Release => "Refs.Menu.FinishRelease", _ => "Refs.Menu.FinishHotfix" }, parsed.Name) : null,
                    CopyText = flowBranch.Name,
                };

            case RefRowKind.DetachedHead:
                return new RefRowMenu { CanCreateBranch = prompt, CanCreateTag = prompt, CopyText = _graph.Head?.ToString() };

            case RefRowKind.LocalBranch when row.Ref is { } branch:
                return new RefRowMenu
                {
                    CheckoutLabel = CheckoutLabel(row),
                    Merge = Merge.HeadMenuFor(branch)?.Merge,
                    Rebase = Merge.HeadMenuFor(branch)?.Rebase,
                    CanCreateBranch = prompt,
                    CanCreateTag = prompt,
                    // Not on the checked-out branch, nor on one already checked out in another worktree.
                    CanAddWorktree = _worktrees is { CanPrompt: true } && !branch.IsCurrent && _worktrees.WorktreeOf(branch.Name) is null,
                    PushLabel = actions.PushBranchLabel(branch),
                    CanCreatePullRequest = _hosting?.CanCreatePullRequest(branch) == true,
                    CanRename = prompt,
                    DeleteLabel = Loc.Format("Refs.Menu.DeleteBranch", branch.Name),
                    // The checked-out branch cannot be deleted (git refuses): shown, disabled.
                    CanDelete = !branch.IsCurrent,
                    CopyText = branch.Name,
                };

            case RefRowKind.RemoteBranch when row.Ref is { } remoteBranch:
                return new RefRowMenu
                {
                    CheckoutLabel = CheckoutLabel(row),
                    Merge = Merge.HeadMenuFor(remoteBranch)?.Merge,
                    Rebase = Merge.HeadMenuFor(remoteBranch)?.Rebase,
                    CanCreateBranch = prompt,
                    CanCreateTag = prompt,
                    DeleteLabel = Loc.Format("Refs.Menu.DeleteRemoteBranch", remoteBranch.Name),
                    CanDelete = true,
                    CopyText = remoteBranch.Name,
                };

            case RefRowKind.Tag when row.Ref is { } tag:
                return new RefRowMenu
                {
                    CheckoutLabel = CheckoutLabel(row),
                    CanCreateBranch = prompt,
                    PushLabel = actions.PushTagLabel(tag),
                    DeleteLabel = Loc.Format("Refs.Menu.DeleteTag", tag.Name),
                    CanDelete = true,
                    CopyText = tag.Name,
                };

            case RefRowKind.Stash when row.Ref is { } stash:
                return new RefRowMenu
                {
                    CanApplyStash = _stash is not null,
                    DeleteLabel = DeleteStashLabel(stash),
                    CanDelete = true,
                    CopyText = stash.Name,
                };

            default:
                return null;
        }
    }

    /// <summary>"Filtrer" sub-menu of the issue section: state, "me" filters, search, reset (J6.10a).</summary>
    internal static IReadOnlyList<IssueFilterEntry> IssueFilterEntries(HostingPanelViewModel hosting)
    {
        var query = hosting.IssueFilter;
        return
        [
            new(Loc.Get("Refs.IssueFilter.Open"), query.State == IssueStateFilter.Open, false, () => hosting.SetIssueFilterAsync(query with { State = IssueStateFilter.Open })),
            new(Loc.Get("Refs.IssueFilter.Closed"), query.State == IssueStateFilter.Closed, false, () => hosting.SetIssueFilterAsync(query with { State = IssueStateFilter.Closed })),
            new(Loc.Get("Refs.IssueFilter.All"), query.State == IssueStateFilter.All, false, () => hosting.SetIssueFilterAsync(query with { State = IssueStateFilter.All })),
            new(Loc.Get("Refs.IssueFilter.AssignedToMe"), query.AssignedToMe, true, () => hosting.SetIssueFilterAsync(query with { AssignedToMe = !query.AssignedToMe })),
            new(Loc.Get("Refs.IssueFilter.CreatedByMe"), query.CreatedByMe, false, () => hosting.SetIssueFilterAsync(query with { CreatedByMe = !query.CreatedByMe })),
            new(query.Search.Length > 0 ? Loc.Format("Refs.IssueFilter.SearchActive", query.Search) : Loc.Get("Refs.IssueFilter.Search"), query.Search.Length > 0, false, hosting.SearchIssuesAsync),
            new(Loc.Get("Refs.IssueFilter.Reset"), false, true, hosting.ClearIssueFilterAsync, !query.IsDefault),
        ];
    }

    /// <summary>Double click, Enter or menu on a pull request or an issue: its page in the browser (J6.2).</summary>
    [RelayCommand]
    private void OpenUrl(string url) => _hosting?.OpenUrlCommand.Execute(url);

    /// <summary>Double click, Enter or menu on a pull request: its screen in the app, or its page when the service has no screen yet (J6.9a).</summary>
    [RelayCommand]
    private Task ShowPullRequestAsync(RefRow row) =>
        row is { Kind: RefRowKind.PullRequest } && _hosting is not null
            && row.Key.StartsWith("pr:", StringComparison.Ordinal) && int.TryParse(row.Key.AsSpan(3), out var number)
            ? _hosting.ShowPullRequestAsync(number)
            : Task.CompletedTask;

    /// <summary>Double click, Enter or menu on an issue: its screen in the app, or its page when the service has no screen yet (J6.10a).</summary>
    [RelayCommand]
    private Task ShowIssueAsync(RefRow row) =>
        row is { Kind: RefRowKind.Issue } && _hosting is not null
            && row.Key.StartsWith("issue:", StringComparison.Ordinal) && int.TryParse(row.Key.AsSpan(6), out var number)
            ? _hosting.ShowIssueAsync(number)
            : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshHostingAsync() => _hosting?.RefreshAsync() ?? Task.CompletedTask;

    /// <summary>"Nouvelle issue…" of the issue section (J6.10c).</summary>
    [RelayCommand]
    private Task NewIssueAsync() => _hosting?.CreateIssueAsync() ?? Task.CompletedTask;

    /// <summary>"Créer une pull request…" of a local branch.</summary>
    [RelayCommand]
    private Task CreatePullRequestAsync(RefRow row) =>
        row is { Kind: RefRowKind.LocalBranch, Ref: { } branch } && _hosting is not null ? _hosting.CreatePullRequestCommand.ExecuteAsync(branch) : Task.CompletedTask;

    /// <summary>"Nouvelle branche ici…": on the commit of the row; HEAD for the detached HEAD row; selected commit or HEAD otherwise.</summary>
    [RelayCommand]
    private Task NewBranchAsync(RefRow row) => RefActions.CreateBranchCommand.ExecuteAsync(TargetOf(row));

    [RelayCommand]
    private Task NewTagAsync(RefRow row) => RefActions.CreateTagCommand.ExecuteAsync(TargetOf(row));

    private GitSha? TargetOf(RefRow row) => row.Kind == RefRowKind.DetachedHead ? _graph.Head : row.Ref?.Target;

    /// <summary>Push entry: a local branch to its upstream (or published), a tag to the default remote.</summary>
    [RelayCommand]
    private Task PushAsync(RefRow row) => row switch
    {
        { Kind: RefRowKind.LocalBranch, Ref: { } branch } => RefActions.PushBranchCommand.ExecuteAsync(branch),
        { Kind: RefRowKind.Tag, Ref: { } tag } => RefActions.PushTagCommand.ExecuteAsync(tag),
        _ => Task.CompletedTask,
    };

    /// <summary>F2: renames a local branch, edits a remote.</summary>
    [RelayCommand]
    private Task RenameAsync(RefRow row) => row switch
    {
        { Kind: RefRowKind.LocalBranch, Ref: { } branch } => RefActions.RenameBranchCommand.ExecuteAsync(branch),
        { Kind: RefRowKind.Remote } => RefActions.EditRemoteCommand.ExecuteAsync(row.Label),
        _ => Task.CompletedTask,
    };

    /// <summary>Delete key: deletes a local branch (not the checked-out one), a remote branch on its remote, a tag, a remote.</summary>
    [RelayCommand]
    private Task DeleteAsync(RefRow row) => row switch
    {
        { Kind: RefRowKind.LocalBranch, Ref: { IsCurrent: false } branch } => RefActions.DeleteBranchCommand.ExecuteAsync(branch),
        { Kind: RefRowKind.RemoteBranch, Ref: { } remoteBranch } => RefActions.DeleteRemoteBranchCommand.ExecuteAsync(remoteBranch),
        { Kind: RefRowKind.Tag, Ref: { } tag } => RefActions.DeleteTagCommand.ExecuteAsync(tag),
        { Kind: RefRowKind.Remote } => RefActions.RemoveRemoteCommand.ExecuteAsync(row.Label),
        { Kind: RefRowKind.Stash, Ref: { } stash } when _stash is not null => _stash.DeleteCommand.ExecuteAsync(stash),
        { Kind: RefRowKind.Worktree, Worktree: { } worktree } when _worktrees is not null => _worktrees.RemoveCommand.ExecuteAsync(worktree),
        { Kind: RefRowKind.LfsPattern, LfsPattern: { } pattern } when _lfs is not null => _lfs.UntrackCommand.ExecuteAsync(pattern),
        _ => Task.CompletedTask,
    };

    /// <summary>"Nouveau worktree…" of the section (new branch) or "Ouvrir dans un nouveau worktree…" of a local branch.</summary>
    [RelayCommand]
    private Task AddWorktreeAsync(RefRow row) => _worktrees is null
        ? Task.CompletedTask
        : row is { Kind: RefRowKind.LocalBranch, Ref: { } branch }
            ? _worktrees.AddForBranchCommand.ExecuteAsync(branch)
            : _worktrees.AddCommand.ExecuteAsync(null);

    /// <summary>Double click, Enter or menu on a worktree row: opens it in a tab of its own.</summary>
    [RelayCommand]
    private Task OpenWorktreeAsync(RefRow row) =>
        row is { Kind: RefRowKind.Worktree, Worktree: { } worktree } && _worktrees is not null
            ? _worktrees.OpenCommand.ExecuteAsync(worktree)
            : Task.CompletedTask;

    [RelayCommand]
    private Task RemoveWorktreeAsync(RefRow row) =>
        row is { Kind: RefRowKind.Worktree, Worktree: { } worktree } && _worktrees is not null
            ? _worktrees.RemoveCommand.ExecuteAsync(worktree)
            : Task.CompletedTask;

    [RelayCommand]
    private Task PruneWorktreesAsync() => _worktrees?.PruneCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    /// <summary>Double click or Enter on a submodule row: opens it in a tab; a submodule not cloned yet is initialized instead.</summary>
    [RelayCommand]
    private Task OpenSubmoduleAsync(RefRow row) => row is { Kind: RefRowKind.Submodule, Submodule: { } submodule } && _submodules is not null
        ? submodule.IsInitialized ? _submodules.OpenCommand.ExecuteAsync(submodule) : _submodules.UpdateCommand.ExecuteAsync(submodule)
        : Task.CompletedTask;

    [RelayCommand]
    private Task UpdateSubmoduleAsync(RefRow row) =>
        row is { Kind: RefRowKind.Submodule, Submodule: { } submodule } && _submodules is not null
            ? _submodules.UpdateCommand.ExecuteAsync(submodule)
            : Task.CompletedTask;

    [RelayCommand]
    private Task UpdateAllSubmodulesAsync() => _submodules?.UpdateAllCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task AddSubmoduleAsync() => _submodules?.AddCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task DeinitSubmoduleAsync(RefRow row) =>
        row is { Kind: RefRowKind.Submodule, Submodule: { } submodule } && _submodules is not null
            ? _submodules.DeinitCommand.ExecuteAsync(submodule)
            : Task.CompletedTask;

    [RelayCommand]
    private Task TrackLfsAsync() => _lfs?.TrackCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task UntrackLfsAsync(RefRow row) =>
        row is { Kind: RefRowKind.LfsPattern, LfsPattern: { } pattern } && _lfs is not null
            ? _lfs.UntrackCommand.ExecuteAsync(pattern)
            : Task.CompletedTask;

    [RelayCommand]
    private Task PullLfsAsync() => _lfs?.PullCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task InitGitFlowAsync() => _gitFlow?.InitializeCommand.ExecuteAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task StartGitFlowAsync(GitFlowKind kind) => _gitFlow?.StartCommand.ExecuteAsync(kind) ?? Task.CompletedTask;

    [RelayCommand]
    private Task FinishGitFlowAsync(RefRow row) =>
        row is { Kind: RefRowKind.FlowBranch, Ref: { } branch } && _gitFlow is not null
            ? _gitFlow.FinishCommand.ExecuteAsync(branch)
            : Task.CompletedTask;

    /// <summary>"Appliquer" of a stash row: its changes come back, the stash stays.</summary>
    [RelayCommand]
    private Task ApplyStashAsync(RefRow row) =>
        row is { Kind: RefRowKind.Stash, Ref: { } stash } && _stash is not null ? _stash.ApplyCommand.ExecuteAsync(stash) : Task.CompletedTask;

    /// <summary>"Pop" of a stash row: its changes come back and it is deleted (kept on conflicts).</summary>
    [RelayCommand]
    private Task PopStashAsync(RefRow row) =>
        row is { Kind: RefRowKind.Stash, Ref: { } stash } && _stash is not null ? _stash.PopCommand.ExecuteAsync(stash) : Task.CompletedTask;

    /// <summary>
    /// Checkout of a branch or tag picked elsewhere (command palette), as from the panel: a remote branch goes to the
    /// local branch of the same name.
    /// </summary>
    public async Task CheckoutRefAsync(GitRef reference)
    {
        if (_stash is null || ToCheckoutTarget(reference, _graph.Refs) is not { } target) return;
        var name = target.Value switch
        {
            CheckoutBranch b => b.Name,
            CheckoutRemoteBranch r => r.LocalName,
            _ => reference.Name,
        };
        await _stash.CheckoutAsync(target, name);
    }

    /// <summary>What checking out <paramref name="row"/> does; null when there is nothing to do (current branch, stash…).</summary>
    internal static CheckoutTarget? ToCheckoutTarget(RefRow row, RepositoryRefs? refs) =>
        row.Kind is RefRowKind.LocalBranch or RefRowKind.RemoteBranch or RefRowKind.Tag or RefRowKind.PullRequest or RefRowKind.FlowBranch && row.Ref is { } reference
            ? ToCheckoutTarget(reference, refs)
            : null;

    /// <summary>What checking out <paramref name="reference"/> does; null when there is nothing to do (current branch, stash…).</summary>
    public static CheckoutTarget? ToCheckoutTarget(GitRef reference, RepositoryRefs? refs)
    {
        switch (reference.Kind)
        {
            case GitRefKind.LocalBranch when reference is { IsCurrent: false } branch:
                return new CheckoutBranch(branch.Name);

            case GitRefKind.RemoteBranch when reference is { Remote: { } remote } branch:
                var localName = branch.Name.StartsWith(remote + "/", StringComparison.Ordinal) ? branch.Name[(remote.Length + 1)..] : branch.Name;
                var local = refs?.OfKind(GitRefKind.LocalBranch).FirstOrDefault(b => b.Name == localName);
                if (local is { IsCurrent: true }) return null;
                // An existing local branch of that name is checked out as is (git would refuse to create it again).
                return local is null ? new CheckoutRemoteBranch(branch.Name, localName) : new CheckoutBranch(localName);

            case GitRefKind.Tag:
                return new CheckoutCommit(reference.Target);

            default:
                return null;
        }
    }

    public void Dispose()
    {
        _graph.RefsChanged -= OnGraphRefsChanged;
        _graph.PropertyChanged -= OnGraphPropertyChanged;
        _graph.Hidden.Changed -= OnHiddenChanged;
        RefActions.RemotesChanged -= OnRemotesChanged;
        if (_hosting is not null) _hosting.Changed -= OnHostingChanged;
        if (_worktrees is not null) _worktrees.Changed -= OnWorktreesChanged;
        if (_submodules is not null) _submodules.Changed -= OnSubmodulesChanged;
        if (_lfs is not null) _lfs.Changed -= OnLfsChanged;
        if (_gitFlow is not null) _gitFlow.Changed -= OnGitFlowChanged;
    }

    private void OnGitFlowChanged(object? sender, EventArgs e) => Rebuild();

    private void OnWorktreesChanged(object? sender, EventArgs e) => Rebuild();

    private void OnSubmodulesChanged(object? sender, EventArgs e) => Rebuild();

    private void OnLfsChanged(object? sender, EventArgs e) => Rebuild();

    private void OnGraphRefsChanged(object? sender, EventArgs e)
    {
        Rebuild();
        // Remote URLs (host shown next to each remote); remotes without branches are listed too.
        var remotes = RefActions.Remotes;
        var unknownRemote = _graph.Refs?.OfKind(GitRefKind.RemoteBranch).Any(r => r.Remote is { } name && remotes.All(x => x.Name != name)) == true;
        if (!_remotesRequested || unknownRemote)
        {
            _remotesRequested = true;
            _ = RefActions.LoadRemotesAsync();
        }
    }

    private void OnRemotesChanged(object? sender, EventArgs e) => Rebuild();

    private void OnHostingChanged(object? sender, EventArgs e) => Rebuild();

    // Lanes are known once the history is loaded: branch icons take the color of their lane.
    private void OnGraphPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphViewModel.IsLoading) && !_graph.IsLoading) Rebuild();
    }

    // Always called on the UI thread in the application; the lock only keeps two rebuilds from interleaving when there
    // is no UI thread to serialize them (tests, where the graph and the remotes load on pool threads).
    private readonly object _rebuildLock = new();

    private void Rebuild()
    {
        lock (_rebuildLock)
        {
            var rows = RefRowBuilder.BuildRows(_graph.Refs, RefActions.Remotes, FilterText.Trim(), _collapsed, sha => _graph.GetLane(sha), _hosting?.Snapshot,
                _worktrees?.Worktrees, _worktrees is null ? null : _worktrees.IsCurrent, _submodules?.Submodules, _lfs?.Info, _gitFlow?.Config, _graph.Hidden);
            HasNoMatch = FilterText.Trim().Length > 0 && rows.All(r => r.IsSection);
            RefRowBuilder.SyncRows(Rows, rows, ToggleHidden);
        }
    }

    /// <summary>Menu entry deleting a stash: named by its subject (the index <c>stash@{n}</c> tells nothing), by its index when it has none.</summary>
    public static string DeleteStashLabel(GitRef stash)
    {
        const int MaxLength = 40;
        var subject = StashSubject(stash.Message).Trim();
        if (subject.Length == 0) return Loc.Format("Refs.Menu.DeleteStash", stash.Name);
        return Loc.Format("Refs.Menu.DeleteStashNamed", subject.Length > MaxLength ? subject[..(MaxLength - 1)] + "…" : subject);
    }

    /// <summary>"On main: try paging" / "WIP on main: abc1234 msg" → the part after the branch.</summary>
    internal static string StashSubject(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && (message.StartsWith("On ", StringComparison.Ordinal) || message.StartsWith("WIP on ", StringComparison.Ordinal))
            ? message[(colon + 2)..]
            : message;
    }
}
