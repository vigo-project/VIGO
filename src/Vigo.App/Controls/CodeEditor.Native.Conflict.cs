using LoomEditor.Core.Model;
using LoomEditor.Core.Text;
using LoomEditor.View.Decorations;
using LoomEditor.WinUI.Controls;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.System;
using Windows.UI;
using Loom = LoomEditor.WinUI.Controls.LoomEditor;
using LoomRange = LoomEditor.Core.Text.Range;

namespace Vigo.App.Controls;

/// <summary>
/// Native conflict editor (ADR-090, step 4; J5.4, mockup "Conflits"): panes ours and theirs (common lines and the lines of
/// that side in each conflict, a check box per conflict line in a Win2D gutter, "Prendre ce bloc" in each conflict
/// header), editable result below. The app keeps the choices: it sends the state of every conflict and the lines of a
/// conflict whose choice changed, which replace that conflict's lines in the result; each conflict is tracked there by a
/// decoration, so manual edits elsewhere are kept.
/// </summary>
public sealed partial class CodeEditor
{
    private const double BlockHeight = 26;
    private const double CardHeight = 40;

    // Mockup: ours in lane color 5 (violet), theirs in lane color 1 (blue), 14 % in the panes and 8 % in the result.
    private static Color OursColor => DesignTokens.Lanes[4];
    private static Color TheirsColor => DesignTokens.Lanes[0];

    private static readonly EditorDecorationOptions CfOurs = CfLine("vigo-cf-ours");
    private static readonly EditorDecorationOptions CfOursCurrent = CfLine("vigo-cf-ours-current");
    private static readonly EditorDecorationOptions CfTheirs = CfLine("vigo-cf-theirs");
    private static readonly EditorDecorationOptions CfTheirsCurrent = CfLine("vigo-cf-theirs-current");
    private static readonly EditorDecorationOptions CfDropped = new() { InlineClassName = "vigo-cf-dropped" };
    private static readonly EditorDecorationOptions CfResultOurs = new() { IsWholeLine = true, ClassName = "vigo-cf-res-ours", LinesDecorationsClassName = "vigo-cf-bar-ours" };
    private static readonly EditorDecorationOptions CfResultTheirs = new() { IsWholeLine = true, ClassName = "vigo-cf-res-theirs", LinesDecorationsClassName = "vigo-cf-bar-theirs" };
    // Tracks the lines of a conflict in the result while the user edits elsewhere; draws nothing.
    private static readonly EditorDecorationOptions CfRange = new();

    /// <summary>One pane, ours or theirs: its editor, gutter, and where its conflict lines are.</summary>
    private sealed class ConflictPane(ConflictSide side, Loom editor, CanvasControl gutter, ConflictPaneHeader header)
    {
        public ConflictSide Side { get; } = side;
        public Loom Editor { get; } = editor;
        public CanvasControl Gutter { get; } = gutter;
        public ConflictPaneHeader Header { get; } = header;
        public DecorationsCollection Decorations { get; } = editor.CreateDecorationsCollection(Array.Empty<EditorDecoration>());
        public List<string> Zones { get; } = [];

        /// <summary>Per pane line: the conflict and the index of the line in it; block -1 for a common line.</summary>
        public (int Block, int Index)[] Map { get; set; } = [];

        /// <summary>Per conflict: the first line (1-based) of its lines in the pane.</summary>
        public int[] Starts { get; set; } = [];

        public HashSet<int> StartSet { get; set; } = [];
    }

    private Grid? _cfHost;
    private ConflictPane? _cfOurs, _cfTheirs;
    private Loom? _cfResult;
    private ConflictPaneHeader? _cfResultHeader;
    private Border? _cfTopBorder, _cfSplit;
    private readonly List<string> _cfResultZones = [];
    private readonly List<DecorationsCollection> _cfRanges = [];
    private readonly List<DecorationsCollection> _cfLines = [];
    private EditorConflictLabels? _cfLabels;
    private IReadOnlyList<EditorConflictBlock> _cfBlocks = [];
    private int _cfCurrent = -1;
    private string? _cfPath;
    private bool _cfApplying;
    private bool _cfRevealPending;

    private static EditorDecorationOptions CfLine(string className) => new() { IsWholeLine = true, ClassName = className, MarginClassName = className };

