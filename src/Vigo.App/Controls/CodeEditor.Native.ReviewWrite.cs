using LoomEditor.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.Foundation;
using Loom = LoomEditor.WinUI.Controls.LoomEditor;
using LoomDiffSide = LoomEditor.WinUI.Controls.DiffSide;
using LoomRange = LoomEditor.Core.Text.Range;

namespace Vigo.App.Controls;

/// <summary>
/// Writing in the native review (ADR-090, review step R2), as the page's review.js did: "Résoudre" / "Rouvrir" and the reply box
/// of a thread, the box of a new comment on a line or a selection, "Retirer" on a kept comment, the proposals after #, ! and @.
/// The boxes are created again with their cards on each layout: what they hold lives here (<see cref="ReviewBoxState"/>).
/// A write or a search for proposals gets a key: its answer is dropped when the box that asked was laid out again meanwhile.
/// </summary>
public sealed partial class CodeEditor
{
    private const string ComposerKey = "composer";

    /// <summary>The box of a new comment: where it is (lines of the version, columns) and the code a suggestion replaces.</summary>
    private sealed class ReviewComposer(
        string key, ReviewSide side, int startLine, int line, int startColumn, int endColumn, string? suggestion, ReviewBoxState state)
    {
        /// <summary>File, side and lines: a box closed with text gets it back when the same range is commented again.</summary>
        public string Key { get; } = key;
        public ReviewSide Side { get; } = side;
        public int StartLine { get; } = startLine;
        public int Line { get; } = line;
        public int StartColumn { get; } = startColumn;
        public int EndColumn { get; } = endColumn;
        public string? Suggestion { get; } = suggestion;
        public ReviewBoxState State { get; } = state;
    }

    private string? _reviewFile;
    private bool _canComment;
    private bool _canDraft;
    private bool _canComplete;
    private IReadOnlyList<(int Index, ReviewDraftRow Row)> _drafts = [];
    private ReviewComposer? _composer;
    // The reply text of each thread (kept when its box is closed, as in the page) and the threads whose box is open.
    private readonly Dictionary<string, ReviewBoxState> _replies = [];
    private readonly HashSet<string> _openReplies = [];
    private readonly HashSet<string> _resolving = [];
    private readonly Dictionary<int, Action<bool>> _reviewWrites = [];
    // The text of new comments closed unsent (another file, Échap), by file, side and lines.
    private readonly Dictionary<string, ReviewBoxState> _composerTexts = [];
    private int _lastReviewKey;
    // Key of the box to focus when the cards are laid out (just opened, or typed in before the layout).
    private string? _focusBox;
    private int _commentHoverLine;
    // "Commenter" under the end of a selection (as the page's selection button), and the editor it is for.
    private Popup? _selectionPopup;
    private Loom? _selectionEditor;
    private ReviewSide? _selectionSide;
    private (string? Family, double Size, double Width) _charWidth;

    private int NextReviewKey() => --_lastReviewKey;

    private static string ReplyKey(string threadId) => "reply:" + threadId;

    private static string RangeText(int? startLine, int line)
    {
        var labels = EditorReviewLabels.Current;
        return startLine is { } start && start != line ? $"{labels.Lines} {start}–{line}" : $"{labels.Line} {line}";
    }

    private void NativeReviewDone(int key, bool ok)
    {
        if (_reviewWrites.Remove(key, out var done)) done(ok);
    }

    /// <summary>The proposals go to the box on screen that still waits for them (none after a new layout: dropped).</summary>
    private void ShowNativeReferences(int key, IReadOnlyList<ReferenceSuggestion> items)
    {
        foreach (var surface in (ReadOnlySpan<ReviewSurface>)[_unifiedReview, _originalReview, _modifiedReview])
            foreach (var box in surface.Boxes.Values)
                if (box.ReferenceKey == key) box.ShowReferences(key, items);
    }

