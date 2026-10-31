using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.App.Services;
using Vigo.Core.Services;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Views;

public sealed partial class RepositoryView : UserControl
{
    private readonly LayoutState _layout;
    private readonly Action<string> _openPalette;
    private double _dragStartWidth;
    private double _dragStartTerminalHeight;

    public RepositoryView(RepositoryTabViewModel viewModel, LayoutState layout, ShellViewModel shell, Action<string> openPalette)
    {
        ViewModel = viewModel;
        Shell = shell;
        _openPalette = openPalette;
        _layout = layout;
        InitializeComponent();
        AddShortcuts();
        GraphView.Layout = layout;
        layout.PropertyChanged += OnLayoutPropertyChanged;
        Body.SizeChanged += (_, _) => ApplyPanelWidths();
        ApplyPanelWidths();
        Root.SizeChanged += (_, _) => ApplyTerminalHeight();

        LeftSplitter.DragStarted += (_, _) => _dragStartWidth = LeftColumn.ActualWidth;
        LeftSplitter.DragDelta += (_, delta) =>
            layout.LeftPanelWidth = Math.Min(_dragStartWidth + delta, layout.MaxLeftPanelWidth(Body.ActualWidth));
        LeftSplitter.ResetRequested += (_, _) => layout.LeftPanelWidth = LayoutLimits.LeftPanel.Default;
        RightSplitter.DragStarted += (_, _) => _dragStartWidth = RightColumn.ActualWidth;
        RightSplitter.DragDelta += (_, delta) =>
            layout.RightPanelWidth = Math.Min(_dragStartWidth - delta, layout.MaxRightPanelWidth(Body.ActualWidth));
        RightSplitter.ResetRequested += (_, _) => layout.RightPanelWidth = LayoutLimits.RightPanel.Default;
        LeftSplitter.DragCompleted += (_, _) => _ = layout.SaveAsync();
        RightSplitter.DragCompleted += (_, _) => _ = layout.SaveAsync();

        // Integrated terminal (J7.10, ADR-071): panel height saved with the layout, resized from its top edge.
        TerminalPanel.ViewModel = viewModel.Terminal;
        TerminalSplitter.DragStarted += (_, _) => _dragStartTerminalHeight = TerminalPanel.ActualHeight;
        TerminalSplitter.DragDelta += (_, delta) => layout.TerminalHeight = _dragStartTerminalHeight - delta;
        TerminalSplitter.ResetRequested += (_, _) => layout.TerminalHeight = LayoutLimits.Terminal.Default;
        TerminalSplitter.DragCompleted += (_, _) => _ = layout.SaveAsync();
        ToolTipService.SetToolTip(TerminalButton, ShortcutMap.Current.WithHint("Terminal", ShortcutIds.Terminal));
        viewModel.Terminal.PropertyChanged += OnTerminalPropertyChanged;
        RefsPanel.ViewModel = viewModel.Refs;
        DetailsPanel.ViewModel = viewModel.Details;
        DetailsPanel.Layout = layout;
        WorkingTreePanel.ViewModel = viewModel.WorkingTree;
        WorkingTreePanel.Merge = viewModel.Merge;
        WorkingTreePanel.Conflicts = viewModel.Conflicts;
        WorkingTreePanel.Stash = viewModel.Stash;
        WorkingTreePanel.Patches = viewModel.Patches;
        WorkingTreePanel.Folders = viewModel.Folders;
        WorkingTreePanel.Layout = layout;
        ApplyFileListMode();
        GraphView.Graph = viewModel.Graph;
        GraphView.RefActions = viewModel.RefActions;
        GraphView.Merge = viewModel.Merge;
        GraphView.Refs = viewModel.Refs;
        GraphView.CheckoutAndPull = viewModel.TryCheckoutAndPull;
        GraphView.History = viewModel.History;
        GraphView.Patches = viewModel.Patches;
        GraphView.Stash = viewModel.Stash;
        DiffView.ViewModel = viewModel.Diff;
        ConflictView.ViewModel = viewModel.Conflicts;
        RebaseView.ViewModel = viewModel.Rebase;
        FileHistoryView.ViewModel = viewModel.FileHistory;
        BlameView.ViewModel = viewModel.Blame;
        PullRequestView.Layout = layout;
        PullRequestView.ViewModel = viewModel.PullRequest;
        IssueView.ViewModel = viewModel.Issue;
        WorkItemView.ViewModel = viewModel.WorkItem;
        SummaryView.ViewModel = viewModel.Summary;
        RefsPanel.SummaryRequested = viewModel.ShowSummaryAsync;
        StatsView.ViewModel = viewModel.Stats;
        RefsPanel.StatsRequested = viewModel.ShowStatsAsync;
        RefsPanel.TrackPages(viewModel.Summary, viewModel.Stats);
        GraphView.Rebase = viewModel.Rebase;
        RefsPanel.Rebase = viewModel.Rebase;
        GraphView.Hosting = viewModel.Hosting;
        RefsPanel.Hosting = viewModel.Hosting;
        ConflictView.AbortRequested += (_, _) => Run(viewModel.Merge.AbortCommand);
        viewModel.PropertyChanged += (_, e) =>
        {
            // Back from a diff or the conflict editor: the keyboard goes to the graph again (selection still on the same commit).
            if (e.PropertyName == nameof(RepositoryTabViewModel.ShowGraph) && viewModel.ShowGraph)
                DispatcherQueue.TryEnqueue(() => GraphView.Focus(FocusState.Programmatic));
            else if (e.PropertyName == nameof(RepositoryTabViewModel.ShowRightPanel))
                ApplyPanelWidths();
        };
        // Started from the UI thread: pages and statuses are applied on it while git.exe runs in the background.
        // Loaded again each time the tab is shown: the graph loads once, the status is refreshed.
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public RepositoryTabViewModel ViewModel { get; }

    /// <summary>Application commands of the toolbar (preferences).</summary>
    public ShellViewModel Shell { get; }

    private void OnPaletteClick(object sender, RoutedEventArgs e) => _openPalette(string.Empty);

    // ---- "Dépôt" selector: the list is read when it opens, the filter box has the focus.

    private void OnRepositoryFlyoutOpening(object? sender, object e) => Shell.RepositoryPicker.Refresh();

    private void OnRepositoryFlyoutOpened(object? sender, object e) => RepositoryFilter.Focus(FocusState.Programmatic);

    private async void OnRepositoryFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                RepositoryFlyout.Hide();
                await Shell.RepositoryPicker.ChooseFirstAsync();
                break;
            case VirtualKey.Down:
                e.Handled = true;
                RepositoryList.Focus(FocusState.Keyboard);
                break;
        }
    }

    private void OnRepositoryItemClick(object sender, ItemClickEventArgs e)
    {
        RepositoryFlyout.Hide();
        if (e.ClickedItem is RepositoryPickerItem item) Shell.RepositoryPicker.ChooseCommand.Execute(item);
    }

    private void OnRepositoryActionClick(object sender, RoutedEventArgs e) => RepositoryFlyout.Hide();

    private string RepositoryPath => ViewModel.Info.WorkingDirectory ?? ViewModel.Info.GitDirectory;

    private void OnCopyRepositoryPathClick(object sender, RoutedEventArgs e)
    {
        RepositoryFlyout.Hide();
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(RepositoryPath);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void OnShowRepositoryInExplorerClick(object sender, RoutedEventArgs e)
    {
        RepositoryFlyout.Hide();
        Explorer.Open($"\"{RepositoryPath}\"");
    }

    // ---- "Branche" selector: same pattern, a click checks out.

    private void OnBranchFlyoutOpening(object? sender, object e) => ViewModel.BranchPicker.Refresh();

    private void OnBranchFlyoutOpened(object? sender, object e) => BranchFilter.Focus(FocusState.Programmatic);

    private async void OnBranchFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                BranchFlyout.Hide();
                await ViewModel.BranchPicker.ChooseFirstAsync();
                break;
            case VirtualKey.Down:
                e.Handled = true;
                BranchList.Focus(FocusState.Keyboard);
                break;
        }
    }

    private void OnBranchItemClick(object sender, ItemClickEventArgs e)
    {
        BranchFlyout.Hide();
        if (e.ClickedItem is BranchPickerItem item) ViewModel.BranchPicker.ChooseCommand.Execute(item);
    }

    private void OnBranchActionClick(object sender, RoutedEventArgs e) => BranchFlyout.Hide();

    // ---- Conflict prevention (J7.11).

    private void OnPreventionActionClick(object sender, RoutedEventArgs e) => ConflictFlyout.Hide();

    // A file of the list: its history opens in place of the graph.
    private void OnPreventionFileClick(object sender, RoutedEventArgs e)
    {
        ConflictFlyout.Hide();
        if (sender is Button { Tag: string path }) ViewModel.ConflictPrevention.ShowHistoryCommand.Execute(path);
    }

    private void OnPreventionCopyClick(object sender, RoutedEventArgs e)
    {
        ConflictFlyout.Hide();
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ViewModel.ConflictPrevention.FilesText);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    // ---- Commit search: Escape clears the box and gives the keyboard back to the graph.

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                ViewModel.Search.Text = string.Empty;
                GraphView.Focus(FocusState.Programmatic);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                GraphView.Focus(FocusState.Programmatic);
                break;
        }
    }

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutState.LeftPanelWidth) or nameof(LayoutState.RightPanelWidth))
            ApplyPanelWidths();
        else if (e.PropertyName == nameof(LayoutState.TerminalHeight))
            ApplyTerminalHeight();
        else if (e.PropertyName == nameof(LayoutState.FileListAsTree))
            ApplyFileListMode();
    }

    private void OnTerminalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TerminalPanelViewModel.IsOpen)) return;
        ApplyTerminalHeight();
        // Hidden: the keyboard goes back to the graph (the terminal view takes it when shown, see TerminalPanel).
        if (!ViewModel.Terminal.IsOpen && ViewModel.ShowGraph) DispatcherQueue.TryEnqueue(() => GraphView.Focus(FocusState.Programmatic));
    }

    /// <summary>Saved terminal height while the panel is shown, reduced to leave room for the toolbar and the graph.</summary>
    private void ApplyTerminalHeight()
    {
        if (!ViewModel.Terminal.IsOpen)
        {
            TerminalRow.Height = new GridLength(0);
            return;
        }
        var available = Root.ActualHeight - Toolbar.ActualHeight;
        TerminalRow.Height = new GridLength(_layout.FitTerminal(available > 0 ? available : double.PositiveInfinity));
    }

    /// <summary>Path / Tree mode of the changed files lists, shared by every tab.</summary>
    private void ApplyFileListMode()
    {
        ViewModel.Details.ShowAsTree = _layout.FileListAsTree;
        ViewModel.WorkingTree.ShowAsTree = _layout.FileListAsTree;
        ViewModel.PullRequest.ShowAsTree = _layout.FileListAsTree;
    }

    /// <summary>Saved widths, reduced when the window is too narrow to leave room for the graph.</summary>
    private void ApplyPanelWidths()
    {
        var (left, right) = _layout.FitPanels(Body.ActualWidth > 0 ? Body.ActualWidth : double.PositiveInfinity);
        LeftColumn.Width = new GridLength(left);
        // The summary page (J7.13) takes the width of the right panel.
        RightColumn.Width = new GridLength(ViewModel.ShowRightPanel ? right : 0);
    }

    /// <summary>Shortcuts of the toolbar (mockup), active while this tab is shown; also reached from the code editor.</summary>
    private void AddShortcuts()
    {
        KeyboardShortcuts.AddCommand(this, ShortcutIds.Fetch, () => Run(ViewModel.Remote.FetchCommand));
        KeyboardShortcuts.AddCommand(this, ShortcutIds.Pull, () => Run(ViewModel.Remote.PullCommand));
        KeyboardShortcuts.AddCommand(this, ShortcutIds.Push, () => Run(ViewModel.Remote.PushCommand));
        KeyboardShortcuts.AddCommand(this, ShortcutIds.NewBranch, () => Run(ViewModel.RefActions.CreateBranchCommand));
        KeyboardShortcuts.AddCommand(this, ShortcutIds.Stash, () => Run(ViewModel.Stash.StashCommand));
        // A focused TextBox keeps its own Ctrl+Z / Ctrl+Y (built-in accelerators win over the app's): text undo still works.
        KeyboardShortcuts.AddCommand(this, ShortcutIds.Undo, () => Run(ViewModel.Undo.UndoCommand));
        KeyboardShortcuts.AddCommand(this, ShortcutIds.Redo, () => Run(ViewModel.Undo.RedoCommand));
        KeyboardShortcuts.AddCommand(this, ShortcutIds.Terminal, () => Run(ViewModel.Terminal.ToggleCommand));
    }

    private static bool Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
        return true;
    }

    /// <summary>Called when the tab is closed: releases Win2D resources.</summary>
    public void Release()
    {
        _layout.PropertyChanged -= OnLayoutPropertyChanged;
        ViewModel.Terminal.PropertyChanged -= OnTerminalPropertyChanged;
        TerminalPanel.Release();
        GraphView.Release();
        DiffView.Release();
        ConflictView.Release();
        RebaseView.Release();
        FileHistoryView.Release();
        BlameView.Release();
        PullRequestView.Release();
        IssueView.Release();
        WorkItemView.Release();
        SummaryView.Release();
        StatsView.Release();
    }
}
