using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Controls;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Text;

namespace Vigo.App.Views;

public sealed partial class RefsPanel : UserControl
{
    private RefRow? _menuRow;

    public RefsPanel() => InitializeComponent();

    /// <summary>Set once by the repository view, right after its InitializeComponent.</summary>
    public RefsPanelViewModel ViewModel
    {
        get;
        set
        {
            field = value;
            // The x:Bind bindings were evaluated with a null view model during InitializeComponent.
            Bindings.Update();
        }
    } = null!;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Thickness PaddingOf(double indent) => new(indent, 0, 8, 0);

    public static string ChevronOf(bool expanded) => expanded ? FluentIcons.ChevronDown : FluentIcons.ChevronRight;

    /// <summary>Mockup icons: desktop / cloud / tag / archive for sections, square check for the checked-out branch.</summary>
    public static string IconOf(RefRowKind kind, RefSection section, bool isHead, bool expanded, Worktree? worktree, string? itemType = null) => kind switch
    {
        RefRowKind.Section => section switch
        {
            RefSection.Local => FluentIcons.Desktop,
            RefSection.Remote => FluentIcons.Cloud,
            RefSection.Tags => FluentIcons.Tag,
            RefSection.PullRequests => FluentIcons.BranchRequest,
            RefSection.Issues => FluentIcons.Bug,
            RefSection.Worktrees => FluentIcons.TreeEvergreen,
            RefSection.Submodules => FluentIcons.Cube,
            RefSection.Lfs => FluentIcons.Document,
            RefSection.GitFlow => FluentIcons.BranchFork,
            _ => FluentIcons.Archive,
        },
        RefRowKind.FlowBranch when isHead => FluentIcons.CheckboxChecked,
        RefRowKind.FlowBranch => FluentIcons.BranchFork,
        RefRowKind.Submodule => FluentIcons.Cube,
        RefRowKind.LfsPattern => FluentIcons.Document,
        RefRowKind.Worktree => worktree is { IsMain: true } ? FluentIcons.Home : FluentIcons.TreeEvergreen,
        RefRowKind.Folder => expanded ? FluentIcons.FolderOpen : FluentIcons.Folder,
        RefRowKind.Remote => FluentIcons.Cloud,
        RefRowKind.Tag => FluentIcons.Tag,
        RefRowKind.Stash => FluentIcons.Archive,
        RefRowKind.PullRequest => FluentIcons.BranchRequest,
        RefRowKind.Issue => WorkItemIcon(itemType),
        RefRowKind.Hint => string.Empty,
        RefRowKind.LocalBranch or RefRowKind.DetachedHead when isHead => FluentIcons.CheckboxChecked,
        _ => FluentIcons.Branch,
    };

    /// <summary>Icon of an issue row: by Azure DevOps work item type (processes Agile, Scrum, Basic, CMMI); the bug for GitHub issues.</summary>
    private static string WorkItemIcon(string? type) => type?.ToLowerInvariant() switch
    {
        null or "" => FluentIcons.Bug,
        "bug" => FluentIcons.Bug,
        "epic" => FluentIcons.Crown,
        "feature" => FluentIcons.Trophy,
        "user story" or "product backlog item" or "requirement" => FluentIcons.Book,
        "task" => FluentIcons.ClipboardTask,
        "issue" or "impediment" or "risk" or "change request" => FluentIcons.Flag,
        _ => FluentIcons.TaskListLtr,
    };

    public static Visibility LaneIcon(int? colorIndex) => colorIndex is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility NeutralIcon(int? colorIndex) => colorIndex is null ? Visibility.Visible : Visibility.Collapsed;

    public static SolidColorBrush LaneBrush(int? colorIndex) => LaneBrushes.For(colorIndex ?? 0);

    /// <summary>Background of the checked-out branch row: its lane color, tinted; none for the other rows.</summary>
    public static Brush? TintOf(bool isHead, int? colorIndex) => isHead ? LaneBrushes.SoftFor(colorIndex ?? 0) : null;