    /// <summary>Closes the box of a new comment; its text is kept for the same range.</summary>
    private void CloseComposer()
    {
        if (_composer is not { } composer) return;
        if (composer.State.BusyKey == 0 && composer.State.Text.Trim().Length > 0) _composerTexts[composer.Key] = composer.State;
        _composer = null;
    }

    /// <summary>The native editor is cleared or released: no box, no write waited for.</summary>
    private void ClearNativeReview()
    {
        _reviewWrites.Clear();
        _replies.Clear();
        _openReplies.Clear();
        _resolving.Clear();
        _composerTexts.Clear();
        _composer = null;
        _reviewFile = null;
        HideSelectionButton();
        foreach (var surface in (ReadOnlySpan<ReviewSurface>)[_unifiedReview, _originalReview, _modifiedReview]) surface.Boxes.Clear();
    }

    private ReviewMarkdownBox NewBox(
        ReviewSurface surface, string key, ReviewBoxState state, string placeholder, string primary, string? secondary, string? suggestion)
    {
        var box = new ReviewMarkdownBox(state, placeholder, primary, secondary, suggestion, _canComplete, _reviewReferences);
        box.ReferencesRequested += (_, request) =>
        {
            var referenceKey = NextReviewKey();
            box.ReferenceKey = referenceKey;
            ReferenceRequested?.Invoke(this, new EditorReferenceRequest(referenceKey, request.Kind, request.Query));
        };
        surface.Boxes[key] = box;
        if (_focusBox == key) box.FocusWhenShown();
        return box;
    }

    /// <summary>The key of the box that has the focus, if any.</summary>
    private string? FocusedBox()
    {
        if (XamlRoot is null) return null;
        for (var o = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; o is not null; o = VisualTreeHelper.GetParent(o))
        {
            if (o is not ReviewMarkdownBox box) continue;
            foreach (var surface in (ReadOnlySpan<ReviewSurface>)[_unifiedReview, _originalReview, _modifiedReview])
                foreach (var (key, shown) in surface.Boxes)
                    if (ReferenceEquals(shown, box)) return key;
            return null;
        }
        return null;
    }

    // ---------------------------------------------------------------- threads

    private void AddThreadActions(ReviewSurface surface, ReviewThreadCard card)
    {
        var row = card.Row;
        if (!row.CanReview) return;
        var id = row.Thread.Id;
        ReviewMarkdownBox? box = null;
        if (_openReplies.Contains(id))
        {
            var labels = EditorReviewLabels.Current;
            var state = _replies[id];
            box = NewBox(surface, ReplyKey(id), state, labels.ReplyPlaceholder, labels.Reply, null, null);
            box.Submitted += (_, _) => SubmitReply(id, state, box.Body);
            box.Canceled += (_, _) =>
            {
                _openReplies.Remove(id);
                QueueReviewLayout();
            };
        }
        card.ShowActions(_resolving.Contains(id), box);
        card.ReplyOpened += (_, _) =>
        {
            _replies.TryAdd(id, new ReviewBoxState());
            _openReplies.Add(id);
            _focusBox = ReplyKey(id);
            QueueReviewLayout();
        };
        card.ResolveClicked += (_, _) =>
        {
            if (!_resolving.Add(id)) return;
            var key = NextReviewKey();
            _reviewWrites[key] = _ =>
            {
                // The thread's new state comes with the next review.
                _resolving.Remove(id);
                QueueReviewLayout();
            };
            QueueReviewLayout();
            ReviewResolveRequested?.Invoke(this, new EditorReviewResolve(key, id, !row.IsResolved));
        };
    }

    private void SubmitReply(string threadId, ReviewBoxState state, string body)
    {
        if (state.BusyKey != 0 || body.Length == 0) return;
        var key = state.BusyKey = NextReviewKey();
        _reviewWrites[key] = ok =>
        {
            state.BusyKey = 0;
            if (ok)
            {
                state.Text = string.Empty;
                state.SelectionStart = state.SelectionLength = 0;
                _openReplies.Remove(threadId);
            }
            else
            {
                // The text stays, ready to be sent again.
                _focusBox = ReplyKey(threadId);
            }
            QueueReviewLayout();
        };
        QueueReviewLayout();
        ReviewReplied?.Invoke(this, new EditorReviewReply(key, threadId, body));
    }

