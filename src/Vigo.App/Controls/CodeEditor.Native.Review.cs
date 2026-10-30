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
/// Discussions of a pull request in the native diffs (ADR-090, review step R1, read only): a <see cref="ReviewThreadCard"/> in a
/// view zone under the line each thread is about, and its range highlighted. A thread whose line the unified diff does not show
/// goes after the last line. A card is measured when its zone is added; afterwards the zone follows its real height
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
    private sealed class ReviewEntry(ReviewThreadCard card, string zone, int after, double height)
    {
        public ReviewThreadCard Card { get; } = card;
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

    private void SetNativeReview(IReadOnlyList<ThreadRow> threads, string? revealThread, int revealToken)
    {
        _threads = threads;
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
            if (_doc is not null && _unified is { } unified) AddUnifiedZones(unified);
            LayoutSideReview();
        });
    }

    private void LayoutSideReview()
    {
        // Original / Modified throw until the template is applied: laid out again on Loaded.
        if (_diff is not { IsLoaded: true } diff || _diffPath is null) return;
        var onScreen = ReferenceEquals(Content, diff);
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
    /// Adds a zone with a new card for each thread (of <paramref name="side"/> only, or all), and highlights the ranges. The zone
    /// ids go to <paramref name="zones"/>, the heights to <paramref name="zoneAbove"/> (keyed by the line under the zone) when given.
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
        }
        surface.ZoneAbove = zoneAbove;
        surface.Entries.Clear();
        var width = CardWidth(editor, null);
        var ranges = new List<EditorDecoration>();
        var count = editor.Model.LineCount;
        foreach (var row in _threads)
        {
            var thread = row.Thread;
            if (side is { } only && thread.Side != only) continue;
            var line = thread.Line is { } l ? modelLine(thread.Side, l) : null;
            var start = thread.Line is { } last ? modelLine(thread.Side, thread.StartLine ?? last) : null;
            if (line is { } end && start is { } first)
            {
                ranges.Add(thread.StartColumn is { } sc && thread.EndColumn is { } ec
                    ? new EditorDecoration(new LoomRange(first, sc, end, ec), ReviewRange)
                    : new EditorDecoration(new LoomRange(first, 1, end, 1), ReviewLines));
            }

            // A new card and host per zone: an element left in a removed zone may still have it as parent (crash 0x800f1000).
            var card = new ReviewThreadCard(row) { Width = width };
            card.Show(outsideDiff: line is null, _expandedThreads.Contains(thread.Id));
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
            card.Toggled += (_, _) =>
            {
                if (!_expandedThreads.Remove(thread.Id)) _expandedThreads.Add(thread.Id);
                card.Show(card.OutsideDiff, _expandedThreads.Contains(thread.Id));
            };

            if (onScreen && thread.Id == _revealThread && _revealToken > 0 && _revealToken != _revealedToken)
            {
                _revealedToken = _revealToken;
                editor.ScrollTop = Math.Max(0, editor.GetTopForLineNumber(Math.Max(1, after), false) - editor.ActualHeight / 3);
            }
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