    /// <summary>
    /// Tint of a row, set on the root panel of the row template and drawn by its first child (a border). Shown only
    /// while the row is neither hovered nor selected, so that the standard hover and selection backgrounds of the list
    /// item win. The list item container cannot carry it: its presenter reads the container Background once, when the
    /// template is applied, and ignores later local values.
    /// </summary>
    public static readonly DependencyProperty RowTintProperty = DependencyProperty.RegisterAttached(
        "RowTint", typeof(Brush), typeof(RefsPanel), new PropertyMetadata(null, OnRowTintChanged));

    public static Brush? GetRowTint(DependencyObject element) => (Brush?)element.GetValue(RowTintProperty);

    public static void SetRowTint(DependencyObject element, Brush? value) => element.SetValue(RowTintProperty, value);

    /// <summary>Pointer over a list item container (hover state of its row tint).</summary>
    private static readonly DependencyProperty IsRowHoveredProperty = DependencyProperty.RegisterAttached(
        "IsRowHovered", typeof(bool), typeof(RefsPanel), new PropertyMetadata(false));

    /// <summary>List item container a branch is dragged over (mockup: accent background and outline).</summary>
    private static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsDropTarget", typeof(bool), typeof(RefsPanel), new PropertyMetadata(false));

    /// <summary>List item container whose hover and selection changes already update its row tint.</summary>
    private static readonly DependencyProperty IsRowTintTrackedProperty = DependencyProperty.RegisterAttached(
        "IsRowTintTracked", typeof(bool), typeof(RefsPanel), new PropertyMetadata(false));