    private static void RegisterConflictStyles()
    {
        var oursSoft = ThemeColorOf(WithAlpha(OursColor, 0x24));
        var theirsSoft = ThemeColorOf(WithAlpha(TheirsColor, 0x24));
        CustomDecorationStyles.Register("vigo-cf-ours", new CustomDecorationStyle { Background = oursSoft, DarkBackground = oursSoft });
        CustomDecorationStyles.Register("vigo-cf-theirs", new CustomDecorationStyle { Background = theirsSoft, DarkBackground = theirsSoft });
        // The current conflict: an accent bar on the left of its lines.
        foreach (var (name, background) in new[] { ("vigo-cf-ours-current", oursSoft), ("vigo-cf-theirs-current", theirsSoft) })
        {
            CustomDecorationStyles.Register(name, new CustomDecorationStyle
            {
                Background = background, DarkBackground = background,
                Border = ThemeColorOf(Light.Accent), DarkBorder = ThemeColorOf(Dark.Accent), BorderWidth = 0, LeftBorderWidth = 2,
            });
        }
        // Monaco: struck through at 60 % opacity; LoomEditor's custom styles have neither: the text is dimmed only.
        CustomDecorationStyles.Register("vigo-cf-dropped", new CustomDecorationStyle
        {
            Foreground = ThemeColorOf(Light.Text3), DarkForeground = ThemeColorOf(Dark.Text3),
        });
        var oursFaint = ThemeColorOf(WithAlpha(OursColor, 0x14));
        var theirsFaint = ThemeColorOf(WithAlpha(TheirsColor, 0x14));
        CustomDecorationStyles.Register("vigo-cf-res-ours", new CustomDecorationStyle { Background = oursFaint, DarkBackground = oursFaint });
        CustomDecorationStyles.Register("vigo-cf-res-theirs", new CustomDecorationStyle { Background = theirsFaint, DarkBackground = theirsFaint });
        CustomDecorationStyles.Register("vigo-cf-bar-ours", new CustomDecorationStyle { Background = ThemeColorOf(OursColor), DarkBackground = ThemeColorOf(OursColor) });
        CustomDecorationStyles.Register("vigo-cf-bar-theirs", new CustomDecorationStyle { Background = ThemeColorOf(TheirsColor), DarkBackground = ThemeColorOf(TheirsColor) });
    }

