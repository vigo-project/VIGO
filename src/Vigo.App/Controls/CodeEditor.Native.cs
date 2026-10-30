using LoomEditor.TextMate.Grammars;
using LoomEditor.Themes;
using LoomEditor.View.Decorations;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Windows.Foundation;
using Windows.UI;
using Windows.System;
using Windows.UI.Core;
using Loom = LoomEditor.WinUI.Controls.LoomEditor;
using LoomDiff = LoomEditor.WinUI.Controls.LoomDiffEditor;
using LoomWordWrap = LoomEditor.WinUI.Controls.EditorWordWrap;
using LoomRange = LoomEditor.Core.Text.Range;
using LoomSelection = LoomEditor.Core.Text.Selection;

namespace Vigo.App.Controls;

/// <summary>
/// Native part of the code editor (LoomEditor, Win2D, no WebView2): the modes moved off the Monaco page so far (ADR-090).
/// The blame (J5.6): the file read-only, the commit column drawn on its left with, for each block of lines from the same
/// commit, an age bar, the author's avatar, the subject and the date on its first line. The side-by-side diff: a
/// read-only <see cref="LoomDiff"/>.
/// </summary>
public sealed partial class CodeEditor
{
    private const double BlameWidth = 303;
    private const string BlameSelectedClass = "vigo-blame-selected";

    private static readonly EditorDecorationOptions BlameSelectedLine = new() { IsWholeLine = true, ClassName = BlameSelectedClass };

    private Grid? _blameHost;
    private Rectangle? _blameDivider;
    private Loom? _native;
    private CanvasControl? _blameColumn;
    private DecorationsCollection? _blameDecorations;
    private string? _nativePath;
    private IReadOnlyList<EditorBlameCommit> _blameCommits = [];
    private IReadOnlyList<int> _blameLines = [];
    private int _blameSelected = -1;
    private int _blameCaretLine;
    private int _blameTipLine;
    private LoomDiff? _diff;
    private string? _diffPath;

    static CodeEditor()
    {
        CustomDecorationStyles.Register(BlameSelectedClass, new CustomDecorationStyle
        {
            Background = ThemeColorOf(Light.Selected),
            DarkBackground = ThemeColorOf(Dark.Selected),
        });
        RegisterDiffStyles();
        RegisterConflictStyles();
        RegisterReviewStyles();
    }

