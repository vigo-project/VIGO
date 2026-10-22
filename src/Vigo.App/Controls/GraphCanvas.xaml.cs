using System.ComponentModel;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Graph;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI;

namespace Vigo.App.Controls;

/// <summary>
/// Virtualized commit graph: lanes, ref badges, message, author, date and SHA of the visible rows, drawn by Win2D
/// on a viewport-sized canvas (ADR-018). The cost of a frame depends on the viewport, not on the history size.
/// </summary>
public sealed partial class GraphCanvas : UserControl
{
    // Density (J4.6): read on every frame, the control is redrawn when it changes.
    private static float RowHeight => (float)Appearance.Current.RowHeight;
    private static float LaneWidth => (float)Appearance.Current.Metrics.LaneWidth;
    private const float LaneOrigin = 12f;
    private const float NodeRadius = 4.5f;
    private const float LineWidth = 2f;
    private const float CellPadding = 8f;
    private const float LoadingFooter = 40f;
    private const float MinGraphWidth = 64f;

    // Beyond this many lanes the graph column stops growing and the extra lanes are clipped (bundling: J8).
    private const int MaxVisibleLanes = 24;

    private GraphViewModel? _graph;
    private LayoutState? _layout;
    private float _offset;
    private float _laneWidth = MinGraphWidth;
    private Columns _columns;
    private double _dragStartWidth;
    private int _hoveredRow = -1;

    // Drag and drop of a branch (J4.3): badges drawn on the visible rows (canvas coordinates), the badge pressed and
    // where, the row a branch is dragged over.
    private readonly List<BadgeHit> _badgeHits = [];
    private (Point Position, GitRef Reference, BadgeHit Hit)? _pressedBadge;
    private (Point Position, GitRef Reference, BadgeHit Hit)? _dragSource;
    private int _dropRow = -1;
    private int[] _passThrough = new int[16];
    private bool[] _passThroughDotted = new bool[16];
    private TextFormats? _formats;
    private CanvasStrokeStyle? _dashed;

    // Lines from a stash (J7.16): dotted.
    private CanvasStrokeStyle? _dotted;
    private CanvasRenderTarget? _frame;
    private Size _frameSize;

