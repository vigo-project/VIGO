using LoomEditor.View.Decorations;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using DiffChangeBlock = LoomEditor.View.DiffEditor.DiffChangeBlock;
using Loom = LoomEditor.WinUI.Controls.LoomEditor;
using LoomDiff = LoomEditor.WinUI.Controls.LoomDiffEditor;
using LoomRange = LoomEditor.Core.Text.Range;

namespace Vigo.App.Controls;

/// <summary>
/// Stage / discard bar of the side-by-side diff (as Fork's block header): while the pointer is over a block of changes, a popup
/// holds its buttons at the right edge of the editor that shows the block (the modified one, the original one for a pure
/// deletion), kept in view while the block is. A popup moves no line, so the two sides stay aligned.
/// </summary>
public sealed partial class CodeEditor
{
    // Hide delay after the pointer leaves the editor, so it can reach the bar.
    private static readonly TimeSpan BarHideDelay = TimeSpan.FromMilliseconds(250);
    private const double BarMargin = 22;

    private EditorStaging? _sideStaging;
    private bool _blockBarWired;
    private Popup? _blockPopup;
    private DiffBlockBar? _blockBar;
    private DispatcherQueueTimer? _blockHideTimer;
    private DiffChangeBlock? _blockHover;
    private Loom? _blockEditor;
    private bool _blockBarHot;

