using LoomEditor.View.Decorations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.Foundation;
using Loom = LoomEditor.WinUI.Controls.LoomEditor;
using LoomRange = LoomEditor.Core.Text.Range;

namespace Vigo.App.Controls;

/// <summary>
/// Discussions of a pull request in the native diffs (ADR-090, review steps R1 and R2): a <see cref="ReviewThreadCard"/> in a
/// view zone under the line each thread is about, and its range highlighted; the comments kept for the review and the box of a
/// new comment the same way (<see cref="ReviewNoteCard"/>; writing: CodeEditor.Native.ReviewWrite.cs). A thread whose line the
/// unified diff does not show goes after the last line. A card is measured when its zone is added; afterwards the zone follows its real height
/// (<c>SetViewZoneHeight</c>: pictures loaded, thread unfolded, new width) without being laid out again.
/// </summary>
public sealed partial class CodeEditor
{
    private const double CardGap = 8;
    private const double CardLeft = 12;
    private const double CardMinWidth = 160;
    private const double CardMaxWidth = 860;

    private static readonly EditorDecorationOptions ReviewRange = new() { ClassName = "vigo-rv-range" };
    private static readonly EditorDecorationOptions ReviewLines = new() { IsWholeLine = true, ClassName = "vigo-rv-range" };

    /// <summary>A card shown in a zone: the zone it is in, the line the zone follows, the card's height.</summary>
    private sealed class ReviewEntry(FrameworkElement card, string zone, int after, double height)
    {
        public FrameworkElement Card { get; } = card;
        public string Zone { get; } = zone;
        public int After { get; } = after;
        public double Height { get; set; } = height;
    }

    /// <summary>The cards of one editor.</summary>
    private sealed class ReviewSurface
    {
        public readonly List<ReviewEntry> Entries = [];
        public readonly List<string> Zones = [];
        public DecorationsCollection? Decorations;
        public Loom? Editor;
        // The zone heights above the lines (unified diff only: its gutter needs them).
        public Dictionary<int, double>? ZoneAbove;
        // The boxes on screen, by key (the focus goes back to the same box once the cards are laid out again).
        public readonly Dictionary<string, ReviewMarkdownBox> Boxes = [];
        // "Commenter" on a right click (not set while the review cannot be commented).
        public MenuFlyout? Flyout;
    }

    private IReadOnlyList<ThreadRow> _threads = [];
    private readonly HashSet<string> _expandedThreads = [];
    private string? _revealThread;
    private int _revealToken;
    private int _revealedToken;
    private bool _reviewLayoutQueued;
    private readonly ReviewSurface _unifiedReview = new();
    private readonly ReviewSurface _originalReview = new();
    private readonly ReviewSurface _modifiedReview = new();

    private static void RegisterReviewStyles() =>
        CustomDecorationStyles.Register("vigo-rv-range", new CustomDecorationStyle
        {
            Background = ThemeColorOf(Light.Selected), DarkBackground = ThemeColorOf(Dark.Selected),
        });

    private void SetNativeReview(
        string file, bool canComment, bool canDraft, bool canComplete, IReadOnlyList<ThreadRow> threads,
        IReadOnlyList<(int Index, ReviewDraftRow Row)> drafts, string? revealThread, int revealToken)
    {
        // Another file: its boxes are gone (as in the page).
        if (file != _reviewFile)
        {
            CloseComposer();
            _reviewFile = file;
            _replies.Clear();
            _openReplies.Clear();
            _resolving.Clear();
            _expandedThreads.Clear();
        }
        _canComment = canComment;
        _canDraft = canDraft;
        _canComplete = canComplete;
        if (!canComment)
        {
            CloseComposer();
            HideSelectionButton();
        }
        _threads = threads;
        _drafts = drafts;
        _revealThread = revealThread;
        _revealToken = revealToken;
        QueueReviewLayout();
    }