    public GraphCanvas()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            UpdateColumns();
            UpdateScrollRange();
        };
        Appearance.Current.PropertyChanged += OnAppearanceChanged;
        ActualThemeChanged += (_, _) => Canvas.Invalidate();
        KeyDown += OnKeyDown;
        // On the control (it has the keyboard focus), not on the canvas: the menu key and Shift+F10 raise the event on
        // the focused element and it only bubbles up; a right click on the canvas bubbles up to here too.
        ContextRequested += OnContextRequested;
        Canvas.DoubleTapped += OnDoubleTapped;
        Canvas.DragStarting += OnDragStarting;
        Canvas.DropCompleted += (_, _) => RefDragDrop.End();
        Canvas.DragOver += OnDragOver;
        Canvas.DragLeave += (_, _) => SetDropRow(-1);
        Canvas.Drop += OnDrop;

        GraphSplitter.DragStarted += (_, _) => _dragStartWidth = _columns.Graph;
        GraphSplitter.DragDelta += (_, delta) => SetLayout(l => l.GraphColumnWidth = _dragStartWidth + delta);
        GraphSplitter.ResetRequested += (_, _) => SetLayout(l => l.GraphColumnWidth = null);
        HookColumnSplitter(AuthorSplitter, () => _columns.Author, (l, w) => l.AuthorColumnWidth = w, LayoutLimits.AuthorColumn);
        HookColumnSplitter(DateSplitter, () => _columns.Date, (l, w) => l.DateColumnWidth = w, LayoutLimits.DateColumn);
        HookColumnSplitter(ShaSplitter, () => _columns.Sha, (l, w) => l.ShaColumnWidth = w, LayoutLimits.ShaColumn);
        foreach (var splitter in (Splitter[])[GraphSplitter, AuthorSplitter, DateSplitter, ShaSplitter])
        {
            splitter.DragCompleted += (_, _) => _ = _layout?.SaveAsync();
        }
    }

    /// <summary>Column widths shared by every tab; set once by the repository view.</summary>
    public LayoutState? Layout
    {
        get => _layout;
        set
        {
            if (_layout is not null) _layout.PropertyChanged -= OnLayoutPropertyChanged;
            _layout = value;
            if (_layout is not null) _layout.PropertyChanged += OnLayoutPropertyChanged;
            UpdateColumns();
        }
    }

    /// <summary>Graph shown by the control; set once by the repository view.</summary>
    public GraphViewModel? Graph
    {
        get => _graph;
        set
        {
            if (_graph is not null)
            {
                _graph.Changed -= OnGraphChanged;
                _graph.PropertyChanged -= OnGraphPropertyChanged;
            }

            _graph = value;
            if (_graph is not null)
            {
                _graph.Changed += OnGraphChanged;
                _graph.PropertyChanged += OnGraphPropertyChanged;
            }

            OnGraphChanged(this, EventArgs.Empty);
        }
    }

    /// <summary>Merge and rebase by drag and drop of a badge; set once by the repository view.</summary>
    public MergeActionsViewModel? Merge { get; set; }

    /// <summary>Checkout then pull of a badge's branch (double click); set once by the repository view.</summary>
    public Func<GitRef, bool>? CheckoutAndPull { get; set; }

    /// <summary>Branch and tag creation of the row menu; set once by the repository view.</summary>
    public RefActionsViewModel? RefActions { get; set; }

    /// <summary>Cherry-pick, revert and reset of the row menu; set once by the repository view.</summary>
    public HistoryActionsViewModel? History { get; set; }

    /// <summary>Patch files of the row menu (J7.12); set once by the repository view.</summary>
    public PatchActionsViewModel? Patches { get; set; }

    /// <summary>Interactive rebase of the row menu (J5.5); set once by the repository view.</summary>
    public InteractiveRebaseViewModel? Rebase { get; set; }

    /// <summary>Pull request of the row menu and of the drop menu (J6.2); set once by the repository view.</summary>
    public HostingPanelViewModel? Hosting { get; set; }

    /// <summary>
    /// Right click (or menu key / Shift+F10) on a commit row: selects it and opens the row menu. No menu on the
    /// work-in-progress row.
    /// </summary>
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (_graph is null) return;
        e.Handled = true;
        var row = e.TryGetPosition(Canvas, out var point) ? RowAt(point.Y) : _graph.SelectedRow;
        if (row < 0 || _graph.IsWorkInProgressRow(row)) return;
        _graph.SelectedRow = row;
        // A stash is not a commit of the history: branch, tag, cherry-pick, reset… make no sense on it; it has its own menu.
        if (_graph.GetCommit(row).IsStash)
        {
            ShowStashMenu(e, row);
            return;
        }

        var canCreate = RefActions is { CanPrompt: true };
        NewBranchItem.IsEnabled = NewTagItem.IsEnabled = canCreate;
        var sha = _graph.GetCommit(row).Sha;
        CherryPickItem.IsEnabled = History?.CanCherryPick(sha) == true;
        RevertItem.IsEnabled = History?.CanRevert(sha) == true;
        ResetItem.IsEnabled = History?.CanReset(sha) == true;
        CreatePatchItem.IsEnabled = Patches?.CanCreateFromCommit == true;
        ResetItem.Text = History?.ResetLabel ?? Loc.Get("Graph.Menu.Reset");
        // The commits of HEAD after this one (or not in it) are replayed on it: nothing to do on HEAD itself.
        InteractiveRebaseItem.IsEnabled = Rebase?.CanOpen == true && sha != _graph.Head;
        InteractiveRebaseItem.Text = Loc.Format("Graph.Menu.InteractiveRebase", _graph.HeadBranch ?? "HEAD");
        // Pull request of the local branch carried by this row (its badge).
        // On a badge of another branch, that badge is the base of the pull request of the checked-out branch.
        _pullRequestTarget = null;
        var badge = e.TryGetPosition(Canvas, out var badgePoint) ? BranchAt(badgePoint)?.Reference : null;
        var current = _graph.Refs?.OfKind(GitRefKind.LocalBranch).FirstOrDefault(r => r.IsCurrent);
        if (badge is not null && current is not null && badge.CanonicalName != current.CanonicalName && badge.Name != current.Upstream
            && Hosting?.CanCreatePullRequest(current) == true)
        {
            _pullRequestBranch = current;
            _pullRequestTarget = badge;
        }
        else
        {
            _pullRequestBranch = LocalBranchOf(row) is { } branch && Hosting?.CanCreatePullRequest(branch) == true ? branch : null;
        }
        PullRequestItem.Visibility = _pullRequestBranch is null ? Visibility.Collapsed : Visibility.Visible;
        if (_pullRequestBranch is not null)
        {
            PullRequestItem.Text = _pullRequestTarget is null
                ? Loc.Format("Graph.Menu.PullRequestFor", _pullRequestBranch.Name)
                : Loc.Format("Graph.Menu.PullRequestTo", _pullRequestBranch.Name, _pullRequestTarget.Name);
        }
        // Keyboard: under the selected row, at the left of the message column.
        var position = e.TryGetPosition(Canvas, out var at) ? at : new Point(_columns.Graph, (row + 1) * RowHeight - _offset);
        RowMenu.ShowAt(Canvas, new FlyoutShowOptions { Position = position });
    }

    /// <summary>Stash actions (apply, pop, delete), set once by the repository view.</summary>
    public StashActionsViewModel? Stash { get; set; }

    private GitRef? _stashRef;

    private void ShowStashMenu(ContextRequestedEventArgs e, int row)
    {
        var sha = _graph!.GetCommit(row).Sha;
        _stashRef = Stash is null ? null : _graph.Refs?.OfKind(GitRefKind.Stash).FirstOrDefault(r => r.Target == sha);
        if (_stashRef is null) return;
        StashDeleteItem.Text = RefsPanelViewModel.DeleteStashLabel(_stashRef);
        var position = e.TryGetPosition(Canvas, out var at) ? at : new Point(_columns.Graph, (row + 1) * RowHeight - _offset);
        StashMenu.ShowAt(Canvas, new FlyoutShowOptions { Position = position });
    }

    private void OnStashApplyClick(object sender, RoutedEventArgs e)
    {
        if (_stashRef is { } stash) Stash?.ApplyCommand.Execute(stash);
    }

    private void OnStashPopClick(object sender, RoutedEventArgs e)
    {
        if (_stashRef is { } stash) Stash?.PopCommand.Execute(stash);
    }

    private void OnStashDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_stashRef is { } stash) Stash?.DeleteCommand.Execute(stash);
    }

    private GitRef? _pullRequestBranch;
    private GitRef? _pullRequestTarget;

    private void OnPullRequestClick(object sender, RoutedEventArgs e)
    {
        if (_pullRequestBranch is { } branch) _ = Hosting?.CreatePullRequestAsync(branch, _pullRequestTarget);
    }

    private void OnNewBranchClick(object sender, RoutedEventArgs e)
    {
        if (_graph?.SelectedSha is { } sha) RefActions?.CreateBranchCommand.Execute(sha);
    }

    private void OnNewTagClick(object sender, RoutedEventArgs e)
    {
        if (_graph?.SelectedSha is { } sha) RefActions?.CreateTagCommand.Execute(sha);
    }

    private void OnCherryPickClick(object sender, RoutedEventArgs e)
    {
        if (_graph?.SelectedSha is { } sha) History?.CherryPickCommand.Execute(sha);
    }

    private void OnCreatePatchClick(object sender, RoutedEventArgs e)
    {
        if (_graph?.SelectedSha is { } sha) Patches?.CreateCommitPatchCommand.Execute(sha);
    }

    private void OnRevertClick(object sender, RoutedEventArgs e)
    {
        if (_graph?.SelectedSha is { } sha) History?.RevertCommand.Execute(sha);
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_graph?.SelectedSha is not { } sha || sender is not MenuFlyoutItem { Tag: string tag }) return;
        var mode = tag switch
        {
            "Soft" => ResetMode.Soft,
            "Hard" => ResetMode.Hard,
            _ => ResetMode.Mixed,
        };
        History?.ResetCommand.Execute(new ResetPlan(sha, mode));
    }

    private void OnInteractiveRebaseClick(object sender, RoutedEventArgs e)
    {
        if (_graph?.SelectedSha is not { } sha || Rebase is null) return;
        var name = _graph.FindCommit(sha) is { } info ? $"{sha.ToShortString()} « {info.Subject} »" : sha.ToShortString();
        _ = Rebase.OpenAsync(sha.ToString(), name, sha);
    }

    private void OnCopyShaClick(object sender, RoutedEventArgs e) => CopySelectedSha();

    /// <summary>Full SHA of the selected commit to the clipboard (menu, Ctrl+C).</summary>
    private void CopySelectedSha()
    {
        if (_graph?.SelectedSha is not { } sha) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(sha.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    /// <summary>Releases DirectX resources when the tab is closed for good (the view is cached across tab switches).</summary>
    public void Release()
    {
        Graph = null;
        Appearance.Current.PropertyChanged -= OnAppearanceChanged;
        Layout = null;
        Canvas.RemoveFromVisualTree();
        _formats?.Dispose();
        _formats = null;
        _dashed?.Dispose();
        _dashed = null;
        _dotted?.Dispose();
        _dotted = null;
        _frame?.Dispose();
        _frame = null;
    }

    private int RowCount => _graph?.RowCount ?? 0;

    private float ContentHeight => RowCount * RowHeight + (_graph is { RowCount: > 0 } && (_graph.IsLoading || _graph.IsPartial) ? LoadingFooter : 0);

    private static float LaneX(int lane) => LaneOrigin + lane * LaneWidth;

    private void OnGraphChanged(object? sender, EventArgs e)
    {
        var lanes = _graph is { RowCount: > 0 } graph ? Math.Clamp(graph.Lanes.MaxLaneCount, 1, MaxVisibleLanes) : 1;
        _laneWidth = Math.Max(MinGraphWidth, LaneX(lanes - 1) + LaneWidth);
        UpdateColumns();
        UpdateStatus();
        UpdateScrollRange();
        Canvas.Invalidate();
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateColumns();

    // Density: row height and lane width change, so do the graph column, the scroll range and the drawing.
    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Appearance.Metrics)) OnGraphChanged(this, EventArgs.Empty);
        else if (e.PropertyName == nameof(Appearance.Lanes)) Canvas.Invalidate();
    }

    /// <summary>Right-hand columns: the splitter is on their left border, dragging it to the left widens them.</summary>
    private void HookColumnSplitter(Splitter splitter, Func<double> width, Action<LayoutState, double> set, WidthLimit limit)
    {
        splitter.DragStarted += (_, _) => _dragStartWidth = width();
        splitter.DragDelta += (_, delta) => SetLayout(l => set(l, _dragStartWidth - delta));
        splitter.ResetRequested += (_, _) => SetLayout(l => set(l, limit.Default));
    }

    private void SetLayout(Action<LayoutState> change)
    {
        if (_layout is not null) change(_layout);
    }

    /// <summary>Recomputes the column widths (lanes, saved layout, control width) and applies them to the header.</summary>
    private void UpdateColumns()
    {
        var layout = _layout;
        var computed = GraphColumnLayout.Compute(ActualWidth,
            layout?.GraphColumnWidth ?? _laneWidth,
            layout?.AuthorColumnWidth ?? LayoutLimits.AuthorColumn.Default,
            layout?.DateColumnWidth ?? LayoutLimits.DateColumn.Default,
            layout?.ShaColumnWidth ?? LayoutLimits.ShaColumn.Default);
        var columns = new Columns(computed);
        if (columns == _columns) return;

        _columns = columns;
        GraphColumn.Width = new GridLength(computed.Graph);
        MessageColumn.Width = new GridLength(computed.Message);
        AuthorColumn.Width = new GridLength(computed.Author);
        DateColumn.Width = new GridLength(computed.Date);
        ShaColumn.Width = new GridLength(computed.Sha);
        Canvas.Invalidate();
    }

    private void OnGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GraphViewModel.SelectedRow):
                EnsureVisible(_graph!.SelectedRow);
                Canvas.Invalidate();
                break;
            case nameof(GraphViewModel.IsLoading) or nameof(GraphViewModel.ErrorMessage) or nameof(GraphViewModel.IsEmpty)
                or nameof(GraphViewModel.HasWorkInProgress) or nameof(GraphViewModel.IsFiltered) or nameof(GraphViewModel.IsSearching):
                UpdateStatus();
                UpdateScrollRange();
                Canvas.Invalidate();
                break;
        }
    }

    private void UpdateStatus()
    {
        var text = _graph switch
        {
            null => null,
            { ErrorMessage: { } error, RowCount: 0 } => error,
            { IsEmpty: true, IsFiltered: true, IsSearching: false } => Loc.Get("Graph.Status.NoMatch"),
            { IsEmpty: true, HasWorkInProgress: false, IsFiltered: false } => Loc.Get("Graph.Status.Empty"),
            { IsLoading: true, RowCount: 0 } => Loc.Get("Graph.Status.Loading"),
            _ => null,
        };
        Status.Text = text ?? string.Empty;
        Status.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateScrollRange()
    {
        var viewport = (float)Canvas.ActualHeight;
        var maximum = Math.Max(0, ContentHeight - viewport);
        VerticalBar.ViewportSize = viewport;
        VerticalBar.LargeChange = Math.Max(RowHeight, viewport - RowHeight);
        VerticalBar.Maximum = maximum;
        VerticalBar.Visibility = maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_offset > maximum)
        {
            ScrollTo(maximum);
        }
    }

    private void ScrollTo(float offset)
    {
        offset = Math.Clamp(offset, 0, (float)VerticalBar.Maximum);
        if (offset == _offset) return;
        _offset = offset;
        VerticalBar.Value = offset;
        Canvas.Invalidate();
    }

    private void EnsureVisible(int row)
    {
        if (row < 0) return;
        var top = row * RowHeight;
        var viewport = (float)Canvas.ActualHeight;
        if (top < _offset)
            ScrollTo(top);
        else if (top + RowHeight > _offset + viewport)
            ScrollTo(top + RowHeight - viewport);
    }

    private int RowAt(double y)
    {
        var row = (int)Math.Floor((y + _offset) / RowHeight);
        return row >= 0 && row < RowCount ? row : -1;
    }

    private void OnScrollValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _offset = (float)e.NewValue;
        Canvas.Invalidate();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Canvas).Properties.MouseWheelDelta;
        ScrollTo(_offset - delta / 120f * 3 * RowHeight);
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(Canvas);
        _pressedBadge = point.Properties.IsLeftButtonPressed && Merge is { CanStart: true } && BranchAt(point.Position) is var (reference, hit)
            ? (point.Position, reference, hit)
            : null;
        if (_graph is not null && RowAt(e.GetCurrentPoint(Canvas).Position.Y) is var row and >= 0)
        {
            _graph.SelectedRow = row;
        }
        // Handled: otherwise the default pointer focus runs after this handler and moves the focus to a ScrollViewer, so the arrow keys never reach OnKeyDown.
        e.Handled = true;
    }

    /// <summary>Double click on a branch badge: checkout of the branch, then pull (the progress card and toasts are the tab's).</summary>
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (CheckoutAndPull is not { } checkoutAndPull || BranchAt(e.GetPosition(Canvas)) is not var (reference, _)) return;
        e.Handled = checkoutAndPull(reference);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);
        if (_pressedBadge is { } pressed)
        {
            if (!point.Properties.IsLeftButtonPressed)
            {
                _pressedBadge = null;
            }
            else if (Math.Abs(point.Position.X - pressed.Position.X) + Math.Abs(point.Position.Y - pressed.Position.Y) > 6)
            {
                // A pressed badge moved: drag its branch (mockup refBadge: drag on another branch to merge or rebase).
                _pressedBadge = null;
                _dragSource = pressed;
                _ = Canvas.StartDragAsync(point);
                return;
            }
        }

        var row = RowAt(point.Position.Y);
        if (row != _hoveredRow)
        {
            _hoveredRow = row;
            Canvas.Invalidate();
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoveredRow < 0) return;
        _hoveredRow = -1;
        Canvas.Invalidate();
    }

    private void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (_dragSource is { } source)
        {
            RefDragDrop.Start(args.Data, source.Reference);
            // The default visual is a capture of the whole canvas: the dragged badge follows the pointer instead, held
            // where it was pressed.
            if (RenderBadge(source.Hit, source.Position) is var (bitmap, anchor)) args.DragUI.SetContentFromSoftwareBitmap(bitmap, anchor);
            else args.DragUI.SetContentFromDataPackage();
        }
        else
        {
            args.Cancel = true;
        }
        _dragSource = null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var position = e.GetPosition(Canvas);
        var row = RowAt(position.Y);
        var target = DropTargetAt(position, row);
        var accepted = RefDragDrop.SourceFor(target) is not null
            || (RefDragDrop.PullRequestSourceFor(target) is { } source && Hosting?.CanCreatePullRequest(source) == true);
        SetDropRow(accepted ? row : -1);
        if (accepted) RefDragDrop.AcceptOver(e, target);
        else e.AcceptedOperation = DataPackageOperation.None;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        SetDropRow(-1);
        var position = e.GetPosition(Canvas);
        var target = DropTargetAt(position, RowAt(position.Y));
        if (Merge is not { } merge) return;
        if (RefDragDrop.SourceFor(target) is { } source)
        {
            if (await merge.DropMenuAsync(source, target!) is { } menu) RefDragDrop.ShowMenu(Canvas, position, menu, merge, Rebase, Hosting);
        }
        else if (RefDragDrop.PullRequestSourceFor(target) is { } pullRequestSource && Hosting?.CanCreatePullRequest(pullRequestSource) == true)
        {
            // Remote badge: no merge or rebase on it, only the pull request towards that branch.
            var menu = new DropMenu($"{pullRequestSource.Name} → {target!.Name}", null, null, pullRequestSource, target);
            RefDragDrop.ShowMenu(Canvas, position, menu, merge, Rebase, Hosting);
        }
    }

    /// <summary>
    /// What a branch dropped at <paramref name="point"/> lands on: the branch of the badge under the pointer (local or
    /// remote), else the first local branch of the row.
    /// </summary>
    private GitRef? DropTargetAt(Point point, int row) => BranchAt(point)?.Reference ?? LocalBranchOf(row);

    private void SetDropRow(int row)
    {
        if (row == _dropRow) return;
        _dropRow = row;
        Canvas.Invalidate();
    }

    /// <summary>Local or remote branch of the badge under <paramref name="point"/> (not a tag, not the detached HEAD).</summary>
    private (GitRef Reference, BadgeHit Hit)? BranchAt(Point point)
    {
        foreach (var hit in _badgeHits)
        {
            if (!hit.Bounds.Contains(point)) continue;
            var kind = hit.Badge.Kind switch
            {
                RefBadgeKind.Head or RefBadgeKind.Branch => GitRefKind.LocalBranch,
                RefBadgeKind.Remote => GitRefKind.RemoteBranch,
                _ => (GitRefKind?)null,
            };
            return kind is { } k && _graph?.Refs?.OfKind(k).FirstOrDefault(r => r.Name == hit.Badge.Label) is { } reference
                ? (reference, hit)
                : null;
        }
        return null;
    }

    /// <summary>
    /// The badge as drawn in the graph, on an opaque background (the badge fill is translucent), at the screen scale;
    /// the anchor keeps the pointer where the badge was pressed. Null when it cannot be drawn (device lost).
    /// </summary>
    private (SoftwareBitmap Bitmap, Point Anchor)? RenderBadge(BadgeHit hit, Point pressed)
    {
        if (_formats is not { } formats || _dashed is not { } dashed) return null;
        const float margin = 1;
        var scale = (float)(XamlRoot?.RasterizationScale ?? 1);
        var width = (float)hit.Bounds.Width + 2 * margin;
        var height = BadgeHeight + 2 * margin;
        try
        {
            using var target = new CanvasRenderTarget(Canvas.Device, width, height, 96 * scale);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                var colors = ThemeColors.For(ActualTheme);
                ds.FillRoundedRectangle(new Rect(margin, margin, hit.Bounds.Width, BadgeHeight), 4, 4,
                    ActualTheme == ElementTheme.Light ? DesignTokens.Light.Layer : DesignTokens.Dark.Layer);
                DrawBadge(ds, target, hit.Badge, margin, height / 2, (float)hit.Bounds.Width, hit.LaneColor, formats, colors, dashed);
            }
            var pixels = target.GetPixelBytes();
            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                (int)target.SizeInPixels.Width, (int)target.SizeInPixels.Height, BitmapAlphaMode.Premultiplied);
            // Pixels of the bitmap: where the badge was pressed, kept inside it.
            var x = Math.Clamp(pressed.X - hit.Bounds.X + margin, 0, width) * scale;
            var y = Math.Clamp(pressed.Y - (hit.Bounds.Y + hit.Bounds.Height / 2) + height / 2, 0, height) * scale;
            return (bitmap, new Point(x, y));
        }
        catch (Exception ex) when (Canvas.Device.IsDeviceLost(ex.HResult))
        {
            return null;
        }
    }

    /// <summary>A badge drawn on a visible row (canvas coordinates), for hit testing and the drag visual.</summary>
    private readonly record struct BadgeHit(Rect Bounds, RefBadge Badge, Color LaneColor);

    /// <summary>Local branch a branch dropped on <paramref name="row"/> goes to (mockup: the first branch of the row).</summary>
    private GitRef? LocalBranchOf(int row)
    {
        if (_graph is not { } graph || row < 0 || graph.IsWorkInProgressRow(row)) return null;
        var sha = graph.GetCommit(row).Sha;
        return graph.Refs?.OfKind(GitRefKind.LocalBranch).Where(r => r.Target == sha).OrderByDescending(r => r.IsCurrent).FirstOrDefault();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_graph is null) return;
        if (e.Key == VirtualKey.C && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            CopySelectedSha();
            e.Handled = true;
            return;
        }

        var page = Math.Max(1, (int)(Canvas.ActualHeight / RowHeight) - 1);
        int? delta = e.Key switch
        {
            VirtualKey.Down => 1,
            VirtualKey.Up => -1,
            VirtualKey.PageDown => page,
            VirtualKey.PageUp => -page,
            VirtualKey.Home => int.MinValue / 2,
            VirtualKey.End => int.MaxValue / 2,
            _ => null,
        };
        if (delta is { } d)
        {
            _graph.MoveSelection(d);
            e.Handled = true;
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_graph is not { RowCount: > 0 } graph) return;

        // The frame is drawn off-screen, then copied pixel for pixel. CanvasControl draws into a shared XAML surface at
        // an offset that changes from frame to frame; its rounding moved the glyphs to another subpixel position, so
        // the text jumped or blurred when hovering rows (non-maximized window), while the lanes did not move.
        var size = sender.Size;
        if (_frame is not null && (_frame.Device != sender.Device || _frame.Dpi != sender.Dpi || _frameSize != size))
        {
            _frame.Dispose();
            _frame = null;
        }

        var target = _frame ??= new CanvasRenderTarget(sender, (float)size.Width, (float)size.Height);
        _frameSize = size;
        using (var frame = target.CreateDrawingSession())
        {
            frame.Clear(Colors.Transparent);
            DrawFrame(frame, sender, graph);
        }

        args.DrawingSession.DrawImage(target, 0, 0, target.Bounds, 1, CanvasImageInterpolation.NearestNeighbor);
    }

    private void DrawFrame(CanvasDrawingSession ds, CanvasControl sender, GraphViewModel graph)
    {
        var formats = _formats ??= new TextFormats();
        _dashed ??= new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        _dotted ??= new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dot };
        var colors = ThemeColors.For(ActualTheme);
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        var first = Math.Max(0, (int)(_offset / RowHeight));
        var last = Math.Min(graph.RowCount - 1, (int)((_offset + height) / RowHeight));
        graph.EnsureLoaded(last);

        // Row backgrounds first, so lanes and text are drawn on top. Without a selected commit the right panel shows
        // the working directory: the work-in-progress row is highlighted then (mockup).
        for (var row = first; row <= last; row++)
        {
            var top = row * RowHeight - _offset;
            if (row == _dropRow)
            {
                // A branch dragged over the row of a local branch (mockup: accent background and outline).
                ds.FillRectangle(0, top, width, RowHeight, colors.AccentSoft);
                ds.DrawRectangle(0.5f, top + 0.5f, width - 1, RowHeight - 1, colors.Accent, 1);
            }
            else if (row == graph.SelectedRow || (graph.SelectedRow < 0 && graph.IsWorkInProgressRow(row)))
            {
                ds.FillRectangle(0, top, width, RowHeight, colors.AccentSoft);
                ds.FillRectangle(0, top, 3, RowHeight, colors.Accent);
            }
            else if (row == _hoveredRow)
            {
                ds.FillRectangle(0, top, width, RowHeight, colors.SubHover);
            }
        }

        var columns = _columns;
        using (ds.CreateLayer(1f, new Rect(0, 0, columns.Graph, height)))
        {
            var headRow = graph.HeadRow;
            for (var row = first; row <= last; row++)
            {
                var top = row * RowHeight - _offset;
                if (graph.IsWorkInProgressRow(row))
                    DrawWorkInProgressNode(ds, graph, top, headRow, colors, _dashed);
                else
                    DrawLanes(ds, graph, row, top, colors);

                // Dashed link from the work-in-progress node down to HEAD, in HEAD's lane.
                // Not through the node of a stash that sits in that lane (the icon would be drawn over the line).
                if (graph.HasWorkInProgress && row > 0 && row <= headRow && !(graph.GetCommit(row).IsStash && graph.GetNodeLane(row) == graph.GetNodeLane(0)))
                {
                    var x = LaneX(graph.GetNodeLane(0));
                    ds.DrawLine(x, top, x, row == headRow ? top + RowHeight / 2 : top + RowHeight, LaneColor(graph.GetNodeLane(0)), LineWidth, _dashed);
                }
            }
        }

        _badgeHits.Clear();
        for (var row = first; row <= last; row++)
        {
            if (graph.IsWorkInProgressRow(row))
                DrawWorkInProgressText(ds, graph, row * RowHeight - _offset, columns, formats, colors);
            else
                DrawRowText(ds, sender, graph, row, row * RowHeight - _offset, columns, formats, colors, _dashed, _badgeHits);
        }

        var footer = graph.RowCount * RowHeight - _offset;
        if ((graph.IsLoading || graph.IsPartial) && footer < height)
        {
            ds.DrawText(Loc.Format(graph.IsLoading ? "Graph.Footer.Loading" : "Graph.Footer.More", graph.RowCount - graph.CommitRowOffset),
                new Rect(columns.MessageX + CellPadding, footer, Math.Max(0, width - columns.MessageX - CellPadding), LoadingFooter), colors.Text3, formats.Body);
        }
    }

    /// <summary>Hollow dashed node of the work-in-progress row, in HEAD's lane (mockup: <c>c.wip</c>).</summary>
    private static void DrawWorkInProgressNode(CanvasDrawingSession ds, GraphViewModel graph, float top, int headRow,
        ThemeColors colors, CanvasStrokeStyle dashed)
    {
        var lane = graph.GetNodeLane(0);
        var x = LaneX(lane);
        var center = top + RowHeight / 2;
        if (headRow > 0)
            ds.DrawLine(x, center + NodeRadius, x, top + RowHeight, LaneColor(lane), LineWidth, dashed);
        ds.FillCircle(x, center, NodeRadius + 0.5f, colors.Sunken);
        ds.DrawCircle(x, center, NodeRadius + 0.5f, LaneColor(lane), 1.5f, dashed);
    }

    private static void DrawWorkInProgressText(CanvasDrawingSession ds, GraphViewModel graph, float top, Columns columns,
        TextFormats formats, ThemeColors colors)
    {
        if (graph.IsWorkInProgressWarning)
            ds.DrawText(graph.WorkInProgressText, Cell(columns.MessageX, columns.Message, top), colors.Warning, formats.Body);
        else
            ds.DrawText(graph.WorkInProgressText, Cell(columns.MessageX, columns.Message, top), colors.Text2, formats.Italic);
        ds.DrawText(Loc.Get("Graph.Wip.Now"), Cell(columns.DateX, columns.Date, top), colors.Text3, formats.Small);
    }

    private void DrawLanes(CanvasDrawingSession ds, GraphViewModel graph, int displayRow, float top, ThemeColors colors)
    {
        var lanes = graph.Lanes;
        var row = graph.ToCommitRow(displayRow);
        var center = top + RowHeight / 2;
        var bottom = top + RowHeight;
        var node = lanes.GetNodeLane(row);
        var nodeX = LaneX(node);

        if (_passThrough.Length < lanes.MaxLaneCount)
        {
            _passThrough = new int[lanes.MaxLaneCount * 2];
            _passThroughDotted = new bool[lanes.MaxLaneCount * 2];
        }
        var count = lanes.GetPassThroughLanes(row, _passThrough, _passThroughDotted);
        for (var i = 0; i < count; i++)
        {
            var lane = _passThrough[i];
            Line(ds, LaneX(lane), top, LaneX(lane), bottom, LaneColor(lane), _passThroughDotted[i] ? _dotted : null);
        }

        // Lines ending at the node from the row above; lanes other than the node's curve into it. A stash's lines are dotted.
        var incoming = lanes.GetIncomingLanes(row);
        var incomingDotted = lanes.GetIncomingDotted(row);
        for (var i = 0; i < incoming.Length; i++)
        {
            var lane = incoming[i];
            var style = incomingDotted[i] ? _dotted : null;
            if (lane == node)
                Line(ds, nodeX, top, nodeX, center, LaneColor(lane), style);
            else
                DrawCurve(ds, new Vector2(LaneX(lane), top), new Vector2(nodeX, center), LaneColor(lane), style);
        }

        // One line per parent towards the row below; parents in another lane curve out of the node.
        var outgoing = lanes.GetOutgoingLanes(row);
        var outgoingDotted = lanes.GetOutgoingDotted(row);
        for (var i = 0; i < outgoing.Length; i++)
        {
            var lane = outgoing[i];
            var style = outgoingDotted[i] ? _dotted : null;
            if (lane == node)
                Line(ds, nodeX, center, nodeX, bottom, LaneColor(lane), style);
            else
                DrawCurve(ds, new Vector2(nodeX, center), new Vector2(LaneX(lane), bottom), LaneColor(lane), style);
        }

        var commit = graph.GetCommit(displayRow);
        if (commit.IsStash)
        {
            // A stash is not a commit of the history: a dotted square holding the stash icon.
            const float half = 8;
            var box = new Rect(nodeX - half, center - half, 2 * half, 2 * half);
            ds.FillRoundedRectangle(box, 3, 3, colors.Sunken);
            ds.DrawRoundedRectangle(box, 3, 3, LaneColor(node), 1.5f, _dotted);
            if (_formats is { } iconFormats) ds.DrawText(FluentIcons.Archive, box, LaneColor(node), iconFormats.Icon);
            return;
        }
        if (commit.Sha == graph.Head)
            ds.DrawCircle(nodeX, center, NodeRadius + 3, colors.Text, 1.5f);
        var radius = commit.IsMerge ? NodeRadius - 1 : NodeRadius;
        ds.FillCircle(nodeX, center, radius, LaneColor(node));
        ds.DrawCircle(nodeX, center, radius, colors.Sunken, LineWidth);
    }

    // A solid line, or a dotted one (a stash's) when a style is given.
    private static void Line(CanvasDrawingSession ds, float x0, float y0, float x1, float y1, Color color, CanvasStrokeStyle? style)
    {
        if (style is null) ds.DrawLine(x0, y0, x1, y1, color, LineWidth);
        else ds.DrawLine(x0, y0, x1, y1, color, LineWidth, style);
    }

    private static void DrawCurve(CanvasDrawingSession ds, Vector2 from, Vector2 to, Color color, CanvasStrokeStyle? style = null)
    {
        using var builder = new CanvasPathBuilder(ds);
        var bend = (to.Y - from.Y) * 0.6f;
        builder.BeginFigure(from);
        builder.AddCubicBezier(new Vector2(from.X, from.Y + bend), new Vector2(to.X, to.Y - bend), to);
        builder.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(builder);
        if (style is null) ds.DrawGeometry(geometry, color, LineWidth);
        else ds.DrawGeometry(geometry, color, LineWidth, style);
    }

    private static void DrawRowText(CanvasDrawingSession ds, ICanvasResourceCreator device, GraphViewModel graph, int row, float top,
        Columns columns, TextFormats formats, ThemeColors colors, CanvasStrokeStyle dashed, List<BadgeHit> badgeHits)
    {
        var commit = graph.GetCommit(row);
        var center = top + RowHeight / 2;
        var laneColor = LaneColor(graph.GetNodeLane(row));

        // Ref badges, then the message in the remaining width.
        var x = columns.MessageX + CellPadding;
        var messageEnd = columns.AuthorX - CellPadding;
        foreach (var badge in graph.GetBadges(commit.Sha))
        {
            if (messageEnd - x < 48) break;
            var badgeWidth = DrawBadge(ds, device, badge, x, center, messageEnd - x, laneColor, formats, colors, dashed);
            badgeHits.Add(new BadgeHit(new Rect(x, center - BadgeHeight / 2, badgeWidth, BadgeHeight), badge, laneColor));
            x += badgeWidth + 6;
        }

        var isHead = commit.Sha == graph.Head;
        if (messageEnd > x)
            ds.DrawText(commit.Subject, new Rect(x, top, messageEnd - x, RowHeight), colors.Text, isHead ? formats.BodyStrong : formats.Body);

        // Author: initials avatar + name.
        var authorX = columns.AuthorX + CellPadding;
        ds.FillCircle(authorX + 9, center, 9, Appearance.Current.Lanes[StableColor.IndexFor(commit.AuthorName)]);
        ds.DrawText(Initials(commit.AuthorName), new Rect(authorX, center - 9, 18, 18), Colors.White, formats.Avatar);
        ds.DrawText(commit.AuthorName, Cell(columns.AuthorX + 24, columns.Author - 24, top), colors.Text2, formats.Small);

        ds.DrawText(graph.FormatDate(commit.AuthorDate), Cell(columns.DateX, columns.Date, top), colors.Text3, formats.Small);
        ds.DrawText(commit.Sha.ToShortString(), Cell(columns.ShaX, columns.Sha, top), colors.Text3, formats.Mono);
    }

    /// <summary>Draws a ref badge (mockup <c>refBadge</c>) and returns its width.</summary>
    private static float DrawBadge(CanvasDrawingSession ds, ICanvasResourceCreator device, RefBadge badge, float x, float center,
        float maxWidth, Color laneColor, TextFormats formats, ThemeColors colors, CanvasStrokeStyle dashed)
    {
        const float height = BadgeHeight, padding = 6, icon = 12, gap = 4;
        var glyph = badge.Kind switch
        {
            RefBadgeKind.Head => FluentIcons.Checkmark,
            RefBadgeKind.Branch => FluentIcons.Desktop,
            RefBadgeKind.Remote => FluentIcons.Cloud,
            _ => FluentIcons.Tag,
        };
        var icons = badge.IsOnRemote ? 2 : 1;
        var iconsWidth = icons * icon + (icons - 1) * gap;
        var top = center - height / 2;

        using var label = new CanvasTextLayout(device, badge.Label, formats.Badge, Math.Max(0, maxWidth - 2 * padding - iconsWidth - gap), height);
        var width = Math.Min(maxWidth, 2 * padding + iconsWidth + gap + (float)label.LayoutBounds.Width);
        var rect = new Rect(x, top, width, height);

        var soft = Color.FromArgb(56, laneColor.R, laneColor.G, laneColor.B);
        switch (badge.Kind)
        {
            case RefBadgeKind.Head:
                ds.FillRoundedRectangle(rect, 4, 4, soft);
                ds.DrawRoundedRectangle(Inset(rect, 1), 3, 3, laneColor, 2);
                break;
            case RefBadgeKind.Branch:
                ds.FillRoundedRectangle(rect, 4, 4, soft);
                ds.DrawRoundedRectangle(Inset(rect, 0.5f), 4, 4, laneColor, 1);
                break;
            case RefBadgeKind.Remote:
                ds.DrawRoundedRectangle(Inset(rect, 0.5f), 4, 4, laneColor, 1, dashed);
                break;
            default:
                ds.FillRoundedRectangle(rect, 4, 4, colors.Ctrl);
                ds.DrawRoundedRectangle(Inset(rect, 0.5f), 4, 4, colors.StrokeStrong, 1);
                break;
        }

        var iconX = x + padding;
        ds.DrawText(glyph, new Rect(iconX, top, icon, height), colors.Text, formats.Icon);
        if (badge.IsOnRemote)
            ds.DrawText(FluentIcons.Cloud, new Rect(iconX + icon + gap, top, icon, height), colors.Text, formats.Icon);
        ds.DrawTextLayout(label, iconX + iconsWidth + gap, top, colors.Text);
        return width;
    }

    private const float BadgeHeight = 18;

    private static Rect Inset(Rect rect, float by) => new(rect.X + by, rect.Y + by, rect.Width - 2 * by, rect.Height - 2 * by);

    private static Color LaneColor(int lane) => Appearance.Current.Lanes[LanePalette.ColorIndexFor(lane)];

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0])),
        };
    }

    /// <summary>Text of a cell: inset by the cell padding, clipped (with an ellipsis) before its right border.</summary>
    private static Rect Cell(float x, float width, float top) => new(x + CellPadding, top, Math.Max(0, width - 2 * CellPadding), RowHeight);

    /// <summary><see cref="GraphColumnLayout"/> in floats for Win2D.</summary>
    private readonly record struct Columns(float Graph, float Message, float Author, float Date, float Sha)
    {
        public Columns(GraphColumnLayout layout)
            : this((float)layout.Graph, (float)layout.Message, (float)layout.Author, (float)layout.Date, (float)layout.Sha)
        {
        }

        public float MessageX => Graph;

        public float AuthorX => MessageX + Message;

        public float DateX => AuthorX + Author;

        public float ShaX => DateX + Date;
    }

    private readonly record struct ThemeColors(
        Color Text, Color Text2, Color Text3, Color Sunken, Color Accent, Color AccentSoft, Color SubHover, Color Ctrl, Color StrokeStrong,
        Color Warning)
    {
        public static ThemeColors For(ElementTheme theme) => theme == ElementTheme.Light
            ? new(DesignTokens.Light.Text, DesignTokens.Light.Text2, DesignTokens.Light.Text3, DesignTokens.Light.Sunken,
                DesignTokens.Light.Accent, DesignTokens.Light.AccentSoft, DesignTokens.Light.SubHover, DesignTokens.Light.Ctrl,
                DesignTokens.Light.StrokeStrong, DesignTokens.Light.Warning)
            : new(DesignTokens.Dark.Text, DesignTokens.Dark.Text2, DesignTokens.Dark.Text3, DesignTokens.Dark.Sunken,
                DesignTokens.Dark.Accent, DesignTokens.Dark.AccentSoft, DesignTokens.Dark.SubHover, DesignTokens.Dark.Ctrl,
                DesignTokens.Dark.StrokeStrong, DesignTokens.Dark.Warning);
    }

    /// <summary>DirectWrite text formats, created on first draw and kept until <see cref="Release"/>.</summary>
    private sealed class TextFormats : IDisposable
    {
        private static string TextFont => Appearance.Current.UiFontFamily;

        private static readonly string IconFontUri =
            new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "FluentSystemIcons-Regular.ttf")).AbsoluteUri + "#FluentSystemIcons-Regular";

        public CanvasTextFormat Body { get; } = Create(TextFont, 13);
        public CanvasTextFormat BodyStrong { get; } = Create(TextFont, 13, bold: true);
        public CanvasTextFormat Italic { get; } = Create(TextFont, 13, italic: true);
        public CanvasTextFormat Small { get; } = Create(TextFont, 12);
        public CanvasTextFormat Mono { get; } = Create("Cascadia Mono", 12);
        public CanvasTextFormat Badge { get; } = Create(TextFont, 11, bold: true);
        public CanvasTextFormat Avatar { get; } = Create(TextFont, 8, bold: true, centered: true);
        // Win2D does not resolve ms-appx:/// in an unpackaged app (E_INVALIDARG when drawing): absolute file URI instead.
        public CanvasTextFormat Icon { get; } = Create(IconFontUri, 12, centered: true, scale: false);

        public void Dispose()
        {
            Body.Dispose();
            BodyStrong.Dispose();
            Italic.Dispose();
            Small.Dispose();
            Mono.Dispose();
            Badge.Dispose();
            Avatar.Dispose();
            Icon.Dispose();
        }

        private static CanvasTextFormat Create(string family, float size, bool bold = false, bool centered = false, bool italic = false, bool scale = true) => new()
        {
            FontFamily = family,
            FontSize = scale ? Appearance.Scaled(size) : size,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            HorizontalAlignment = centered ? CanvasHorizontalAlignment.Center : CanvasHorizontalAlignment.Left,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
        };
    }
}
