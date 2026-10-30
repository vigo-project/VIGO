using LoomEditor.View.Decorations;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Services;
using Vigo.Core.Models;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Loom = LoomEditor.WinUI.Controls.LoomEditor;
using LoomRange = LoomEditor.Core.Text.Range;

namespace Vigo.App.Controls;

/// <summary>
/// Native unified diff (ADR-090, step 3): the document lines in a read-only LoomEditor; on its left a gutter drawn in
/// Win2D with the selection box of each changed line (staging), the old and new line numbers and the +/− sign; the hunk
/// headers with their buttons in view zones (<see cref="DiffHunkHeader"/>), the header of the hunk at the top kept
/// visible once its zone is scrolled away, and the staging hint after the last line.
/// </summary>
public sealed partial class CodeEditor
{
    private const double HunkHeight = 30;
    private const double HintHeight = 40;
    private const double CheckWidth = 22;
    private const double SignWidth = 16;

    private static readonly EditorDecorationOptions DiffAddLine = WholeLine("vigo-diff-add");
    private static readonly EditorDecorationOptions DiffDelLine = WholeLine("vigo-diff-del");
    private static readonly EditorDecorationOptions DiffSelectedLine = WholeLine("vigo-diff-selected");
    private static readonly EditorDecorationOptions DiffNote = new() { InlineClassName = "vigo-diff-note" };

    private Grid? _unifiedHost;
    private Loom? _unified;
    private CanvasControl? _unifiedGutter;
    private DiffHunkHeader? _sticky;
    private TextBlock? _hint;
    private DecorationsCollection? _unifiedDecorations;
    private readonly List<string> _unifiedZones = [];
    private DiffDocument? _doc;
    private string? _unifiedPath;
    private EditorStaging? _staging;
    private HashSet<int> _selectedLines = [];
    private HashSet<int> _hunkStarts = [];
    private bool _unifiedRevealPending;

    private static EditorDecorationOptions WholeLine(string className) =>
        new() { IsWholeLine = true, ClassName = className, MarginClassName = className };

    private static void RegisterDiffStyles()
    {
        CustomDecorationStyles.Register("vigo-diff-add", new CustomDecorationStyle
        {
            Background = ThemeColorOf(Light.Add), DarkBackground = ThemeColorOf(Dark.Add),
        });
        CustomDecorationStyles.Register("vigo-diff-del", new CustomDecorationStyle
        {
            Background = ThemeColorOf(Light.Del), DarkBackground = ThemeColorOf(Dark.Del),
        });
        CustomDecorationStyles.Register("vigo-diff-selected", new CustomDecorationStyle
        {
            Background = ThemeColorOf(Light.Selected), DarkBackground = ThemeColorOf(Dark.Selected),
        });
        CustomDecorationStyles.Register("vigo-diff-note", new CustomDecorationStyle
        {
            Foreground = ThemeColorOf(Light.Text3), DarkForeground = ThemeColorOf(Dark.Text3),
        });
    }