    /// <summary>Lays the cards out again once the current changes are done (several in a row give one layout).</summary>
    private void QueueReviewLayout()
    {
        if (_reviewLayoutQueued || _closed) return;
        _reviewLayoutQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _reviewLayoutQueued = false;
            if (_closed) return;
            // The box being typed in is created again: it gets the focus back, with its caret.
            _focusBox ??= FocusedBox();
            if (_doc is not null && _unified is { } unified) AddUnifiedZones(unified);
            LayoutSideReview();
            _focusBox = null;
        });
    }

    private void LayoutSideReview()
    {
        // Original / Modified throw until the template is applied: laid out again on Loaded.
        if (_diff is not { IsLoaded: true } diff || _diffPath is null) return;
        var onScreen = ReferenceEquals(Content, diff);
        // LoomEditor's "+" on the line numbers raises AddCommentRequested, which opens the box (the editor adds no thread itself).
        diff.Original.CommentGutterButtonEnabled = _canComment;
        diff.Modified.CommentGutterButtonEnabled = _canComment;
        LayoutSide(_originalReview, diff.Original, ReviewSide.Old, onScreen);
        LayoutSide(_modifiedReview, diff.Modified, ReviewSide.New, onScreen);
    }

    private void LayoutSide(ReviewSurface surface, Loom editor, ReviewSide side, bool onScreen)
    {
        foreach (var id in surface.Zones) editor.RemoveViewZone(id);
        surface.Zones.Clear();
        var count = editor.Model.LineCount;
        AddReviewZones(surface, editor, (s, line) => s == side && line <= count ? line : null, side, onScreen, surface.Zones, null);
    }

    /// <summary>The line of the unified document that shows <paramref name="line"/> of a version; null when the diff does not show it.</summary>
    private static int? DocLine(DiffDocument doc, ReviewSide side, int line)
    {
        var numbers = side == ReviewSide.Old ? doc.OldNumbers : doc.NewNumbers;
        for (var i = 0; i < numbers.Count; i++)
            if (numbers[i] == line) return i + 1;
        return null;
    }

    private const double ScrollbarWidth = 14;

    /// <summary>
    /// A card fills the visible text area (line numbers and scroll bar excluded, as the page did), within reason. Its left is read
    /// from a zone host already on screen (the layout of the editor is not public); before that, a guess for the line numbers.
    /// </summary>
    private double CardWidth(Loom editor, UIElement? host)
    {
        // The zones stay put when the text scrolls horizontally.
        var textLeft = host is { XamlRoot: not null } ? Math.Max(0, host.TransformToVisual(editor).TransformPoint(default).X) : 60.0;
        return Math.Clamp(VisibleRight(editor) - textLeft - ScrollbarWidth - 2 * CardLeft, CardMinWidth, CardMaxWidth);
    }

    // Width of the overview ruler of the side-by-side diff (Monaco: ENTIRE_DIFF_OVERVIEW_WIDTH); not public in LoomDiffEditor.
    private const double DiffOverviewWidth = 30;

    /// <summary>Right edge of what is visible of <paramref name="editor"/>: the modified side of the side-by-side diff runs under its overview ruler.</summary>
    private double VisibleRight(Loom editor) =>
        editor.ActualWidth - (_diff is { RenderOverviewRuler: true, IsLoaded: true } diff && ReferenceEquals(editor, diff.Modified) ? DiffOverviewWidth : 0);

    /// <summary>
    /// Adds a zone with a new card for each thread, kept comment and the box of a new comment (of <paramref name="side"/> only, or
    /// all), and highlights their ranges. The zone ids go to <paramref name="zones"/>, the heights to <paramref name="zoneAbove"/>
    /// (keyed by the line under the zone) when given.
    /// </summary>
    private void AddReviewZones(
        ReviewSurface surface, Loom editor, Func<ReviewSide, int, int?> modelLine, ReviewSide? side, bool onScreen,
        List<string> zones, Dictionary<int, double>? zoneAbove)
    {
        if (!ReferenceEquals(surface.Editor, editor))
        {
            surface.Editor = editor;
            surface.Decorations = editor.CreateDecorationsCollection(Array.Empty<EditorDecoration>());
            // A new width wraps the comments differently: the cards take it, their zones follow their height.
            editor.LayoutChanged += (_, _) => FitCards(surface, editor);
            surface.Flyout = CommentFlyout(editor, side);
            WatchSelection(editor, side);
        }
        editor.ContextFlyout = _canComment ? surface.Flyout : null;
        surface.ZoneAbove = zoneAbove;
        surface.Entries.Clear();
        surface.Boxes.Clear();
        var labels = EditorReviewLabels.Current;
        var width = CardWidth(editor, null);
        var ranges = new List<EditorDecoration>();
        var count = editor.Model.LineCount;

        // The model line of an anchor (null when the editor does not show it); its range is highlighted.
        int? Anchor(ReviewSide itemSide, int? line, int? startLine, int? startColumn, int? endColumn)
        {
            if (line is not { } last) return null;
            var (first, end) = (modelLine(itemSide, startLine ?? last), modelLine(itemSide, last));
            if (first is { } f && end is { } e)
            {
                ranges.Add(startColumn is { } sc && endColumn is { } ec
                    ? new EditorDecoration(new LoomRange(f, sc, e, ec), ReviewRange)
                    : new EditorDecoration(new LoomRange(f, 1, e, 1), ReviewLines));
            }
            return end;
        }

        // A new card and host per zone: an element left in a removed zone may still have it as parent (crash 0x800f1000).
        ReviewEntry Place(FrameworkElement card, int? line)
        {
            card.Width = width;
            card.Measure(new Size(width, double.PositiveInfinity));
            var height = card.DesiredSize.Height;
            // The host does not limit the card: its real height is known from SizeChanged.
            var host = new Canvas();
            // Once on screen, the zone tells where the text starts: the first guess of the width is corrected.
            host.Loaded += (_, _) => FitCards(surface, editor);
            Canvas.SetLeft(card, CardLeft);
            Canvas.SetTop(card, CardGap / 2);
            host.Children.Add(card);
            var after = line ?? count;
            var zone = editor.AddViewZone(after, height + CardGap, host);
            zones.Add(zone);
            if (zoneAbove is not null) zoneAbove[after + 1] = zoneAbove.GetValueOrDefault(after + 1) + height + CardGap;
            var entry = new ReviewEntry(card, zone, after, height);
            surface.Entries.Add(entry);
            card.SizeChanged += (_, e) => OnCardResized(surface, editor, entry, e.NewSize.Height);
            return entry;
        }

        foreach (var row in _threads)
        {
            var thread = row.Thread;
            if (side is { } only && thread.Side != only) continue;
            var line = Anchor(thread.Side, thread.Line, thread.StartLine, thread.StartColumn, thread.EndColumn);
            var card = new ReviewThreadCard(row);
            card.Show(outsideDiff: line is null, _expandedThreads.Contains(thread.Id));
            AddThreadActions(surface, card);
            card.ReactionToggled += (_, reaction) => ReviewReactionToggled?.Invoke(this, reaction);
            var entry = Place(card, line);
            card.Toggled += (_, _) =>
            {
                if (!_expandedThreads.Remove(thread.Id)) _expandedThreads.Add(thread.Id);
                card.Show(card.OutsideDiff, _expandedThreads.Contains(thread.Id));
            };

            if (onScreen && thread.Id == _revealThread && _revealToken > 0 && _revealToken != _revealedToken)
            {
                _revealedToken = _revealToken;
                editor.ScrollTop = Math.Max(0, editor.GetTopForLineNumber(Math.Max(1, entry.After), false) - editor.ActualHeight / 3);
            }
        }

        foreach (var (index, row) in _drafts)
        {
            var draft = row.Comment;
            if (side is { } only && draft.Side != only) continue;
            var line = Anchor(draft.Side, draft.Line, draft.StartLine, draft.StartColumn, draft.EndColumn);
            var card = ReviewNoteCard.Draft(RangeText(draft.StartLine, draft.Line), draft.Body);
            card.RemoveClicked += (_, _) => ReviewDraftRemoved?.Invoke(this, index);
            Place(card, line);
        }

        if (_composer is { } composer && (side is null || side == composer.Side))
        {
            var line = Anchor(composer.Side, composer.Line, composer.StartLine, composer.StartColumn, composer.EndColumn);
            var box = NewBox(surface, ComposerKey, composer.State, labels.Placeholder, labels.Comment, _canDraft ? labels.AddToReview : null, composer.Suggestion);
            box.Submitted += (_, draft) => SubmitComment(composer, draft, box.Body);
            box.Canceled += (_, _) =>
            {
                if (!ReferenceEquals(_composer, composer)) return;
                CloseComposer();
                QueueReviewLayout();
            };
            Place(ReviewNoteCard.Composer($"{labels.CommentOn} {RangeText(composer.StartLine, composer.Line)}", box), line);
        }
        surface.Decorations!.Set(ranges);
    }

    private void FitCards(ReviewSurface surface, Loom editor)
    {
        if (surface.Entries.Count == 0) return;
        var width = CardWidth(editor, surface.Entries.Select(e => e.Card.Parent as UIElement).FirstOrDefault(h => h?.XamlRoot is not null));
        foreach (var entry in surface.Entries) entry.Card.Width = width;
    }

    private void OnCardResized(ReviewSurface surface, Loom editor, ReviewEntry entry, double height)
    {
        if (!surface.Entries.Contains(entry) || Math.Abs(entry.Height - height) < 0.5) return;
        if (!editor.SetViewZoneHeight(entry.Zone, height + CardGap)) return;
        if (surface.ZoneAbove is { } above) above[entry.After + 1] = above.GetValueOrDefault(entry.After + 1) + height - entry.Height;
        entry.Height = height;
        if (ReferenceEquals(editor, _unified)) _unifiedGutter?.Invalidate();
    }
}