    private void WireBlockBar(LoomDiff diff)
    {
        if (_blockBarWired || !diff.IsLoaded) return;
        _blockBarWired = true;
        foreach (var editor in new[] { diff.Original, diff.Modified })
        {
            editor.AddHandler(PointerMovedEvent, new PointerEventHandler((_, e) => OnBlockPointerMoved(diff, editor, e)), true);
            editor.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => ScheduleBlockBarHide()), true);
            editor.ScrollChanged += (_, _) => PlaceBlockBar();
            editor.Unloaded += (_, _) => HideBlockBar();
        }
    }

    private void OnBlockPointerMoved(LoomDiff diff, Loom editor, PointerRoutedEventArgs e)
    {
        if (_sideStaging is null || !diff.IsDiffUpToDate || e.Pointer.IsInContact && e.GetCurrentPoint(editor).Properties.IsLeftButtonPressed)
        {
            // A drag selection (or nothing to stage): no bar.
            if (_sideStaging is null) HideBlockBar();
            return;
        }
        var y = e.GetCurrentPoint(editor).Position.Y + editor.ScrollTop;
        var onOriginal = ReferenceEquals(editor, diff.Original);
        var block = BlockAt(diff, editor, y, onOriginal);
        if (block is null)
        {
            ScheduleBlockBarHide();
            return;
        }
        _blockHideTimer?.Stop();
        if (ReferenceEquals(block, _blockHover) && ReferenceEquals(editor, _blockEditor)) return;
        _blockHover = block;
        _blockEditor = editor;
        ShowBlockBar();
    }

    /// <summary>The block shown at <paramref name="y"/> (document coordinates) of an editor: modified lines, or original ones for a pure deletion.</summary>
    private static DiffChangeBlock? BlockAt(LoomDiff diff, Loom editor, double y, bool onOriginal)
    {
        foreach (var block in diff.Changes)
        {
            var (start, count) = onOriginal ? (block.OriginalStartLineNumber, block.OriginalLineCount) : (block.ModifiedStartLineNumber, block.ModifiedLineCount);
            if (count == 0 || onOriginal && block.ModifiedLineCount > 0) continue;
            var (top, bottom) = BlockSpan(editor, start, count);
            if (y >= top && y < bottom) return block;
        }
        return null;
    }

    private static (double Top, double Bottom) BlockSpan(Loom editor, int start, int count)
    {
        var top = editor.GetTopForLineNumber(start, false);
        var end = start + count;
        var bottom = end <= editor.Model.LineCount ? editor.GetTopForLineNumber(end, false) : editor.GetTopForLineNumber(end - 1, false) + editor.LineHeight;
        return (top, bottom);
    }

    private static readonly EditorDecorationOptions BlockLine = WholeLine("vigo-diff-block");
    private DecorationsCollection? _blockOriginalMarks, _blockModifiedMarks;

    /// <summary>Marks the lines the bar acts on, on both sides, so it is clear what a click sends.</summary>
    private void MarkBlock(DiffChangeBlock? block)
    {
        if (_diff is not { IsLoaded: true } diff) return;
        _blockOriginalMarks ??= diff.Original.CreateDecorationsCollection(Array.Empty<EditorDecoration>());
        _blockModifiedMarks ??= diff.Modified.CreateDecorationsCollection(Array.Empty<EditorDecoration>());
        _blockOriginalMarks.Set(LinesOf(block?.OriginalStartLineNumber ?? 0, block?.OriginalLineCount ?? 0));
        _blockModifiedMarks.Set(LinesOf(block?.ModifiedStartLineNumber ?? 0, block?.ModifiedLineCount ?? 0));
    }

    private static List<EditorDecoration> LinesOf(int start, int count)
    {
        List<EditorDecoration> marks = [];
        for (var line = start; line < start + count; line++) marks.Add(new EditorDecoration(new LoomRange(line, 1, line, 1), BlockLine));
        return marks;
    }

    private void ShowBlockBar()
    {
        if (_sideStaging is not { } staging || _blockEditor is not { XamlRoot: not null } editor) return;
        if (_blockPopup is null)
        {
            var bar = _blockBar = new DiffBlockBar();
            bar.Invoked += OnBlockBarInvoked;
            bar.PointerEntered += (_, _) =>
            {
                _blockBarHot = true;
                _blockHideTimer?.Stop();
            };
            bar.PointerExited += (_, _) =>
            {
                _blockBarHot = false;
                ScheduleBlockBarHide();
            };
            _blockPopup = new Popup { Child = bar };
        }
        _blockBar!.SetLabels(staging.ApplyLabel, staging.DiscardLabel);
        _blockPopup.XamlRoot = editor.XamlRoot;
        _blockBar.RequestedTheme = ActualTheme;
        MarkBlock(_blockHover);
        PlaceBlockBar();
    }

    /// <summary>Puts the bar on the block, at the top of its visible part; hides it once the block is out of view.</summary>
    private void PlaceBlockBar()
    {
        if (_blockHover is not { } block || _blockEditor is not { } editor || _blockPopup is null || _blockBar is null || !editor.IsLoaded) return;
        var onOriginal = _diff is { IsLoaded: true } diff && ReferenceEquals(editor, diff.Original);
        var (top, bottom) = BlockSpan(editor, onOriginal ? block.OriginalStartLineNumber : block.ModifiedStartLineNumber,
            onOriginal ? block.OriginalLineCount : block.ModifiedLineCount);
        top -= editor.ScrollTop;
        bottom -= editor.ScrollTop;
        if (bottom <= 0 || top >= editor.ActualHeight)
        {
            HideBlockBar();
            return;
        }
        _blockBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _blockBar.DesiredSize;
        var x = Math.Max(0, editor.ActualWidth - size.Width - BarMargin);
        var y = Math.Clamp(top, 0, Math.Max(0, Math.Min(bottom - size.Height, editor.ActualHeight - size.Height)));
        var at = editor.TransformToVisual(null).TransformPoint(new Point(x, y));
        _blockPopup.HorizontalOffset = at.X;
        _blockPopup.VerticalOffset = at.Y;
        _blockPopup.IsOpen = true;
    }

    private void ScheduleBlockBarHide()
    {
        if (_blockPopup is not { IsOpen: true } || _blockBarHot) return;
        _blockHideTimer ??= CreateHideTimer();
        _blockHideTimer.Stop();
        _blockHideTimer.Start();
    }

    private DispatcherQueueTimer CreateHideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = BarHideDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!_blockBarHot) HideBlockBar();
        };
        return timer;
    }

    private void HideBlockBar()
    {
        _blockHideTimer?.Stop();
        if (_blockHover is not null) MarkBlock(null);
        _blockHover = null;
        _blockEditor = null;
        _blockBarHot = false;
        _blockPopup?.IsOpen = false;
    }

    private void OnBlockBarInvoked(object? sender, bool discard)
    {
        if (_blockHover is not { } b) return;
        HideBlockBar();
        SideBlockInvoked?.Invoke(this, (b.OriginalStartLineNumber, b.OriginalLineCount, b.ModifiedStartLineNumber, b.ModifiedLineCount, discard));
    }
}