    private Loom EnsureNative()
    {
        if (_native is not null) return _native;
        var editor = _native = new Loom
        {
            IsReadOnly = true,
            // The blame column lines up with the lines of the file: no wrapping there.
            WordWrap = LoomWordWrap.Off,
            MinimapEnabled = false,
            // As the page: line numbers, no folding, a thin decorations margin.
            FoldingEnabled = false,
            LineDecorationsWidth = 8,
        };
        // The commit of the caret's line is the selected one: a click or the arrow keys select it.
        editor.SelectionChanged += (_, _) =>
        {
            var line = editor.Selections[0].PositionLineNumber;
            if (line == _blameCaretLine) return;
            _blameCaretLine = line;
            BlameLineSelected?.Invoke(this, line);
        };
        editor.ScrollChanged += (_, _) => _blameColumn?.Invalidate();
        editor.LayoutChanged += (_, _) => _blameColumn?.Invalidate();
        _blameDecorations = editor.CreateDecorationsCollection(Array.Empty<EditorDecoration>());

        var column = _blameColumn = new CanvasControl { Width = BlameWidth };
        column.Draw += OnDrawBlameColumn;
        column.PointerWheelChanged += OnBlameColumnWheel;
        column.PointerPressed += OnBlameColumnPressed;
        column.PointerMoved += OnBlameColumnMoved;
        ToolTipService.SetToolTip(column, new ToolTip());

        _blameDivider = new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Right };
        var host = _blameHost = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.Children.Add(column);
        host.Children.Add(_blameDivider);
        Grid.SetColumn(editor, 1);
        host.Children.Add(editor);
        ApplyNativeTheme();
        return editor;
    }

    private void ShowNativeBlame(string path, string text)
    {
        var editor = EnsureNative();
        _nativePath = path;
        _blameCommits = [];
        _blameLines = [];
        _blameSelected = -1;
        _blameCaretLine = 0;
        editor.LanguageId = LanguageOf(path);
        editor.ResetText(text);
        editor.ScrollTop = 0;
        _blameDecorations!.Clear();
        ShowNative(_blameHost!);
        _blameColumn!.Invalidate();
    }

    private void ShowNative(UIElement element)
    {
        if (!ReferenceEquals(Content, element)) Content = element;
    }

    /// <summary>The native editor on screen, for the focus; null when the page (or nothing) is shown.</summary>
    private Control? NativeFocusTarget =>
        ReferenceEquals(Content, _blameHost) ? _native
        : ReferenceEquals(Content, _diff) ? (_diff!.IsLoaded ? _diff.Modified : _diff)
        : IsNativeDiffShown ? _unified
        : IsNativeConflictShown ? _cfResult
        : null;

    // ---------------------------------------------------------------- side by side

    private LoomDiff EnsureDiff()
    {
        if (_diff is not null) return _diff;
        var diff = _diff = new LoomDiff
        {
            IsReadOnly = true,
            OriginalEditable = false,
            RenderSideBySide = true,
            UseInlineViewWhenSpaceIsLimited = false,
            RenderOverviewRuler = true,
            IgnoreTrimWhitespace = false,
            RenderIndicators = true,
            HideUnchangedRegions = false,
        };
        // Alt+Up / Alt+Down: previous / next change (before the accelerators of the view, which move between hunks).
        diff.PreviewKeyDown += (_, e) =>
        {
            // Not while a box of the review is typed in (its caret moves with Alt+arrows too).
            if (e.OriginalSource is TextBox) return;
            if (e.Key is not (VirtualKey.Up or VirtualKey.Down)
                || !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)) return;
            if (e.Key == VirtualKey.Up) diff.GoToPreviousChange();
            else diff.GoToNextChange();
            e.Handled = true;
        };
        diff.AddCommentRequested += (_, e) => OnDiffAddCommentRequested(e.Side, e.StartLineNumber, e.LineNumber, e.TextRange);
        // The two inner editors exist once the template is applied.
        diff.Loaded += (_, _) =>
        {
            ApplyNativeTheme();
            LayoutSideReview();
        };
        ApplyNativeTheme();
        return diff;
    }

    private void ShowNativeSideBySide(DiffSides sides)
    {
        var diff = EnsureDiff();
        _diffPath = sides.Path;
        diff.LanguageId = LanguageOf(sides.Path);
        diff.OriginalText = sides.Original;
        diff.ModifiedText = sides.Modified;
        ShowNative(diff);
        // The zones of the threads are laid out on the new texts.
        QueueReviewLayout();
        // Not loaded yet (first diff, or after another kind of diff): the control waits for its next load.
        RevealFirstChange(diff);
    }

    /// <summary>The diff is computed asynchronously: opens the file on its first change once it is known.</summary>
    private static async void RevealFirstChange(LoomDiff diff) =>
        // shortcut: not canceled when another diff is shown before this one is computed, add a token if that reveals the wrong file.
        await diff.RevealFirstChangeAsync(CancellationToken.None);

    private void SetNativeBlameState(IReadOnlyList<EditorBlameCommit> commits, IReadOnlyList<int> lines, int selected)
    {
        if (_native is null) return;
        _blameCommits = commits;
        _blameLines = lines;
        SetNativeBlameSelection(selected);
    }

    private void SetNativeBlameSelection(int selected)
    {
        if (_native is null) return;
        _blameSelected = selected;
        var items = new List<EditorDecoration>();
        if (selected >= 0)
        {
            for (var i = 0; i < _blameLines.Count; i++)
                if (_blameLines[i] == selected) items.Add(new EditorDecoration(new LoomRange(i + 1, 1, i + 1, 1), BlameSelectedLine));
        }
        _blameDecorations!.Set(items);
        _blameColumn!.Invalidate();
    }

    private void ClearNative()
    {
        ClearNativeReview();
        ClearNativeDiff();
        ClearNativeConflict();
        if (_diff is not null)
        {
            _diffPath = null;
            _diff.OriginalText = string.Empty;
            _diff.ModifiedText = string.Empty;
        }
        if (_native is null) return;
        _nativePath = null;
        _blameCommits = [];
        _blameLines = [];
        _blameSelected = -1;
        _blameDecorations!.Clear();
        _native.ResetText(string.Empty);
        _blameColumn!.Invalidate();
    }

    private void ReleaseNative()
    {
        ClearNativeReview();
        ReleaseNativeDiff();
        ReleaseNativeConflict();
        // Win2D: the device resources are freed when the tab is closed, not on Unloaded.
        _blameColumn?.RemoveFromVisualTree();
        _blameColumn = null;
    }

    /// <summary>The language of a file for the syntax coloring; plain text when it is turned off or unknown.</summary>
    private static string LanguageOf(string path) =>
        Appearance.Current.Editor.SyntaxHighlighting && BuiltInLanguages.GetLanguageIdByFileName(System.IO.Path.GetFileName(path)) is { } id
            ? id
            : "plaintext";

    /// <summary>Theme, fonts and options of the preferences (ADR-032: changed live).</summary>
    private void ApplyNativeTheme()
    {
        var dark = ActualTheme != ElementTheme.Light;
        var theme = dark ? DarkTheme.Value : LightTheme.Value;
        ApplyNativeDiffTheme(theme, dark);
        ApplyNativeConflictTheme(theme, dark);
        if (_native is { } editor)
        {
            ApplyEditorSettings(editor, theme, wrap: false);
            if (_nativePath is not null) editor.LanguageId = LanguageOf(_nativePath);
            _blameDivider!.Fill = new SolidColorBrush((dark ? Dark : Light).Stroke);
            _blameColumn?.Invalidate();
        }
        if (_diff is { } diff)
        {
            var wrap = Appearance.Current.Editor.WordWrap;
            diff.Theme = theme;
            diff.LineHeight = LineHeightOf(Appearance.Current.Editor);
            // Original / Modified throw until the template is applied: set again on Loaded.
            if (diff.IsLoaded)
            {
                ApplyEditorSettings(diff.Original, theme, wrap);
                ApplyEditorSettings(diff.Modified, theme, wrap);
            }
            if (_diffPath is not null) diff.LanguageId = LanguageOf(_diffPath);
        }
    }

    private static void ApplyEditorSettings(Loom editor, EditorTheme theme, bool wrap)
    {
        var settings = Appearance.Current.Editor;
        editor.Theme = theme;
        editor.FontSize = settings.FontSize;
        if (settings.FontFamily is { } family) editor.FontFamily = new FontFamily(family);
        editor.TabSize = settings.TabSize;
        editor.LineNumbersEnabled = settings.LineNumbers;
        editor.FontLigatures = settings.Ligatures;
        editor.WordWrap = wrap ? LoomWordWrap.On : LoomWordWrap.Off;
        editor.LineHeight = LineHeightOf(settings);
        // As the page: no lines stuck at the top (they would cover the diff and conflict headers).
        editor.StickyScrollEnabled = false;
    }

    /// <summary>The density's line height is for the default size; it grows with the chosen one (as the page did).</summary>
    private static double LineHeightOf(EditorSettings settings) =>
        Math.Round(Appearance.Current.Metrics.CodeLineHeight * settings.FontSize / UserPreferences.DefaultEditorFontSize);

    // ---------------------------------------------------------------- blame column

    private int BlameLineAt(double y)
    {
        var editor = _native!;
        return editor.LineHeight <= 0 ? 0 : (int)((y + editor.ScrollTop) / editor.LineHeight) + 1;
    }

    private void OnDrawBlameColumn(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_native is not { } editor) return;
        var palette = ActualTheme == ElementTheme.Light ? Light : Dark;
        var ds = args.DrawingSession;
        ds.Clear(palette.Background);
        var count = editor.Model.LineCount;
        var height = editor.LineHeight;
        if (_blameLines.Count == 0 || height <= 0) return;

        var width = (float)sender.ActualWidth;
        var family = Appearance.Current.UiFontFamily;
        using var summaryFormat = new CanvasTextFormat
        {
            FontFamily = family, FontSize = (float)Appearance.Scaled(12), WordWrapping = CanvasWordWrapping.NoWrap,
            VerticalAlignment = CanvasVerticalAlignment.Center, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
        };
        using var dateFormat = new CanvasTextFormat
        {
            FontFamily = family, FontSize = (float)Appearance.Scaled(11), WordWrapping = CanvasWordWrapping.NoWrap,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        var scrollTop = editor.ScrollTop;
        var h = (float)height;
        // 18 px as in the page, smaller when the rows are (compact density, small font): 1 px above and below.
        var avatar = Math.Min(18f, h - 2);
        using var avatarFormat = new CanvasTextFormat
        {
            FontFamily = family, FontSize = 8 * avatar / 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        var first = Math.Max(1, (int)(scrollTop / height) + 1);
        var last = Math.Min(count, (int)((scrollTop + sender.ActualHeight) / height) + 1);

        // Backgrounds, block separators and age bars first: a separator drawn later would cut the avatar above it.
        for (var line = first; line <= last; line++)
        {
            var c = CommitOfLine(line);
            var top = (float)(editor.GetTopForLineNumber(line, false) - scrollTop);
            if (c >= 0 && c == _blameSelected) ds.FillRectangle(0, top, width, h, palette.Selected);
            if (line > 1 && c != CommitOfLine(line - 1)) ds.DrawLine(0, top + 0.5f, width, top + 0.5f, palette.Stroke);
            if (c >= 0 && c < _blameCommits.Count)
                ds.FillRectangle(0, top, 3, h, WithAlpha(palette.Accent, (byte)(255 * (0.15 + 0.85 * _blameCommits[c].Age))));
        }

        // Then avatar, subject and date on the first line of each block.
        for (var line = first; line <= last; line++)
        {
            var c = CommitOfLine(line);
            if (c < 0 || c >= _blameCommits.Count || (line > 1 && c == CommitOfLine(line - 1))) continue;
            var commit = _blameCommits[c];
            var top = (float)(editor.GetTopForLineNumber(line, false) - scrollTop);
            var x = 13f;
            var cy = top + h / 2;
            ds.FillCircle(x + avatar / 2, cy, avatar / 2, Appearance.Current.Lanes[(int)((uint)commit.ColorIndex % Appearance.Current.Lanes.Length)]);
            ds.DrawText(commit.Initials, new Rect(x, cy - avatar / 2, avatar, avatar), Colors.White, avatarFormat);
            x += avatar + 8;
            var right = width - 10;
            using var date = new CanvasTextLayout(ds, commit.Date, dateFormat, 0, h);
            var dateWidth = (float)date.LayoutBounds.Width;
            ds.DrawTextLayout(date, right - dateWidth, top, palette.Text3);
            ds.DrawText(commit.Summary, new Rect(x, top, Math.Max(0, right - dateWidth - 8 - x), h), palette.Text2, summaryFormat);
        }
    }

    /// <summary>Index of the commit of a line (1-based) in the blame state; -1 when not known yet.</summary>
    private int CommitOfLine(int line) => line >= 1 && line - 1 < _blameLines.Count ? _blameLines[line - 1] : -1;

    /// <summary>The wheel over the column scrolls the editor.</summary>
    private void OnBlameColumnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_native is not { } editor) return;
        editor.ScrollTop = Math.Max(0, editor.ScrollTop - e.GetCurrentPoint(_blameColumn).Properties.MouseWheelDelta);
        e.Handled = true;
    }

    /// <summary>A click in the column puts the caret on the line, which selects its commit.</summary>
    private void OnBlameColumnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_native is not { } editor) return;
        var line = BlameLineAt(e.GetCurrentPoint(_blameColumn).Position.Y);
        if (line < 1 || line > editor.Model.LineCount) return;
        editor.SetSelections(new[] { new LoomSelection(line, 1, line, 1) });
        editor.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    /// <summary>The tooltip of the commit under the pointer.</summary>
    private void OnBlameColumnMoved(object sender, PointerRoutedEventArgs e)
    {
        var line = BlameLineAt(e.GetCurrentPoint(_blameColumn).Position.Y);
        if (line == _blameTipLine) return;
        _blameTipLine = line;
        var c = CommitOfLine(line);
        var tip = (ToolTip)ToolTipService.GetToolTip(_blameColumn);
        tip.Content = c >= 0 && c < _blameCommits.Count ? _blameCommits[c].ToolTip : null;
        tip.IsEnabled = tip.Content is not null;
    }

    // ---------------------------------------------------------------- theme

    private static readonly Lazy<EditorTheme> DarkTheme = new(() => BuildTheme(Dark, dark: true));
    private static readonly Lazy<EditorTheme> LightTheme = new(() => BuildTheme(Light, dark: false));

    private static ThemeColor ThemeColorOf(Color c) => new(c.A, c.R, c.G, c.B);

    /// <summary>The design system's colors as a VS Code color theme (the same keys as the Monaco theme of the page).</summary>
    private static EditorTheme BuildTheme(Palette p, bool dark)
    {
        static string Rule(string scopes, Color color) => $$"""{ "scope": [{{scopes}}], "settings": { "foreground": "{{Hex(color)}}" } }""";
        var json = $$"""
            {
              "colors": {
                "editor.background": "{{Hex(p.Background)}}",
                "editor.foreground": "{{Hex(p.Text)}}",
                "editorGutter.background": "{{Hex(p.Background)}}",
                "editorLineNumber.foreground": "{{Hex(p.Text3)}}",
                "editorLineNumber.activeForeground": "{{Hex(p.Text2)}}",
                "editor.lineHighlightBackground": "#00000000",
                "editor.lineHighlightBorder": "#00000000",
                "editor.selectionBackground": "{{Hex(WithAlpha(p.Accent, dark ? (byte)0x88 : (byte)0x66))}}",
                "editor.inactiveSelectionBackground": "{{Hex(WithAlpha(p.Accent, dark ? (byte)0x66 : (byte)0x4D))}}",
                "editorWidget.background": "{{Hex(p.Layer)}}",
                "editorWidget.border": "{{Hex(p.Stroke)}}",
                "editorStickyScroll.background": "{{Hex(p.Header)}}",
                "focusBorder": "{{Hex(p.Accent)}}",
                "diffEditor.insertedLineBackground": "{{Hex(p.Add)}}",
                "diffEditor.removedLineBackground": "{{Hex(p.Del)}}",
                "diffEditor.insertedTextBackground": "{{Hex(p.AddStrong)}}",
                "diffEditor.removedTextBackground": "{{Hex(p.DelStrong)}}",
                "scrollbarSlider.background": "{{Hex(p.Scrollbar)}}",
                "scrollbarSlider.hoverBackground": "{{Hex(p.ScrollbarHover)}}",
                "scrollbarSlider.activeBackground": "{{Hex(p.ScrollbarHover)}}"
              },
              "tokenColors": [
                {{Rule("\"comment\", \"punctuation.definition.comment\"", p.Comment)}},
                {{Rule("\"keyword\", \"storage\", \"storage.type\", \"storage.modifier\", \"constant.language\", \"variable.language\"", p.Keyword)}},
                {{Rule("\"keyword.operator\", \"punctuation\"", p.Text)}},
                {{Rule("\"keyword.control\"", p.Control)}},
                {{Rule("\"string\", \"punctuation.definition.string\"", p.String)}},
                {{Rule("\"constant.numeric\"", p.Number)}},
                {{Rule("\"entity.name.type\", \"entity.name.class\", \"entity.name.namespace\", \"entity.other.inherited-class\", \"support.type\", \"support.class\"", p.Type)}},
                {{Rule("\"entity.name.function\", \"support.function\"", p.Function)}},
                {{Rule("\"variable\", \"variable.parameter\", \"variable.other.readwrite\"", p.Variable)}},
                {{Rule("\"variable.other.property\", \"variable.other.object.property\", \"support.variable.property\"", p.Property)}}
              ]
            }
            """;
        return EditorTheme.Load(json, dark ? "vs-dark" : "vs", dark ? "VIGO Dark" : "VIGO Light", _ => string.Empty);
    }
}
