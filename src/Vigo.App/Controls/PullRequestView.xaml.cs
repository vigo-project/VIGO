using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Controls;

/// <summary>
/// Pull request (J6.9a): header, conversation, commits, and the changed files with the read-only diff of the selected one
/// in the code editor. Échap goes back to the graph.
/// </summary>
public sealed partial class PullRequestView : UserControl
{
    private PullRequestViewModel? _viewModel;
    private bool _refreshQueued;
    private bool _syncingSelection;

    // What the editor shows, to send it again only when it changes.
    private object? _shown;
    private bool _shownNative;

    public PullRequestView()
    {
        InitializeComponent();
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            // Échap in a comment box must not throw the draft away.
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
            Close();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Editor.KeyPressed += OnEditorKey;
        Editor.EditorFailed += (_, _) => QueueRefresh();
        Editor.ReviewSubmitted += OnReviewSubmitted;
        Editor.ReviewReplied += OnReviewReplied;
        Editor.ReviewResolveRequested += OnReviewResolveRequested;
        Editor.ReviewDraftRemoved += (_, index) => _viewModel?.RemoveDraftAt(index);
        Editor.LinkClicked += OnEditorLinkClicked;
        Editor.ReferenceRequested += OnReferenceRequested;
        ActualThemeChanged += (_, _) => UpdateStateBadge();

        // The files list is resized from its right edge, like the panels of the repository view; the width is saved with the layout.
        FilesSplitter.DragStarted += (_, _) => _dragStartWidth = FilesColumn.ActualWidth;
        FilesSplitter.DragDelta += (_, delta) =>
        {
            if (_layout is { } layout) layout.PullRequestFilesWidth = Math.Min(_dragStartWidth + delta, MaxFilesWidth());
        };
        FilesSplitter.ResetRequested += (_, _) =>
        {
            if (_layout is { } layout) layout.PullRequestFilesWidth = LayoutLimits.PullRequestFiles.Default;
        };
        FilesSplitter.DragCompleted += (_, _) => _ = _layout?.SaveAsync();
    }

    private LayoutState? _layout;
    private double _dragStartWidth;


    private void OnToggleFolders(object? sender, bool collapse) =>
        (collapse ? ViewModel?.CollapseAllFoldersCommand : ViewModel?.ExpandAllFoldersCommand)?.Execute(null);