    private static void OnRowTintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        // Set by x:Bind before the template is in the tree the first time: applied again once loaded.
        element.Loaded -= OnRowTintLoaded;
        element.Loaded += OnRowTintLoaded;
        if (ContainerOf(element) is { } container) TrackRowTint(container);
    }

    private static void OnRowTintLoaded(object sender, RoutedEventArgs e)
    {
        if (ContainerOf((FrameworkElement)sender) is { } container) TrackRowTint(container);
    }

    private static void TrackRowTint(ListViewItem container)
    {
        if (!(bool)container.GetValue(IsRowTintTrackedProperty))
        {
            container.SetValue(IsRowTintTrackedProperty, true);
            container.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => SetRowHovered(container, true)), true);
            PointerEventHandler leave = (_, _) => SetRowHovered(container, false);
            container.AddHandler(PointerExitedEvent, leave, true);
            container.AddHandler(PointerCanceledEvent, leave, true);
            container.AddHandler(PointerCaptureLostEvent, leave, true);
            container.RegisterPropertyChangedCallback(SelectorItem.IsSelectedProperty, (_, _) => UpdateRowTint(container));
        }
        UpdateRowTint(container);
    }

    /// <summary>
    /// List item container of a row template. Its item presenter in between is not tested by type: under AOT, the
    /// visual tree returns it as a plain FrameworkElement.
    /// </summary>
    private static ListViewItem? ContainerOf(FrameworkElement element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            switch (parent)
            {
                case ListViewItem container: return container;
                case ListView: return null;
            }
        }
        return null;
    }

    private static void SetRowHovered(ListViewItem container, bool hovered)
    {
        container.SetValue(IsRowHoveredProperty, hovered);
        UpdateRowTint(container);
    }

    private static void UpdateRowTint(ListViewItem container)
    {
        if (container.ContentTemplateRoot is not Panel { Children: [Border border, ..] } root) return;
        if ((bool)container.GetValue(IsDropTargetProperty))
        {
            // Colors from DesignTokens: a brush read from the resources cannot be cast under AOT (see CLAUDE.md).
            var light = container.ActualTheme == ElementTheme.Light;
            border.Background = new SolidColorBrush(light ? DesignTokens.Light.AccentSoft2 : DesignTokens.Dark.AccentSoft2);
            border.BorderBrush = new SolidColorBrush(light ? DesignTokens.Light.Accent : DesignTokens.Dark.Accent);
            border.BorderThickness = new Thickness(1);
            border.Visibility = Visibility.Visible;
            return;
        }
        border.BorderThickness = default;
        var tint = GetRowTint(root);
        border.Background = tint;
        border.Visibility = tint is not null && !container.IsSelected && !(bool)container.GetValue(IsRowHoveredProperty)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Checked-out branch and remotes in semibold (mockup).</summary>
    public static FontWeight LabelWeight(bool isHead, RefRowKind kind) =>
        isHead || kind == RefRowKind.Remote ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>Hint lines (loading, sign-in needed, nothing open) and refs hidden from the graph are dimmed.</summary>
    public static double OpacityOf(RefRowKind kind, bool isHidden) => isHidden ? 0.5 : kind == RefRowKind.Hint ? 0.7 : 1;

    /// <summary>The eye shows while the row is hovered, and stays once the ref is hidden.</summary>
    public static double EyeOpacity(bool isHovered, bool isHidden) => isHovered || isHidden ? 1 : 0;

    /// <summary>An invisible eye does not take clicks.</summary>
    public static bool EyeActive(bool isHovered, bool isHidden) => isHovered || isHidden;

    public static string EyeGlyph(bool isHidden) => isHidden ? FluentIcons.EyeOff : FluentIcons.Eye;

    public static string EyeToolTip(bool isHidden) => Loc.Get(isHidden ? "Refs.Eye.Show" : "Refs.Eye.Hide");

    private static readonly DependencyProperty IsEyeTrackedProperty = DependencyProperty.RegisterAttached(
        "IsEyeTracked", typeof(bool), typeof(RefsPanel), new PropertyMetadata(false));

    /// <summary>Follows the pointer over a row container once, to show the eye of its row (<see cref="RefRowItem.IsHovered"/>).</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var container = args.ItemContainer;
        if ((bool)container.GetValue(IsEyeTrackedProperty)) return;
        container.SetValue(IsEyeTrackedProperty, true);
        void Set(bool hovered)
        {
            if (RefList.ItemFromContainer(container) is RefRowItem item) item.IsHovered = hovered;
        }
        container.AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => Set(true)), true);
        PointerEventHandler leave = (_, _) => Set(false);
        container.AddHandler(PointerExitedEvent, leave, true);
        container.AddHandler(PointerCanceledEvent, leave, true);
        container.AddHandler(PointerCaptureLostEvent, leave, true);
    }

    public static string? ToolTipOf(string toolTip) => toolTip.Length == 0 ? null : toolTip;

    /// <summary>Row of the element an event came from: inside a row template, or the list item container itself.</summary>
    private RefRow? RowOf(object? source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            switch (element)
            {
                case ListViewItem container:
                    return (RefList.ItemFromContainer(container) as RefRowItem)?.Row;
                case FrameworkElement { DataContext: RefRowItem { Row: var row } }:
                    return row;
                case ListView:
                    return null;
            }
        }
        return null;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RefRowItem { Row: var row }) ViewModel.ActivateCommand.Execute(row);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowOf(e.OriginalSource) is { IsCheckoutable: true } row) ViewModel.CheckoutCommand.Execute(row);
        else if (RowOf(e.OriginalSource) is { Kind: RefRowKind.PullRequest } pull) ViewModel.ShowPullRequestCommand.Execute(pull);
        else if (RowOf(e.OriginalSource) is { Kind: RefRowKind.Issue } issue) ViewModel.ShowIssueCommand.Execute(issue);
        else if (RowOf(e.OriginalSource) is { IsOpenable: true, Url: { } url }) ViewModel.OpenUrlCommand.Execute(url);
        else if (RowOf(e.OriginalSource) is { Kind: RefRowKind.Worktree } worktree) ViewModel.OpenWorktreeCommand.Execute(worktree);
        else if (RowOf(e.OriginalSource) is { Kind: RefRowKind.Submodule } submodule) ViewModel.OpenSubmoduleCommand.Execute(submodule);
    }

    /// <summary>Enter checks out (Space selects, through ItemClick), F2 renames, Delete deletes (after confirmation).</summary>
    private void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (RowOf(e.OriginalSource) is not { } row) return;
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                if (row.IsCheckoutable) ViewModel.CheckoutCommand.Execute(row);
                else if (row.Kind == RefRowKind.PullRequest) ViewModel.ShowPullRequestCommand.Execute(row);
                else if (row.Kind == RefRowKind.Issue) ViewModel.ShowIssueCommand.Execute(row);
                else if (row is { IsOpenable: true, Url: { } url }) ViewModel.OpenUrlCommand.Execute(url);
                else if (row.Kind == RefRowKind.Worktree) ViewModel.OpenWorktreeCommand.Execute(row);
                else if (row.Kind == RefRowKind.Submodule) ViewModel.OpenSubmoduleCommand.Execute(row);
                else ViewModel.ActivateCommand.Execute(row);
                break;
            case VirtualKey.F2:
                e.Handled = true;
                ViewModel.RenameCommand.Execute(row);
                break;
            case VirtualKey.Delete:
                e.Handled = true;
                ViewModel.DeleteCommand.Execute(row);
                break;
        }
    }

    /// <summary>Escape clears the filter, Down goes to the list.</summary>
    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.FilterText.Length > 0)
        {
            ViewModel.FilterText = string.Empty;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down && RefList.Items.Count > 0)
        {
            RefList.SelectedIndex = Math.Max(RefList.SelectedIndex, 0);
            (RefList.ContainerFromIndex(RefList.SelectedIndex) as Control)?.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (RowOf(e.OriginalSource) is not { } row || ViewModel.MenuFor(row) is not { } menu || e.OriginalSource is not FrameworkElement target) return;
        e.Handled = true;

        _menuRow = row;
        _menuCopyText = menu.CopyText;
        _menuMerge = menu.Merge;
        _menuRebase = menu.Rebase;
        CheckoutItem.Text = menu.CheckoutLabel ?? string.Empty;
        MergeItem.Text = menu.Merge?.Label ?? string.Empty;
        RebaseItem.Text = menu.Rebase?.Label ?? string.Empty;
        InteractiveRebaseItem.Text = menu.Rebase?.InteractiveLabel ?? string.Empty;
        PushItem.Text = menu.PushLabel ?? string.Empty;
        DeleteItem.Text = menu.DeleteLabel ?? string.Empty;
        DeleteItem.IsEnabled = menu.CanDelete;
        OpenItem.Text = menu.OpenLabel ?? string.Empty;
        CopyItem.Text = menu.CopyLabel ?? Loc.Get("Refs.Menu.CopyName");
        AddWorktreeItem.Text = row.Kind == RefRowKind.LocalBranch ? Loc.Get("Refs.Menu.OpenInNewWorktree") : Loc.Get("Refs.Menu.NewWorktree");
        UpdateSubmoduleItem.Text = menu.UpdateSubmoduleLabel ?? string.Empty;
        FinishGitFlowItem.Text = menu.FinishGitFlowLabel ?? string.Empty;
        _menuOpenUrl = menu.OpenUrl;

        // Groups in menu order, each preceded by its separator: a separator shows only between two visible groups.
        FillIssueFilterMenu(menu.IssueFilters);
        (MenuFlyoutSeparator? Separator, (MenuFlyoutItemBase Item, bool Visible)[] Items)[] groups =
        [
            (null, [(CheckoutItem, menu.CheckoutLabel is not null), (MergeItem, menu.Merge is not null), (RebaseItem, menu.Rebase is not null),
                (InteractiveRebaseItem, menu.Rebase is not null && Rebase is not null),
                (ApplyStashItem, menu.CanApplyStash), (PopStashItem, menu.CanApplyStash)]),
            (CreateSeparator, [(NewBranchItem, menu.CanCreateBranch), (NewTagItem, menu.CanCreateTag), (PushItem, menu.PushLabel is not null), (NewPullRequestItem, menu.CanCreatePullRequest)]),
            (HostingSeparator, [(ShowPullRequestItem, menu.CanShowPullRequest), (ShowIssueItem, menu.CanShowIssue), (NewIssueItem, menu.CanCreateIssue), (OpenItem, menu.OpenUrl is not null), (IssueFilterMenu, menu.IssueFilters is not null), (RefreshHostingItem, menu.CanRefreshHosting)]),
            (EditSeparator, [(RenameItem, menu.CanRename), (DeleteItem, menu.DeleteLabel is not null)]),
            (RemoteSeparator, [(AddRemoteItem, menu.CanAddRemote), (EditRemoteItem, menu.CanEditRemote), (RemoveRemoteItem, menu.CanRemoveRemote)]),
            (WorktreeSeparator, [(OpenWorktreeItem, menu.CanOpenWorktree), (AddWorktreeItem, menu.CanAddWorktree),
                (PruneWorktreesItem, menu.CanPruneWorktrees), (RemoveWorktreeItem, menu.CanRemoveWorktree)]),
            (SubmoduleSeparator, [(OpenSubmoduleItem, menu.CanOpenSubmodule), (UpdateSubmoduleItem, menu.UpdateSubmoduleLabel is not null),
                (UpdateAllSubmodulesItem, menu.CanUpdateAllSubmodules), (AddSubmoduleItem, menu.CanAddSubmodule),
                (DeinitSubmoduleItem, menu.CanDeinitSubmodule)]),
            (LfsSeparator, [(TrackLfsItem, menu.CanTrackLfs), (PullLfsItem, menu.CanPullLfs), (UntrackLfsItem, menu.CanUntrackLfs)]),
            (GitFlowSeparator, [(InitGitFlowItem, menu.CanInitGitFlow), (StartFeatureItem, menu.CanStartGitFlow), (StartReleaseItem, menu.CanStartGitFlow),
                (StartHotfixItem, menu.CanStartGitFlow), (FinishGitFlowItem, menu.FinishGitFlowLabel is not null)]),
            (CopySeparator, [(CopyItem, menu.CopyText is not null)]),
        ];
        var anyBefore = false;
        foreach (var (separator, items) in groups)
        {
            var groupVisible = false;
            foreach (var (item, visible) in items)
            {
                item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                groupVisible |= visible;
            }
            if (separator is not null) separator.Visibility = groupVisible && anyBefore ? Visibility.Visible : Visibility.Collapsed;
            anyBefore |= groupVisible;
        }

        if (e.TryGetPosition(target, out var point)) RefMenu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else RefMenu.ShowAt(target);
    }

    // "Filtrer" of the issue section (J6.10a): entries rebuilt at each opening, as the checks follow the active filters.
    private void FillIssueFilterMenu(IReadOnlyList<IssueFilterEntry>? entries)
    {
        IssueFilterMenu.Items.Clear();
        if (entries is null) return;

        foreach (var entry in entries)
        {
            if (entry.SeparatorBefore) IssueFilterMenu.Items.Add(new MenuFlyoutSeparator());
            var item = new ToggleMenuFlyoutItem { Text = entry.Label, IsChecked = entry.IsChecked, IsEnabled = entry.IsEnabled };
            item.Click += (_, _) => _ = entry.Run();
            IssueFilterMenu.Items.Add(item);
        }
    }

    private string? _menuCopyText;
    private string? _menuOpenUrl;
    private MergePlan? _menuMerge;
    private RebasePlan? _menuRebase;

    private void OnMergeClick(object sender, RoutedEventArgs e)
    {
        if (_menuMerge is { } plan) ViewModel.Merge.MergeCommand.Execute(plan);
    }

    private void OnRebaseClick(object sender, RoutedEventArgs e)
    {
        if (_menuRebase is { } plan) ViewModel.Merge.RebaseCommand.Execute(plan);
    }

    /// <summary>Interactive rebase of the checked-out branch on the row's branch (J5.5); set once by the repository view.</summary>
    public InteractiveRebaseViewModel? Rebase { get; set; }

    /// <summary>Pull request of the drop menu (J6.2); set once by the repository view.</summary>
    public HostingPanelViewModel? Hosting { get; set; }

    private RepositorySummaryViewModel? _summary;
    private RepositoryStatsViewModel? _stats;

    /// <summary>
    /// Keeps a single selection in the panel: the open page's entry (same list item style as a ref) or a ref, never both. Picking a ref
    /// closes the open page (set once by the repository view).
    /// </summary>
    public void TrackPages(RepositorySummaryViewModel summary, RepositoryStatsViewModel stats)
    {
        _summary = summary;
        _stats = stats;
        summary.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RepositorySummaryViewModel.IsOpen)) UpdatePageSelection(); };
        stats.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RepositoryStatsViewModel.IsOpen)) UpdatePageSelection(); };
        UpdatePageSelection();
    }

    private void UpdatePageSelection()
    {
        if (_summary is null || _stats is null) return;
        PagesList.SelectedIndex = _summary.IsOpen ? 0 : _stats.IsOpen ? 1 : -1;
        if (_summary.IsOpen || _stats.IsOpen) RefList.SelectedItem = null;
    }

    private void OnRefSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RefList.SelectedItem is null) return;
        _summary?.Close();
        _stats?.Close();
    }

    /// <summary>Opens the summary page in place of the graph (J7.13); set once by the repository view.</summary>
    public Func<Task>? SummaryRequested { get; set; }

    /// <summary>Opens the statistics page in place of the graph (J7.14); set once by the repository view.</summary>
    public Func<Task>? StatsRequested { get; set; }

    // The user picked an entry (index 0 = summary, 1 = statistics): open its page. The selection set by UpdatePageSelection
    // finds the page already open and does nothing.
    private async void OnPagesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_summary is null || _stats is null) return;
        switch (PagesList.SelectedIndex)
        {
            case 0 when !_summary.IsOpen && SummaryRequested is { } summary:
                await summary();
                break;
            case 1 when !_stats.IsOpen && StatsRequested is { } stats:
                await stats();
                break;
        }
    }

    private void OnInteractiveRebaseClick(object sender, RoutedEventArgs e)
    {
        if (_menuRebase is { } plan) _ = Rebase?.OpenAsync(plan.Upstream.CanonicalName, plan.Upstream.Name, plan.Upstream.Target);
    }

    private void OnApplyStashClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.ApplyStashCommand.Execute(row);
    }

    private void OnPopStashClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.PopStashCommand.Execute(row);
    }

    // ---- Drag and drop of a branch on another one (J4.3) -------------------------------------------------------------

    private ListViewItem? _dropContainer;

    /// <summary>A local or remote branch can be dragged; other rows cannot (sections, tags, stashes…).</summary>
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 1 && e.Items[0] is RefRowItem { Row.Ref: { Kind: GitRefKind.LocalBranch or GitRefKind.RemoteBranch } reference }
            && ViewModel.Merge.CanStart)
        {
            RefDragDrop.Start(e.Data, reference);
        }
        else
        {
            e.Cancel = true;
        }
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        SetDropContainer(null);
        RefDragDrop.End();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var target = DropTargetAt(e, out var container);
        SetDropContainer(target is null ? null : container);
        RefDragDrop.AcceptOver(e, target);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => SetDropContainer(null);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var target = DropTargetAt(e, out _);
        SetDropContainer(null);
        if (RefDragDrop.SourceFor(target) is not { } source) return;
        var position = e.GetPosition(RefList);
        if (await ViewModel.Merge.DropMenuAsync(source, target!) is { } menu) RefDragDrop.ShowMenu(RefList, position, menu, ViewModel.Merge, Rebase, Hosting);
    }

    /// <summary>
    /// Local branch row under the pointer that the dragged branch can be dropped on, and its container. Found by
    /// position: during a drag the original source of the events is the list itself, not the row.
    /// </summary>
    private GitRef? DropTargetAt(DragEventArgs e, out ListViewItem? container)
    {
        container = null;
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), RefList))
        {
            if (element is ListViewItem item)
            {
                container = item;
                break;
            }
        }
        var target = container is not null && RowOf(container) is { Kind: RefRowKind.LocalBranch, Ref: { } reference } ? reference : null;
        return RefDragDrop.SourceFor(target) is null ? null : target;
    }

    private void SetDropContainer(ListViewItem? container)
    {
        if (container == _dropContainer) return;
        if (_dropContainer is { } previous)
        {
            previous.SetValue(IsDropTargetProperty, false);
            UpdateRowTint(previous);
        }
        _dropContainer = container;
        if (container is not null)
        {
            container.SetValue(IsDropTargetProperty, true);
            UpdateRowTint(container);
        }
    }

    private void OnCheckoutClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.CheckoutCommand.Execute(row);
    }

    private void OnNewBranchClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.NewBranchCommand.Execute(row);
    }

    private void OnNewPullRequestClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.CreatePullRequestCommand.Execute(row);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_menuOpenUrl is { } url) ViewModel.OpenUrlCommand.Execute(url);
    }

    private void OnShowPullRequestClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.ShowPullRequestCommand.Execute(row);
    }

    private void OnShowIssueClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.ShowIssueCommand.Execute(row);
    }

    private void OnRefreshHostingClick(object sender, RoutedEventArgs e) => ViewModel.RefreshHostingCommand.Execute(null);

    private void OnNewIssueClick(object sender, RoutedEventArgs e) => ViewModel.NewIssueCommand.Execute(null);

    private void OnNewTagClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.NewTagCommand.Execute(row);
    }

    private void OnPushClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.PushCommand.Execute(row);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.RenameCommand.Execute(row);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.DeleteCommand.Execute(row);
    }

    private void OnAddRemoteClick(object sender, RoutedEventArgs e) => ViewModel.RefActions.AddRemoteCommand.Execute(null);

    private void OnEditRemoteClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { Kind: RefRowKind.Remote } row) ViewModel.RenameCommand.Execute(row);
    }

    private void OnRemoveRemoteClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { Kind: RefRowKind.Remote } row) ViewModel.DeleteCommand.Execute(row);
    }

    private void OnOpenWorktreeClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.OpenWorktreeCommand.Execute(row);
    }

    private void OnAddWorktreeClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.AddWorktreeCommand.Execute(row);
    }

    private void OnPruneWorktreesClick(object sender, RoutedEventArgs e) => ViewModel.PruneWorktreesCommand.Execute(null);

    private void OnOpenSubmoduleClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.OpenSubmoduleCommand.Execute(row);
    }

    private void OnUpdateSubmoduleClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.UpdateSubmoduleCommand.Execute(row);
    }

    private void OnUpdateAllSubmodulesClick(object sender, RoutedEventArgs e) => ViewModel.UpdateAllSubmodulesCommand.Execute(null);

    private void OnAddSubmoduleClick(object sender, RoutedEventArgs e) => ViewModel.AddSubmoduleCommand.Execute(null);

    private void OnDeinitSubmoduleClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.DeinitSubmoduleCommand.Execute(row);
    }

    private void OnTrackLfsClick(object sender, RoutedEventArgs e) => ViewModel.TrackLfsCommand.Execute(null);

    private void OnPullLfsClick(object sender, RoutedEventArgs e) => ViewModel.PullLfsCommand.Execute(null);

    private void OnInitGitFlowClick(object sender, RoutedEventArgs e) => ViewModel.InitGitFlowCommand.Execute(null);

    private void OnStartFeatureClick(object sender, RoutedEventArgs e) => ViewModel.StartGitFlowCommand.Execute(GitFlowKind.Feature);

    private void OnStartReleaseClick(object sender, RoutedEventArgs e) => ViewModel.StartGitFlowCommand.Execute(GitFlowKind.Release);

    private void OnStartHotfixClick(object sender, RoutedEventArgs e) => ViewModel.StartGitFlowCommand.Execute(GitFlowKind.Hotfix);

    private void OnFinishGitFlowClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.FinishGitFlowCommand.Execute(row);
    }

    private void OnUntrackLfsClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.UntrackLfsCommand.Execute(row);
    }

    private void OnRemoveWorktreeClick(object sender, RoutedEventArgs e)
    {
        if (_menuRow is { } row) ViewModel.RemoveWorktreeCommand.Execute(row);
    }

    private void OnCopyNameClick(object sender, RoutedEventArgs e)
    {
        if (_menuCopyText is not { } text) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