    private void EnsureConflict()
    {
        if (_cfHost is not null) return;
        _cfOurs = MakeConflictPane(ConflictSide.Ours);
        _cfTheirs = MakeConflictPane(ConflictSide.Theirs);

        var result = _cfResult = new Loom { MinimapEnabled = false, FoldingEnabled = false, LineDecorationsWidth = 8 };
        result.Model.ContentChanged += (_, _) =>
        {
            if (!_cfApplying && IsNativeConflictShown) TextChanged?.Invoke(this, EventArgs.Empty);
        };
        // Ctrl+Z / Ctrl+Y undo the edits of the result, not the app's last operation.
        result.ProcessKeyboardAccelerators += (_, e) =>
        {
            if (e.Modifiers == VirtualKeyModifiers.Control && e.Key is VirtualKey.Z or VirtualKey.Y) e.Handled = true;
        };
        result.Loaded += (_, _) =>
        {
            ApplyNativeTheme();
            if (!_cfRevealPending) return;
            _cfRevealPending = false;
            RevealConflict();
        };
        _cfResultHeader = new ConflictPaneHeader();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.Children.Add(PaneElement(_cfOurs.Header, _cfOurs.Gutter, _cfOurs.Editor));
        _cfSplit = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = PaneElement(_cfTheirs.Header, _cfTheirs.Gutter, _cfTheirs.Editor) };
        Grid.SetColumn(_cfSplit, 1);
        top.Children.Add(_cfSplit);
        _cfTopBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = top };

        var host = _cfHost = new Grid();
        host.RowDefinitions.Add(new RowDefinition());
        host.RowDefinitions.Add(new RowDefinition());
        host.Children.Add(_cfTopBorder);
        var bottom = PaneElement(_cfResultHeader, null, result);
        Grid.SetRow(bottom, 1);
        host.Children.Add(bottom);
        ApplyNativeTheme();
    }

    private ConflictPane MakeConflictPane(ConflictSide side)
    {
        var editor = new Loom { IsReadOnly = true, MinimapEnabled = false, FoldingEnabled = false, LineDecorationsWidth = 4 };
        var gutter = new CanvasControl { Width = CheckWidth };
        var header = new ConflictPaneHeader();
        var pane = new ConflictPane(side, editor, gutter, header);
        gutter.Draw += (s, e) => DrawConflictGutter(pane, s, e);
        gutter.PointerPressed += (_, e) =>
        {
            var y = e.GetCurrentPoint(gutter).Position.Y + editor.ScrollTop;
            var line = LineAtOrAbove(editor, y);
            var top = editor.GetTopForLineNumber(line, false);
            if (y < top + RowHeight(editor, line, top, pane.StartSet, BlockHeight)) ToggleConflictLine(pane, line);
            e.Handled = true;
        };
        gutter.PointerWheelChanged += (_, e) => ScrollWithWheel(editor, gutter, e);
        // A click on the line number also includes or excludes the line (as in the page).
        editor.GutterClicked += (_, e) =>
        {
            if (e.Area != GutterArea.LineNumbers) return;
            ToggleConflictLine(pane, e.LineNumber);
            e.Handled = true;
        };
        editor.ScrollChanged += (_, _) => gutter.Invalidate();
        editor.LayoutChanged += (_, _) => gutter.Invalidate();
        editor.Loaded += (_, _) => ApplyNativeTheme();
        header.TakeAll += (_, _) => ConflictAllTaken?.Invoke(this, side);
        return pane;
    }

    /// <summary>Header on top, then the gutter (panes only) and the editor.</summary>
    private static Grid PaneElement(ConflictPaneHeader header, CanvasControl? gutter, Loom editor)
    {
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition());
        pane.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pane.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumnSpan(header, 2);
        pane.Children.Add(header);
        if (gutter is not null)
        {
            Grid.SetRow(gutter, 1);
            pane.Children.Add(gutter);
        }
        Grid.SetRow(editor, 1);
        Grid.SetColumn(editor, 1);
        pane.Children.Add(editor);
        return pane;
    }

    private bool IsNativeConflictShown => _cfHost is not null && ReferenceEquals(Content, _cfHost);

    private void ShowNativeConflict(string path, EditorConflictLabels labels, ConflictFile file, IReadOnlyList<EditorConflictBlock> blocks, int current)
    {
        EnsureConflict();
        _cfPath = path;
        _cfLabels = labels;
        _cfBlocks = blocks;
        _cfCurrent = current;

        _cfOurs!.Header.ShowSide(OursColor, labels.OursTitle, labels.OursDetail, labels.TakeAll);
        _cfTheirs!.Header.ShowSide(TheirsColor, labels.TheirsTitle, labels.TheirsDetail, labels.TakeAll);
        _cfResultHeader!.ShowResult(labels.ResultTitle, labels.ResultDetail, OursColor, labels.LegendOurs, TheirsColor, labels.LegendTheirs);
        ToolTipService.SetToolTip(_cfOurs.Gutter, labels.ToggleTip);
        ToolTipService.SetToolTip(_cfTheirs.Gutter, labels.ToggleTip);

        foreach (var pane in new[] { _cfOurs, _cfTheirs }) FillConflictPane(pane, file, path);

        // Result: common lines and the lines of the conflicts already chosen.
        var result = _cfResult!;
        RemoveZones(result, _cfResultZones);
        var text = new System.Text.StringBuilder();
        var starts = new List<int>();
        var line = 1;
        var block = 0;
        foreach (var segment in file.Segments)
        {
            switch (segment)
            {
                case CommonLines common:
                    foreach (var l in common.Lines) text.Append(l).Append('\n');
                    line += common.Lines.Count;
                    break;
                case ConflictHunk:
                    starts.Add(line);
                    var chosen = block < blocks.Count ? blocks[block].Result : [];
                    foreach (var l in chosen) text.Append(l.Text).Append('\n');
                    line += chosen.Count;
                    block++;
                    break;
            }
        }
        _cfApplying = true;
        try
        {
            result.LanguageId = LanguageOf(path);
            result.ResetText(text.ToString());
        }
        finally
        {
            _cfApplying = false;
        }
        for (var b = 0; b < Math.Max(starts.Count, _cfRanges.Count); b++)
        {
            if (b >= _cfRanges.Count)
            {
                _cfRanges.Add(result.CreateDecorationsCollection(Array.Empty<EditorDecoration>()));
                _cfLines.Add(result.CreateDecorationsCollection(Array.Empty<EditorDecoration>()));
            }
            if (b >= starts.Count)
            {
                _cfRanges[b].Clear();
                _cfLines[b].Clear();
                continue;
            }
            var lines = b < blocks.Count ? blocks[b].Result : [];
            TrackConflict(b, starts[b], lines);
        }

        foreach (var editor in new[] { _cfOurs.Editor, _cfTheirs.Editor, result }) editor.ScrollTop = 0;
        RenderConflict();
        ShowNative(_cfHost!);
        if (result.IsLoaded) RevealConflict();
        else _cfRevealPending = true;
    }

    /// <summary>Text of a pane: common lines and the lines of that side in each conflict.</summary>
    private static void FillConflictPane(ConflictPane pane, ConflictFile file, string path)
    {
        var lines = new List<string>();
        var map = new List<(int, int)>();
        var starts = new List<int>();
        var block = 0;
        foreach (var segment in file.Segments)
        {
            switch (segment)
            {
                case CommonLines common:
                    foreach (var l in common.Lines)
                    {
                        lines.Add(l);
                        map.Add((-1, 0));
                    }
                    break;
                case ConflictHunk hunk:
                    starts.Add(lines.Count + 1);
                    var own = pane.Side == ConflictSide.Ours ? hunk.Ours : hunk.Theirs;
                    for (var i = 0; i < own.Count; i++)
                    {
                        lines.Add(own[i]);
                        map.Add((block, i));
                    }
                    block++;
                    break;
            }
        }
        pane.Map = [.. map];
        pane.Starts = [.. starts];
        pane.StartSet = [.. starts];
        RemoveZones(pane.Editor, pane.Zones);
        pane.Editor.LanguageId = LanguageOf(path);
        pane.Editor.ResetText(string.Join('\n', lines));
    }

    private static void RemoveZones(Loom editor, List<string> zones)
    {
        foreach (var id in zones) editor.RemoveViewZone(id);
        zones.Clear();
    }

    /// <summary>The range of a conflict in the result (from its first line to the start of the next one) and the colors of its lines.</summary>
    private void TrackConflict(int block, int start, IReadOnlyList<(string Text, ConflictSide Side)> lines)
    {
        _cfRanges[block].Set(new[] { new EditorDecoration(new LoomRange(start, 1, start + lines.Count, 1), CfRange) });
        var items = new List<EditorDecoration>(lines.Count);
        for (var k = 0; k < lines.Count; k++)
        {
            items.Add(new EditorDecoration(new LoomRange(start + k, 1, start + k, 1),
                lines[k].Side == ConflictSide.Ours ? CfResultOurs : CfResultTheirs));
        }
        _cfLines[block].Set(items);
    }

    private void SetNativeConflictState(IReadOnlyList<EditorConflictBlock> blocks, int current, bool reveal, IReadOnlyList<int> changed)
    {
        if (!IsNativeConflictShown) return;
        _cfBlocks = blocks;
        _cfCurrent = current;
        foreach (var block in changed) SetConflictBlock(block, blocks[block].Result);
        RenderConflict();
        if (reveal) RevealConflict();
    }

    /// <summary>Replaces the lines of a conflict in the result (its choice changed); undoable with Ctrl+Z.</summary>
    private void SetConflictBlock(int block, IReadOnlyList<(string Text, ConflictSide Side)> lines)
    {
        if (block >= _cfRanges.Count || _cfRanges[block].GetRange(0) is not { } range) return;
        var model = _cfResult!.Model;
        var text = string.Concat(lines.Select(l => l.Text + "\n"));
        _cfApplying = true;
        try
        {
            model.PushStackElement();
            model.PushEditOperations(_cfResult.Selections, new[] { new TextEdit(range, text) }, null!);
            model.PushStackElement();
        }
        finally
        {
            _cfApplying = false;
        }
        TrackConflict(block, range.StartLineNumber, lines);
    }

    /// <summary>Decorations and view zones of the panes and of the result, from the state of the conflicts.</summary>
    private void RenderConflict()
    {
        if (!IsNativeConflictShown || _cfLabels is not { } labels) return;
        var palette = ActualTheme == ElementTheme.Light ? Light : Dark;
        var accent = new SolidColorBrush(palette.Accent);
        var stroke = new SolidColorBrush(palette.Stroke);
        foreach (var pane in new[] { _cfOurs!, _cfTheirs! })
        {
            var ours = pane.Side == ConflictSide.Ours;
            var items = new List<EditorDecoration>();
            for (var i = 0; i < pane.Map.Length; i++)
            {
                var (block, index) = pane.Map[i];
                if (block < 0 || block >= _cfBlocks.Count) continue;
                var state = _cfBlocks[block];
                var current = block == _cfCurrent;
                var line = i + 1;
                items.Add(new EditorDecoration(new LoomRange(line, 1, line, 1),
                    ours ? (current ? CfOursCurrent : CfOurs) : (current ? CfTheirsCurrent : CfTheirs)));
                if (state.IsResolved && !Kept(state, pane.Side, index))
                    items.Add(new EditorDecoration(new LoomRange(line, 1, line, pane.Editor.Model.GetLineLength(line) + 1), CfDropped));
            }
            pane.Decorations.Set(items);

            // A new element per zone (see the unified diff): never moved from a removed zone.
            RemoveZones(pane.Editor, pane.Zones);
            var soft = new SolidColorBrush(WithAlpha(ours ? OursColor : TheirsColor, 0x24));
            for (var block = 0; block < pane.Starts.Length && block < _cfBlocks.Count; block++)
            {
                var zone = new ConflictZone();
                var index = block;
                zone.ShowBlock(ours ? _cfBlocks[block].OursLabel : _cfBlocks[block].TheirsLabel, labels.Take,
                    ours ? ConflictTake.Ours : ConflictTake.Theirs, soft, block == _cfCurrent ? accent : stroke);
                zone.Taken += (_, take) => ConflictTaken?.Invoke(this, (index, take));
                pane.Zones.Add(pane.Editor.AddViewZone(pane.Starts[block] - 1, BlockHeight, zone));
            }
            pane.Gutter.Invalidate();
        }

        // Result: a card for each conflict not resolved yet.
        var result = _cfResult!;
        RemoveZones(result, _cfResultZones);
        for (var block = 0; block < _cfBlocks.Count && block < _cfRanges.Count; block++)
        {
            if (_cfBlocks[block].IsResolved || _cfRanges[block].GetRange(0) is not { } range) continue;
            var card = new ConflictZone();
            var index = block;
            card.ShowCard(_cfBlocks[block].UnresolvedLabel, labels.Ours, labels.Theirs, labels.Both, block == _cfCurrent ? accent : null);
            card.Taken += (_, take) => ConflictTaken?.Invoke(this, (index, take));
            _cfResultZones.Add(result.AddViewZone(range.StartLineNumber - 1, CardHeight, card));
        }
    }

    private static bool Kept(EditorConflictBlock state, ConflictSide side, int index)
    {
        var kept = side == ConflictSide.Ours ? state.KeptOurs : state.KeptTheirs;
        return index < kept.Count && kept[index];
    }

    /// <summary>Scrolls the panes and the result to the current conflict.</summary>
    private void RevealConflict()
    {
        if (_cfCurrent < 0 || !IsNativeConflictShown) return;
        foreach (var pane in new[] { _cfOurs!, _cfTheirs! })
        {
            if (_cfCurrent >= pane.Starts.Length) continue;
            var editor = pane.Editor;
            var line = Math.Min(pane.Starts[_cfCurrent], Math.Max(1, editor.Model.LineCount));
            editor.ScrollTop = Math.Max(0, editor.GetTopForLineNumber(line, false) - BlockHeight - 2 * editor.LineHeight);
        }
        if (_cfCurrent < _cfRanges.Count && _cfRanges[_cfCurrent].GetRange(0) is { } range)
        {
            var result = _cfResult!;
            result.ScrollTop = Math.Max(0, result.GetTopForLineNumber(range.StartLineNumber, false) - CardHeight - 2 * result.LineHeight);
        }
    }

    private void ToggleConflictLine(ConflictPane pane, int line)
    {
        if (line < 1 || line > pane.Map.Length || pane.Map[line - 1] is not { Block: >= 0 } item) return;
        ConflictLineToggled?.Invoke(this, (item.Block, pane.Side, item.Index));
    }

    /// <summary>Text of the result in the line ending of the preferences (auto: the one of the text).</summary>
    private string? GetNativeConflictResult()
    {
        if (!IsNativeConflictShown) return null;
        var eol = Appearance.Current.Editor.EndOfLine switch
        {
            EditorEndOfLine.Lf => EndOfLinePreference.LF,
            EditorEndOfLine.Crlf => EndOfLinePreference.CRLF,
            _ => EndOfLinePreference.TextDefined,
        };
        return _cfResult!.Model.GetValue(eol, false);
    }

    private void ClearNativeConflict()
    {
        if (_cfHost is null) return;
        _cfBlocks = [];
        _cfCurrent = -1;
        _cfPath = null;
        foreach (var pane in new[] { _cfOurs!, _cfTheirs! })
        {
            RemoveZones(pane.Editor, pane.Zones);
            pane.Decorations.Clear();
            pane.Map = [];
            pane.Starts = [];
            pane.StartSet = [];
            pane.Editor.ResetText(string.Empty);
            pane.Gutter.Invalidate();
        }
        RemoveZones(_cfResult!, _cfResultZones);
        foreach (var collection in _cfRanges.Concat(_cfLines)) collection.Clear();
        _cfApplying = true;
        try
        {
            _cfResult!.ResetText(string.Empty);
        }
        finally
        {
            _cfApplying = false;
        }
    }

    private void ReleaseNativeConflict()
    {
        // Win2D: the device resources are freed when the tab is closed, not on Unloaded.
        foreach (var pane in new[] { _cfOurs, _cfTheirs })
            pane?.Gutter.RemoveFromVisualTree();
    }

    private void ApplyNativeConflictTheme(LoomEditor.Themes.EditorTheme theme, bool dark)
    {
        if (_cfHost is null) return;
        var wrap = Appearance.Current.Editor.WordWrap;
        foreach (var editor in new[] { _cfOurs!.Editor, _cfTheirs!.Editor, _cfResult! })
        {
            ApplyEditorSettings(editor, theme, wrap);
            if (_cfPath is not null) editor.LanguageId = LanguageOf(_cfPath);
        }
        var stroke = new SolidColorBrush((dark ? Dark : Light).Stroke);
        _cfTopBorder!.BorderBrush = stroke;
        _cfSplit!.BorderBrush = stroke;
        // The current conflict is outlined with the accent of the theme.
        RenderConflict();
    }

    // ---------------------------------------------------------------- gutter of a pane

    private void DrawConflictGutter(ConflictPane pane, CanvasControl sender, CanvasDrawEventArgs args)
    {
        var palette = ActualTheme == ElementTheme.Light ? Light : Dark;
        var ds = args.DrawingSession;
        ds.Clear(palette.Background);
        var editor = pane.Editor;
        if (pane.Map.Length == 0 || editor.LineHeight <= 0) return;
        var soft = WithAlpha(pane.Side == ConflictSide.Ours ? OursColor : TheirsColor, 0x24);
        var width = (float)sender.ActualWidth;
        var scrollTop = editor.ScrollTop;
        var height = sender.ActualHeight;
        var rowH = (float)editor.LineHeight;
        for (var line = LineAtOrAbove(editor, scrollTop); line <= pane.Map.Length; line++)
        {
            var lineTop = editor.GetTopForLineNumber(line, false);
            var top = (float)(lineTop - scrollTop);
            if (top > height) break;
            var (block, index) = pane.Map[line - 1];
            if (block < 0 || block >= _cfBlocks.Count) continue;
            var h = (float)RowHeight(editor, line, lineTop, pane.StartSet, BlockHeight);
            ds.FillRectangle(0, top, width, h, soft);
            if (block == _cfCurrent) ds.FillRectangle(0, top, 2, h, palette.Accent);
            DrawCheck(ds, palette, width / 2, top + rowH / 2, Kept(_cfBlocks[block], pane.Side, index));
        }
    }
}