    private Loom EnsureUnified()
    {
        if (_unified is not null) return _unified;
        // No margin of its own: the gutter drawn beside it holds the numbers, boxes and signs (as the page's margin did).
        var editor = _unified = new Loom { IsReadOnly = true, MinimapEnabled = false, FoldingEnabled = false, LineDecorationsWidth = 0 };
        editor.ScrollChanged += (_, _) => OnUnifiedScrolled();
        editor.LayoutChanged += (_, _) => OnUnifiedScrolled();
        editor.PreviewKeyDown += OnUnifiedPreviewKeyDown;
        editor.Loaded += (_, _) =>
        {
            ApplyNativeTheme();
            if (!_unifiedRevealPending) return;
            _unifiedRevealPending = false;
            RevealFirstDiffChange();
        };
        _unifiedDecorations = editor.CreateDecorationsCollection(Array.Empty<EditorDecoration>());

        // Width set by the first draw, once the font is measured (an Auto column with no width would never draw).
        var gutter = _unifiedGutter = new CanvasControl { Width = 1 };
        gutter.Draw += OnDrawDiffGutter;
        gutter.PointerPressed += OnDiffGutterPressed;
        gutter.PointerWheelChanged += (_, e) => ScrollWithWheel(editor, gutter, e);

        _sticky = new DiffHunkHeader { VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        _sticky.Invoked += OnHunkHeaderInvoked;

        var host = _unifiedHost = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.Children.Add(gutter);
        Grid.SetColumn(editor, 1);
        host.Children.Add(editor);
        Grid.SetColumn(_sticky, 1);
        host.Children.Add(_sticky);
        ApplyNativeTheme();
        return editor;
    }

    private void ShowNativeDiff(string path, DiffDocument document, IReadOnlyList<int> selected, EditorStaging? staging, bool keepScroll)
    {
        var editor = EnsureUnified();
        var keep = keepScroll && _doc is not null && ReferenceEquals(Content, _unifiedHost);
        var (top, left) = (editor.ScrollTop, editor.ScrollLeft);

        _doc = document;
        _unifiedPath = path;
        _staging = staging;
        _selectedLines = [.. selected];
        _hunkStarts = [.. document.Hunks.Select(h => h.FirstLine)];

        // Zones first: they follow the edits of the text being replaced.
        RemoveDiffZones(editor);
        editor.LanguageId = LanguageOf(path);
        editor.ResetText(document.Text);
        for (var i = 0; i < document.Hunks.Count; i++)
        {
            var header = new DiffHunkHeader();
            header.Show(i, document.Hunks[i].Header, staging);
            header.Invoked += OnHunkHeaderInvoked;
            _unifiedZones.Add(editor.AddViewZone(document.Hunks[i].FirstLine - 1, HunkHeight, header));
        }
        if (staging is { Hint.Length: > 0 } && document.Hunks.Count > 0)
        {
            // A new element per zone: one moved out of a removed zone may still have its old parent (crash 0x800f1000).
            _hint = new TextBlock
            {
                Text = staging.Hint,
                Padding = new Thickness(16, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush((ActualTheme == ElementTheme.Light ? Light : Dark).Text3),
            };
            _unifiedZones.Add(editor.AddViewZone(editor.Model.LineCount, HintHeight, _hint));
        }
        RenderDiffDecorations();
        ToolTipService.SetToolTip(_unifiedGutter, staging?.ToggleTip);
        ShowNative(_unifiedHost!);

        if (keep)
        {
            editor.ScrollTop = top;
            editor.ScrollLeft = left;
        }
        else if (editor.IsLoaded)
        {
            RevealFirstDiffChange();
        }
        else
        {
            _unifiedRevealPending = true;
        }
        OnUnifiedScrolled(force: true);
    }

    private void RemoveDiffZones(Loom editor)
    {
        foreach (var id in _unifiedZones) editor.RemoveViewZone(id);
        _unifiedZones.Clear();
    }

    /// <summary>A freshly opened diff starts on the hunk of its first change, not on the leading context.</summary>
    private void RevealFirstDiffChange()
    {
        if (_doc is not { } doc || _unified is not { } editor) return;
        editor.ScrollTop = 0;
        var first = doc.Kinds.AsSpan().IndexOfAny(DiffDocument.Added, DiffDocument.Deleted);
        if (first >= 0) RevealNativeHunk(HunkAt(first + 1));
    }

    private void SetNativeDiffSelection(IReadOnlyList<int> lines)
    {
        if (_doc is null) return;
        _selectedLines = [.. lines];
        RenderDiffDecorations();
        _unifiedGutter?.Invalidate();
    }

    private void RevealNativeHunk(int hunk)
    {
        if (_doc is not { } doc || _unified is not { } editor || hunk < 0 || hunk >= doc.Hunks.Count) return;
        editor.ScrollTop = Math.Max(0, editor.GetTopForLineNumber(doc.Hunks[hunk].FirstLine, false) - HunkHeight);
    }

    private bool IsNativeDiffShown => _unifiedHost is not null && ReferenceEquals(Content, _unifiedHost);

    private void ClearNativeDiff()
    {
        if (_unified is not { } editor) return;
        _doc = null;
        _unifiedPath = null;
        _staging = null;
        _selectedLines = [];
        _hunkStarts = [];
        RemoveDiffZones(editor);
        _unifiedDecorations!.Clear();
        editor.ResetText(string.Empty);
        _sticky!.Visibility = Visibility.Collapsed;
        _unifiedGutter?.Invalidate();
    }

    private void ReleaseNativeDiff()
    {
        // Win2D: the device resources are freed when the tab is closed, not on Unloaded.
        _unifiedGutter?.RemoveFromVisualTree();
        _unifiedGutter = null;
    }

    private void ApplyNativeDiffTheme(LoomEditor.Themes.EditorTheme theme, bool dark)
    {
        if (_unified is not { } editor) return;
        ApplyEditorSettings(editor, theme, Appearance.Current.Editor.WordWrap);
        // The old and new numbers are drawn in the gutter.
        editor.LineNumbersEnabled = false;
        if (_unifiedPath is not null) editor.LanguageId = LanguageOf(_unifiedPath);
        _hint?.Foreground = new SolidColorBrush((dark ? Dark : Light).Text3);
        _unifiedGutter?.Invalidate();
    }

    private void RenderDiffDecorations()
    {
        if (_doc is not { } doc || _unified is not { } editor) return;
        var kinds = doc.Kinds;
        var items = new List<EditorDecoration>();
        for (var i = 0; i < kinds.Length; i++)
        {
            var line = i + 1;
            switch (kinds[i])
            {
                case DiffDocument.Note:
                    items.Add(new EditorDecoration(new LoomRange(line, 1, line, editor.Model.GetLineLength(line) + 1), DiffNote));
                    break;
                case DiffDocument.Added or DiffDocument.Deleted:
                    var options = _staging is not null && _selectedLines.Contains(line) ? DiffSelectedLine
                        : kinds[i] == DiffDocument.Added ? DiffAddLine : DiffDelLine;
                    items.Add(new EditorDecoration(new LoomRange(line, 1, line, 1), options));
                    break;
            }
        }
        _unifiedDecorations!.Set(items);
    }

    private void OnHunkHeaderInvoked(object? sender, bool discard)
    {
        if (sender is DiffHunkHeader { Hunk: >= 0 } header) HunkInvoked?.Invoke(this, (header.Hunk, discard));
    }

    /// <summary>S stages the selection or the current hunk, Delete discards it (the other diff keys are accelerators of the view).</summary>
    private void OnUnifiedPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_staging is not { } staging || e.OriginalSource is TextBox or ButtonBase) return;
        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Shift)) return;
        var key = e.Key switch
        {
            VirtualKey.S => "S",
            VirtualKey.Delete when staging.DiscardLabel is not null => "Delete",
            _ => null,
        };
        if (key is null) return;
        e.Handled = true;
        KeyPressed?.Invoke(this, new EditorKey(key, 0, false, false, false));
    }

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ---------------------------------------------------------------- geometry

    /// <summary>Index of the hunk a document line belongs to (hunks are sorted by first line).</summary>
    private int HunkAt(int line)
    {
        var hunks = _doc!.Hunks;
        int lo = 0, hi = hunks.Count - 1, found = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (hunks[mid].FirstLine <= line) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>Height of a line on screen (wrapped rows included, zones excluded).</summary>
    private double RowHeight(Loom editor, int line, double top)
    {
        if (line >= editor.Model.LineCount) return editor.LineHeight;
        var next = editor.GetTopForLineNumber(line + 1, false);
        return next - top - (_hunkStarts.Contains(line + 1) ? HunkHeight : 0);
    }

    /// <summary>The last line whose top is at or above <paramref name="y"/> (scroll coordinates).</summary>
    private static int LineAtOrAbove(Loom editor, double y)
    {
        int lo = 1, hi = editor.Model.LineCount, found = 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (editor.GetTopForLineNumber(mid, false) <= y) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private void OnUnifiedScrolled(bool force = false)
    {
        _unifiedGutter?.Invalidate();
        UpdateSticky(force);
    }

    private int _stickyHunk = -1;

    /// <summary>The header of the hunk at the top stays visible once its own zone has scrolled above the top.</summary>
    private void UpdateSticky(bool force)
    {
        if (_sticky is null || _unified is not { } editor) return;
        var index = -1;
        if (_doc is { Hunks.Count: > 0 } doc && editor.Model.LineCount > 0)
        {
            var hunk = HunkAt(LineAtOrAbove(editor, editor.ScrollTop));
            if (editor.GetTopForLineNumber(doc.Hunks[hunk].FirstLine, false) - HunkHeight < editor.ScrollTop) index = hunk;
        }
        if (!force && index == _stickyHunk) return;
        _stickyHunk = index;
        _sticky.Visibility = index < 0 ? Visibility.Collapsed : Visibility.Visible;
        if (index >= 0) _sticky.Show(index, _doc!.Hunks[index].Header, _staging);
    }

    // ---------------------------------------------------------------- gutter

    private void OnDrawDiffGutter(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var palette = ActualTheme == ElementTheme.Light ? Light : Dark;
        var ds = args.DrawingSession;
        ds.Clear(palette.Background);
        if (_doc is not { } doc || _unified is not { } editor || editor.LineHeight <= 0) return;

        var settings = Appearance.Current.Editor;
        using var numberFormat = new CanvasTextFormat
        {
            FontFamily = editor.FontFamily.Source, FontSize = (float)editor.FontSize, WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        using var signFormat = new CanvasTextFormat
        {
            FontFamily = editor.FontFamily.Source, FontSize = (float)editor.FontSize, WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        // Columns (as in the page): selection box, old number, new number, sign.
        var digits = Math.Max(3, Math.Max(MaxOf(doc.OldNumbers), MaxOf(doc.NewNumbers)).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        using var zero = new CanvasTextLayout(ds, "0", numberFormat, 0, 0);
        var charWidth = (float)zero.LayoutBounds.Width;
        var checkWidth = _staging is null ? 0f : (float)CheckWidth;
        var numberWidth = settings.LineNumbers ? digits * charWidth : 0f;
        var numbersLeft = checkWidth + 4;
        var signLeft = settings.LineNumbers ? numbersLeft + 2 * numberWidth + charWidth + 4 : checkWidth + 4;
        var width = Math.Ceiling(signLeft + SignWidth);
        if (Math.Abs(sender.Width - width) > 0.5)
        {
            // Drawn again at the new width.
            sender.Width = width;
            return;
        }

        var scrollTop = editor.ScrollTop;
        var height = sender.ActualHeight;
        var full = (float)width;

        // Hunk header bands, in line with the zones of the editor.
        foreach (var hunk in doc.Hunks)
        {
            var zoneTop = (float)(editor.GetTopForLineNumber(hunk.FirstLine, false) - HunkHeight - scrollTop);
            if (zoneTop + HunkHeight < 0 || zoneTop > height) continue;
            ds.FillRectangle(0, zoneTop, full, (float)HunkHeight, palette.Header);
            ds.DrawLine(0, zoneTop + 0.5f, full, zoneTop + 0.5f, palette.Stroke);
            ds.DrawLine(0, zoneTop + (float)HunkHeight - 0.5f, full, zoneTop + (float)HunkHeight - 0.5f, palette.Stroke);
        }

        var count = editor.Model.LineCount;
        for (var line = LineAtOrAbove(editor, scrollTop); line <= count; line++)
        {
            var lineTop = editor.GetTopForLineNumber(line, false);
            var top = (float)(lineTop - scrollTop);
            if (top > height) break;
            var h = (float)RowHeight(editor, line, lineTop);
            var rowH = (float)editor.LineHeight;
            var i = line - 1;
            var kind = i < doc.Kinds.Length ? doc.Kinds[i] : DiffDocument.Context;
            var changed = kind is DiffDocument.Added or DiffDocument.Deleted;
            var selected = changed && _staging is not null && _selectedLines.Contains(line);
            if (changed)
            {
                ds.FillRectangle(0, top, full, h, selected ? palette.Selected : kind == DiffDocument.Added ? palette.Add : palette.Del);
                if (selected) ds.FillRectangle(0, top, 3, h, palette.Accent);
            }
            if (changed && _staging is not null) DrawCheck(ds, palette, checkWidth / 2, top + rowH / 2, selected);
            if (settings.LineNumbers)
            {
                if (doc.OldNumbers[i] > 0) ds.DrawText(Number(doc.OldNumbers[i]), new Rect(numbersLeft, top, numberWidth, rowH), palette.Text3, numberFormat);
                if (doc.NewNumbers[i] > 0) ds.DrawText(Number(doc.NewNumbers[i]), new Rect(numbersLeft + numberWidth + charWidth, top, numberWidth, rowH), palette.Text3, numberFormat);
            }
            if (changed)
            {
                ds.DrawText(kind == DiffDocument.Added ? "+" : "−", new Rect(signLeft, top, SignWidth, rowH),
                    kind == DiffDocument.Added ? palette.AddText : palette.DelText, signFormat);
            }
        }
    }

    private static int MaxOf(IReadOnlyList<int> numbers)
    {
        var max = 0;
        foreach (var n in numbers) max = Math.Max(max, n);
        return max;
    }

    private static string Number(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Selection box of a changed line (12 px, rounded): outlined, or filled with the accent and a check mark.</summary>
    private static void DrawCheck(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, Palette palette, float cx, float cy, bool on)
    {
        var box = new Rect(cx - 6, cy - 6, 12, 12);
        if (!on)
        {
            ds.DrawRoundedRectangle(box, 3, 3, WithAlpha(palette.StrokeStrong, 0x8C), 1);
            return;
        }
        ds.FillRoundedRectangle(box, 3, 3, palette.Accent);
        using var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        ds.DrawLine(cx - 3.5f, cy + 0.2f, cx - 1f, cy + 2.6f, Colors.White, 1.6f, style);
        ds.DrawLine(cx - 1f, cy + 2.6f, cx + 3.6f, cy - 2.4f, Colors.White, 1.6f, style);
    }

    /// <summary>A click on a changed line's box (or anywhere on its row of the gutter) selects it; Shift for a range.</summary>
    private void OnDiffGutterPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_doc is not { } doc || _staging is null || _unified is not { } editor) return;
        var y = e.GetCurrentPoint(_unifiedGutter).Position.Y + editor.ScrollTop;
        var line = LineAtOrAbove(editor, y);
        var top = editor.GetTopForLineNumber(line, false);
        if (y >= top + RowHeight(editor, line, top)) return;
        var kind = line - 1 < doc.Kinds.Length ? doc.Kinds[line - 1] : DiffDocument.Context;
        if (kind is not (DiffDocument.Added or DiffDocument.Deleted)) return;
        LineToggled?.Invoke(this, (line, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)));
        // The keys of the selection (S, Delete) go through the editor.
        editor.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    /// <summary>The wheel over a column drawn beside an editor scrolls the editor.</summary>
    private static void ScrollWithWheel(Loom editor, UIElement column, PointerRoutedEventArgs e)
    {
        editor.ScrollTop = Math.Max(0, editor.ScrollTop - e.GetCurrentPoint(column).Properties.MouseWheelDelta);
        e.Handled = true;
    }
}