    // ---------------------------------------------------------------- new comment

    private void SubmitComment(ReviewComposer composer, bool draft, string body)
    {
        var state = composer.State;
        if (state.BusyKey != 0 || body.Length == 0) return;
        var key = state.BusyKey = NextReviewKey();
        _reviewWrites[key] = ok =>
        {
            state.BusyKey = 0;
            if (!ReferenceEquals(_composer, composer)) return;
            if (ok)
            {
                _composer = null;
                _composerTexts.Remove(composer.Key);
            }
            else
            {
                _focusBox = ComposerKey;
            }
            QueueReviewLayout();
        };
        QueueReviewLayout();
        ReviewSubmitted?.Invoke(this, new EditorReviewSubmit(
            key, draft, composer.Side, composer.StartLine, composer.Line, composer.StartColumn, composer.EndColumn, body));
    }

    /// <summary>
    /// Opens the box of a new comment on lines <paramref name="startLine"/>–<paramref name="line"/> of a version, shown by the
    /// model lines <paramref name="modelStart"/>–<paramref name="modelEnd"/> of <paramref name="editor"/>.
    /// </summary>
    private void OpenComposer(Loom editor, ReviewSide side, int startLine, int line, int modelStart, int modelEnd, int startColumn, int endColumn)
    {
        if (!_canComment || startLine <= 0 || line < startLine || _composer is { State.BusyKey: not 0 }) return;
        // The code a suggestion replaces: the commented text of the new version.
        var suggestion = side == ReviewSide.New
            ? editor.Model.GetValueInRange(new LoomRange(modelStart, startColumn, modelEnd, endColumn), EndOfLinePreference.LF)
            : null;
        CloseComposer();
        HideSelectionButton();
        var key = $"{_reviewFile}|{side}|{startLine}|{line}";
        var state = _composerTexts.Remove(key, out var kept) ? kept : new ReviewBoxState();
        _composer = new ReviewComposer(key, side, startLine, line, startColumn, endColumn, suggestion, state);
        _focusBox = ComposerKey;
        QueueReviewLayout();
    }

    /// <summary>"Commenter" on a right click: the selection, or the line of the caret.</summary>
    private MenuFlyout CommentFlyout(Loom editor, ReviewSide? side)
    {
        var item = new MenuFlyoutItem();
        item.Click += (_, _) =>
        {
            var range = editor.Selections[0].Range;
            if (side is { } fixedSide) CommentSideRange(editor, fixedSide, range);
            else CommentUnifiedRange(range);
        };
        var flyout = new MenuFlyout();
        flyout.Items.Add(item);
        flyout.Opening += (_, _) =>
        {
            var labels = EditorReviewLabels.Current;
            item.Text = editor.Selections[0].IsEmpty ? labels.CommentOnLine : labels.CommentOnSelection;
        };
        return flyout;
    }

    /// <summary>The selection button follows the selection of <paramref name="editor"/> (a side of the diff, or the unified one).</summary>
    private void WatchSelection(Loom editor, ReviewSide? side)
    {
        editor.SelectionChanged += (_, _) => UpdateSelectionButton(editor, side);
        editor.ScrollChanged += (_, _) => UpdateSelectionButton(editor, side);
        editor.Unloaded += (_, _) => HideSelectionButton();
    }