    /// <summary>Layout shared by every tab (width of the files list, Path / Tree mode); set once by the repository view.</summary>
    public LayoutState? Layout
    {
        get => _layout;
        set
        {
            if (_layout is not null) _layout.PropertyChanged -= OnLayoutPropertyChanged;
            _layout = value;
            if (_layout is not null) _layout.PropertyChanged += OnLayoutPropertyChanged;
            FilesModeSwitch.Layout = value;
            ApplyFilesWidth();
        }
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutState.PullRequestFilesWidth)) ApplyFilesWidth();
    }

    private double MaxFilesWidth() => Math.Max(LayoutLimits.PullRequestFiles.Minimum, FilesPanel.Width - LayoutLimits.PullRequestDiffMinimum);

    // The saved width, reduced when the view is too narrow to leave room for the diff (the saved one comes back when it grows).
    private void ApplyFilesWidth()
    {
        if (_layout is null) return;
        var max = FilesPanel.Width > 0 ? MaxFilesWidth() : double.PositiveInfinity;
        FilesColumn.Width = new GridLength(Math.Min(_layout.PullRequestFilesWidth, max));
    }

    public static Thickness IndentMargin(double indent) => new(indent, 0, 0, 0);

    /// <summary>Pull request shown by the control; set once by the repository view.</summary>
    public PullRequestViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.FileRows.CollectionChanged -= OnFileRowsChanged;
            }
            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                _viewModel.FileRows.CollectionChanged += OnFileRowsChanged;
            }
            _shown = null;
            Bindings.Update();
            UpdateStateBadge();
            QueueRefresh();
        }
    }

    /// <summary>Closes the editor page when the tab is closed for good.</summary>
    public void Release()
    {
        ViewModel = null;
        Editor.Release();
    }

    public static Visibility Inverse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    // Folds the list of checks under its header (the choice lasts as long as the view).
    private void OnChecksToggleClick(object sender, RoutedEventArgs e)
    {
        var fold = ChecksList.Visibility == Visibility.Visible;
        ChecksList.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
        ChecksChevron.Glyph = fold ? FluentIcons.ChevronRight : FluentIcons.ChevronDown;
    }

    public static Visibility HasItems(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PullRequestViewModel.IsOpen) when _viewModel!.IsOpen:
                QueueRefresh();
                break;
            case nameof(PullRequestViewModel.Detail) or nameof(PullRequestViewModel.Pull):
                UpdateStateBadge();
                break;
            case nameof(PullRequestViewModel.Diff) or nameof(PullRequestViewModel.Sides) or nameof(PullRequestViewModel.DiffMessage) or nameof(PullRequestViewModel.IsOpen)
                or nameof(PullRequestViewModel.SelectedTab):
                QueueRefresh();
                break;
            case nameof(PullRequestViewModel.ReviewRevision) or nameof(PullRequestViewModel.Detail):
                SendReview();
                break;
            case nameof(PullRequestViewModel.SelectedFile):
                // The list follows the model (first file selected when the pull request is read).
                if (_viewModel!.SelectedFile is { } row)
                {
                    SelectCurrentFile();
                    FileList.ScrollIntoView(row);
                }
                break;
        }
    }

    // Colors from DesignTokens: a brush read from the resources cannot be cast under AOT (see CLAUDE.md).
    private void UpdateStateBadge()
    {
        var light = ActualTheme == ElementTheme.Light;
        var text = _viewModel?.StateText ?? string.Empty;
        StateLabel.Text = text;
        StateBadge.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var (back, fore) = _viewModel?.State switch
        {
            PullRequestState.Merged => (light ? DesignTokens.Light.AccentSoft2 : DesignTokens.Dark.AccentSoft2, light ? DesignTokens.Light.AccentText : DesignTokens.Dark.AccentText),
            PullRequestState.Closed => (light ? DesignTokens.Light.DangerSoft : DesignTokens.Dark.DangerSoft, light ? DesignTokens.Light.Danger : DesignTokens.Dark.Danger),
            _ when _viewModel?.Detail?.Summary.IsDraft == true => (light ? DesignTokens.Light.Layer2 : DesignTokens.Dark.Layer2, light ? DesignTokens.Light.Text2 : DesignTokens.Dark.Text2),
            _ => (light ? DesignTokens.Light.SuccessSoft : DesignTokens.Dark.SuccessSoft, light ? DesignTokens.Light.Success : DesignTokens.Dark.Success),
        };
        StateBadge.Background = new SolidColorBrush(back);
        StateLabel.Foreground = new SolidColorBrush(fore);
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    // The discussions of the file, the comments kept for the review and the right to comment go to the editor page, which draws
    // them under the lines (J6.11). Sent before the diff itself, so that a new file shows its own threads at once.
    private void SendReview()
    {
        var vm = _viewModel;
        var file = vm?.SelectedFile?.Path ?? string.Empty;
        var enabled = vm is { IsOpen: true, IsFiles: true, Diff: not null } && file.Length > 0;
        List<ThreadRow> threads = enabled ? [.. vm!.FileThreads] : [];
        List<(int Index, ReviewDraftRow Row)> drafts = enabled
            ? [.. vm!.Drafts.Select((row, index) => (index, row)).Where(d => d.row.Comment.Path == file)]
            : [];
        Editor.ShowReview(
            file, enabled, enabled && vm!.CanSubmitReview, enabled && vm!.CanDraftReviewComments, EditorReviewLabels.Current,
            vm?.ReferenceLinks ?? new ReferenceLinks(null, null, null), vm?.MentionNames ?? new Dictionary<string, string>(),
            enabled && vm!.CanCompleteReferences, threads, drafts, vm?.RevealThreadId, vm?.RevealToken ?? 0);
    }

    // The write itself reports its failures in a toast (WriteAsync); the box always gets an answer, so that it never stays locked.
    // An async void handler must not let anything escape: the tab may be closed while the write is running.
    // Proposals after a #, ! or @: only the answer to the latest request is shown (one request per pause in typing at most).
    private int _referenceRequest;

    private async void OnReferenceRequested(object? sender, EditorReferenceRequest e)
    {
        if (_viewModel is not { } vm) return;
        var request = ++_referenceRequest;
        try
        {
            var items = await vm.SearchReferencesAsync(e.Kind, e.Query, CancellationToken.None);
            if (request == _referenceRequest) Editor.ShowReferences(e.Key, items);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The tab was closed meanwhile: nothing to show.
        }
    }

    // Only web addresses leave the app (the page asks, it cannot navigate away by itself).
    private static async void OnEditorLinkClicked(object? sender, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await Launcher.LaunchUriAsync(uri);
    }

    private async void OnReviewSubmitted(object? sender, EditorReviewSubmit e)
    {
        var ok = false;
        try
        {
            if (_viewModel is { SelectedFile: { } file } vm)
            {
                var comment = new NewReviewComment(file.Path, e.Line, e.Side, e.Body, e.StartLine < e.Line ? e.StartLine : null, e.StartColumn, e.EndColumn);
                ok = await vm.AddLineCommentAsync(comment, e.AsDraft);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ok = false;
        }
        Editor.ReviewDone(e.Key, ok);
    }

    private async void OnReviewReplied(object? sender, EditorReviewReply e)
    {
        var ok = false;
        try
        {
            ok = _viewModel is { } vm && await vm.ReplyToThreadAsync(e.ThreadId, e.Body);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ok = false;
        }
        Editor.ReviewDone(e.Key, ok);
    }

    private async void OnReviewResolveRequested(object? sender, EditorReviewResolve e)
    {
        var ok = false;
        try
        {
            ok = _viewModel is { } vm && await vm.SetThreadResolvedAsync(e.ThreadId, e.Resolved);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ok = false;
        }
        Editor.ReviewDone(e.Key, ok);
    }

    private void Refresh()
    {
        var vm = _viewModel;
        SendReview();
        var diff = vm is { IsOpen: true, IsFiles: true } ? vm.Diff : null;
        var message = Editor.Failed && diff is not null
            ? Loc.Get("Editor.Unavailable")
            : vm is { IsFiles: true } ? vm.DiffMessage : null;
        DiffMessage.Text = message ?? string.Empty;
        DiffMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        Editor.Visibility = diff is null || Editor.Failed ? Visibility.Collapsed : Visibility.Visible;
        object? content = diff is null ? null : vm!.IsSideBySide ? (object?)vm.Sides ?? diff : diff;
        // Writing in the review (comment, reply, resolve) still lives in the editor page (ADR-090, step R2): the native editor
        // shows the threads read only, when the service lets the screen write nothing.
        var native = vm is { CanReview: false };
        // Replays of the native review before R2, also in the AOT build: VIGO_NATIVE_REVIEW=1 forces the native editor (writing is
        // then unavailable). No effect unless set; removed with R2.
        native |= Environment.GetEnvironmentVariable("VIGO_NATIVE_REVIEW") == "1";
        if (ReferenceEquals(content, _shown) && native == _shownNative) return;
        _shown = content;
        _shownNative = native;
        switch (content)
        {
            case DiffSides sides:
                Editor.ShowSideBySide(sides, native);
                break;
            case FileDiff shown:
                Editor.ShowDiff(shown.Path, DiffDocument.Build(shown), [], staging: null, keepScroll: false, native);
                break;
            default:
                Editor.Clear();
                break;
        }
    }

    /// <summary>Keys pressed in the editor page: Échap, then the app's shortcuts.</summary>
    private void OnEditorKey(object? sender, EditorKey key)
    {
        if (key.Key == "Escape") Close();
        else if (key.VirtualKey > 0) KeyboardShortcuts.TryInvoke(this, (VirtualKey)key.VirtualKey, key.Modifiers);
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _syncingSelection) return;
        switch (FileList.SelectedItem)
        {
            case PullRequestFileRow row:
                _viewModel.SelectedFile = row;
                break;
            case FolderRow folder:
                // A folder is not something to open: it folds or unfolds, and the file stays selected.
                _viewModel.ToggleFolderCommand.Execute(folder);
                SelectCurrentFile();
                break;
        }
    }

    // Rows are replaced when a folder folds or the mode changes: the selection is put back on the file the diff shows.
    private void OnFileRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(SelectCurrentFile);

    private void SelectCurrentFile()
    {
        if (_viewModel?.SelectedFile is not { } row || Equals(FileList.SelectedItem, row)) return;
        _syncingSelection = true;
        FileList.SelectedItem = _viewModel.FileRows.Contains(row) ? row : null;
        _syncingSelection = false;
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileCommentPreview preview) _viewModel?.OpenThreadCommand.Execute(preview);
    }

    private void OnConversationClick(object sender, RoutedEventArgs e) => _viewModel?.SelectTabCommand.Execute(PullRequestTab.Conversation);

    private void OnCommitsClick(object sender, RoutedEventArgs e) => _viewModel?.SelectTabCommand.Execute(PullRequestTab.Commits);

    private void OnFilesClick(object sender, RoutedEventArgs e) => _viewModel?.SelectTabCommand.Execute(PullRequestTab.Files);

    // The pages get exactly the width of the view: a child that wants more than it is given is arranged at its own width (and
    // centered content or the right half of the side-by-side editor then slides under the right panel).
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        Header.Width = width;
        Pages.Width = width;
        ConversationScroll.Width = width;
        FilesPanel.Width = width;
        ApplyFilesWidth();

        // The conversation column is centered by hand (width and left margin from the view's own width): left-aligned with an
        // explicit size, it cannot depend on the width the scroll viewer's content slot happens to get.
        var column = Math.Min(MaxConversationWidth, width);
        ConversationColumn.Width = column;
        ConversationColumn.Margin = new Thickness(Math.Max(0, (width - column) / 2), 0, 0, 0);
    }

    private const double MaxConversationWidth = 900;

    private void OnMergeCommitClick(object sender, RoutedEventArgs e) => _ = _viewModel?.MergeAsync(MergeMethod.Merge);

    private void OnMergeSquashClick(object sender, RoutedEventArgs e) => _ = _viewModel?.MergeAsync(MergeMethod.Squash);

    private void OnMergeRebaseClick(object sender, RoutedEventArgs e) => _ = _viewModel?.MergeAsync(MergeMethod.Rebase);

    private void OnAutoMergeCommitClick(object sender, RoutedEventArgs e) => _ = _viewModel?.EnableAutoMergeAsync(MergeMethod.Merge);

    private void OnAutoMergeSquashClick(object sender, RoutedEventArgs e) => _ = _viewModel?.EnableAutoMergeAsync(MergeMethod.Squash);

    private void OnAutoMergeRebaseClick(object sender, RoutedEventArgs e) => _ = _viewModel?.EnableAutoMergeAsync(MergeMethod.Rebase);

    private void OnVoteApprovedClick(object sender, RoutedEventArgs e) => _ = _viewModel?.VoteAsync(ReviewVote.Approved);

    private void OnVoteSuggestionsClick(object sender, RoutedEventArgs e) => _ = _viewModel?.VoteAsync(ReviewVote.ApprovedWithSuggestions);

    private void OnVoteWaitingClick(object sender, RoutedEventArgs e) => _ = _viewModel?.VoteAsync(ReviewVote.WaitingForAuthor);

    private void OnVoteRejectedClick(object sender, RoutedEventArgs e) => _ = _viewModel?.VoteAsync(ReviewVote.Rejected);

    private void OnVoteNoneClick(object sender, RoutedEventArgs e) => _ = _viewModel?.VoteAsync(ReviewVote.None);

    // A reaction already given: clicking it adds the account's own, or removes it.
    private void OnReactionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReactionChip chip) _ = _viewModel?.ToggleReactionAsync(chip.CommentId, chip.Kind);
    }

    // The picker lists the eight reactions of the service for the comment the button belongs to.
    private void OnAddReactionClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || sender is not FrameworkElement anchor) return;
        var flyout = new MenuFlyout();
        foreach (var kind in ReactionChip.All)
        {
            var item = new MenuFlyoutItem { Text = $"{ReactionChip.EmojiOf(kind)} {ReactionChip.LabelOf(kind)}" };
            item.Click += (_, _) => _ = _viewModel?.ToggleReactionAsync(row.Entry.Id, kind);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private void OnEditCommentClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _viewModel?.BeginEditCommand.Execute(row);
    }

    private void OnSaveEditClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _viewModel?.SaveEditCommand.Execute(row);
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _viewModel?.CancelEditCommand.Execute(row);
    }

    private void OnDeleteCommentClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _viewModel?.DeleteCommentCommand.Execute(row);
    }

    private void OnReplyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThreadRow row) _viewModel?.ReplyCommand.Execute(row);
    }

    private void OnResolveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThreadRow row) _viewModel?.ToggleResolvedCommand.Execute(row);
    }

    private void OnRemoveDraftClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReviewDraftRow draft) _viewModel?.RemoveDraftCommand.Execute(draft);
    }

    private void OnReviewCommentClick(object sender, RoutedEventArgs e) => _ = _viewModel?.SubmitReviewAsync(ReviewDecision.Comment);

    private void OnReviewApproveClick(object sender, RoutedEventArgs e) => _ = _viewModel?.SubmitReviewAsync(ReviewDecision.Approve);

    private void OnReviewRequestChangesClick(object sender, RoutedEventArgs e) => _ = _viewModel?.SubmitReviewAsync(ReviewDecision.RequestChanges);

    // The comment a button of the conversation belongs to (the item is the data context of its template).
    private static TimelineRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as TimelineRow;

    private void Close() => _viewModel?.CloseCommand.Execute(null);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