    /// <summary>
    /// Shows "Commenter" under the end of a non-empty selection, while the review can be commented and no box of a new comment is
    /// open; hidden once the end is scrolled out of the editor. The x of a column is not public in LoomEditor
    /// (LayoutInfo and AutomationGeometry are internal in 0.2.0-alpha.5): it is computed from the width of a character of the
    /// monospace font, tabs expanded; wrapped lines put it under the first row of the end line.
    /// </summary>
    private void UpdateSelectionButton(Loom editor, ReviewSide? side)
    {
        var selection = editor.Selections[0];
        if (selection.IsEmpty || !_canComment || _composer is not null || !editor.IsLoaded || editor.XamlRoot is null)
        {
            // An empty selection elsewhere (the other side) leaves the button of this one.
            if (_selectionEditor is null || ReferenceEquals(_selectionEditor, editor)) HideSelectionButton();
            return;
        }
        var end = selection.Range.End;
        var y = editor.GetTopForLineNumber(end.LineNumber, false) - editor.ScrollTop + editor.LineHeight + 2;
        if (y < 0 || y > editor.ActualHeight - editor.LineHeight)
        {
            HideSelectionButton();
            return;
        }
        var text = editor.Model.GetLineContent(end.LineNumber);
        var column = 0;
        for (var i = 0; i < Math.Min(end.Column - 1, text.Length); i++)
            column = text[i] == '\t' ? (column / editor.TabSize + 1) * editor.TabSize : column + 1;
        var x = Math.Clamp(TextLeft(editor) + column * CharWidth(editor) - editor.ScrollLeft, 0, Math.Max(0, editor.ActualWidth - 160));

        if (_selectionPopup is null)
        {
            var button = new ReviewSelectionButton();
            button.Invoked += (_, _) =>
            {
                if (_selectionEditor is not { } owner) return;
                var chosen = owner.Selections[0].Range;
                if (_selectionSide is { } fixedSide) CommentSideRange(owner, fixedSide, chosen);
                else CommentUnifiedRange(chosen);
            };
            _selectionPopup = new Popup { Child = button };
        }
        _selectionEditor = editor;
        _selectionSide = side;
        var at = editor.TransformToVisual(null).TransformPoint(new Point(x, y));
        _selectionPopup.XamlRoot = editor.XamlRoot;
        ((FrameworkElement)_selectionPopup.Child).RequestedTheme = ActualTheme;
        _selectionPopup.HorizontalOffset = at.X;
        _selectionPopup.VerticalOffset = at.Y;
        _selectionPopup.IsOpen = true;
    }

    private void HideSelectionButton()
    {
        _selectionEditor = null;
        _selectionPopup?.IsOpen = false;
    }

    /// <summary>
    /// Where the text starts in <paramref name="editor"/>: read from a zone on screen (zones start there), else none for the unified
    /// diff (no margin of its own) and a guess for the line numbers of a side (as <see cref="CardWidth"/>).
    /// </summary>
    private double TextLeft(Loom editor)
    {
        var surface = ReferenceEquals(editor, _unified) ? _unifiedReview
            : _diff is { IsLoaded: true } diff && ReferenceEquals(editor, diff.Original) ? _originalReview : _modifiedReview;
        var host = ReferenceEquals(surface.Editor, editor)
            ? surface.Entries.Select(e => e.Card.Parent as UIElement).FirstOrDefault(h => h?.XamlRoot is not null)
            : null;
        return host is not null ? Math.Max(0, host.TransformToVisual(editor).TransformPoint(default).X)
            : ReferenceEquals(editor, _unified) ? 0 : 60;
    }

    /// <summary>Width of a character of the editor's (monospace) font, measured once per font and size.</summary>
    private double CharWidth(Loom editor)
    {
        var (family, size) = (editor.FontFamily.Source, editor.FontSize);
        if (_charWidth.Family != family || _charWidth.Size != size)
        {
            var probe = new TextBlock { Text = new string('0', 20), FontFamily = editor.FontFamily, FontSize = size };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _charWidth = (family, size, probe.DesiredSize.Width / 20);
        }
        return _charWidth.Width;
    }

    /// <summary>LoomEditor's "+" (or its add-comment command) on a side of the side-by-side diff.</summary>
    private void OnDiffAddCommentRequested(LoomDiffSide diffSide, int startLine, int line, LoomRange? textRange)
    {
        if (_diff is not { IsLoaded: true } diff) return;
        var (editor, side) = diffSide == LoomDiffSide.Original ? (diff.Original, ReviewSide.Old) : (diff.Modified, ReviewSide.New);
        if (textRange is { } range) CommentSideRange(editor, side, range);
        else OpenComposer(editor, side, startLine, line, startLine, line, 1, editor.Model.GetLineMaxColumn(line));
    }

    /// <summary>A range of a side of the side-by-side diff (an empty one: its whole line), as the page's rangeOfSelection.</summary>
    private void CommentSideRange(Loom editor, ReviewSide side, LoomRange range)
    {
        var model = editor.Model;
        if (range.IsEmpty)
        {
            var at = range.StartLineNumber;
            OpenComposer(editor, side, at, at, at, at, 1, model.GetLineMaxColumn(at));
            return;
        }
        var last = range.EndColumn == 1 && range.EndLineNumber > range.StartLineNumber ? range.EndLineNumber - 1 : range.EndLineNumber;
        OpenComposer(editor, side, range.StartLineNumber, last, range.StartLineNumber, last, range.StartColumn,
            last == range.EndLineNumber ? range.EndColumn : model.GetLineMaxColumn(last));
    }

    /// <summary>The version a line of the unified diff belongs to: a deleted line is old, a context or added line new; a note none.</summary>
    private ReviewSide? UnifiedSide(int line) =>
        _doc is not { } doc || line < 1 || line > doc.Kinds.Length ? null
        : doc.Kinds[line - 1] switch { DiffDocument.Deleted => ReviewSide.Old, DiffDocument.Note => null, _ => ReviewSide.New };

    private int UnifiedNumber(ReviewSide side, int line) => (side == ReviewSide.Old ? _doc!.OldNumbers : _doc!.NewNumbers)[line - 1];

    /// <summary>
    /// A range of the unified diff (an empty one: its whole line) to lines of one version, as the page's rangeOfSelection: the
    /// version of its last line, the lines of the other version at its start left out.
    /// </summary>
    private void CommentUnifiedRange(LoomRange range)
    {
        if (_unified is not { } editor || _doc is null) return;
        var model = editor.Model;
        var end = range.EndColumn == 1 && range.EndLineNumber > range.StartLineNumber ? range.EndLineNumber - 1 : range.EndLineNumber;
        while (end >= range.StartLineNumber && UnifiedSide(end) is null) end--;
        if (end < range.StartLineNumber || UnifiedSide(end) is not { } side) return;
        var start = range.StartLineNumber;
        while (start < end && UnifiedNumber(side, start) == 0) start++;
        var whole = range.IsEmpty;
        var startColumn = !whole && start == range.StartLineNumber ? range.StartColumn : 1;
        var endColumn = !whole && end == range.EndLineNumber ? range.EndColumn : model.GetLineMaxColumn(end);
        OpenComposer(editor, side, UnifiedNumber(side, start), UnifiedNumber(side, end), start, end, startColumn, endColumn);
    }

    /// <summary>A click on a line of the unified gutter: the selection when it covers the line, else the line.</summary>
    private void CommentUnifiedLine(int line)
    {
        if (_unified is not { } editor) return;
        var selection = editor.Selections[0].Range;
        CommentUnifiedRange(!selection.IsEmpty && selection.StartLineNumber <= line && line <= selection.EndLineNumber
            ? selection
            : new LoomRange(line, 1, line, 1));
    }

    /// <summary>The "+" of the unified gutter follows the pointer over the lines that can be commented.</summary>
    private void OnDiffGutterMoved(object sender, PointerRoutedEventArgs e)
    {
        var line = _staging is null && _canComment ? UnifiedGutterLine(e) : 0;
        if (line > 0 && UnifiedSide(line) is null) line = 0;
        if (line == _commentHoverLine) return;
        _commentHoverLine = line;
        _unifiedGutter?.Invalidate();
    }

    private void OnDiffGutterExited(object sender, PointerRoutedEventArgs e)
    {
        if (_commentHoverLine == 0) return;
        _commentHoverLine = 0;
        _unifiedGutter?.Invalidate();
    }
}
