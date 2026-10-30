using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.UI;

namespace Vigo.App.Controls;

/// <summary>
/// Code editor, native since ADR-090 (LoomEditor, Win2D; Monaco in a WebView2 before, ADR-038): unified diff with line
/// staging, side-by-side diff, conflicts, blame, and the inline review of a pull request. Each mode lives in a
/// <c>CodeEditor.Native*.cs</c> part; this file holds the public API and the colors shared by the modes.
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private bool _closed;

    public CodeEditor()
    {
        IsTabStop = false;
        // ActualThemeChanged is not raised while the control is out of the tree (tab not shown, view cached): a
        // theme changed meanwhile is applied when it comes back.
        Loaded += (_, _) => ApplyNativeTheme();
        ActualThemeChanged += (_, _) => ApplyNativeTheme();
        Appearance.Current.PropertyChanged += OnAppearanceChanged;
    }

    /// <summary>A changed line's selection box was clicked (document line, 1-based; Shift for a range).</summary>
    public event EventHandler<(int Line, bool Extend)>? LineToggled;

    /// <summary>A hunk header button was clicked: stage / unstage, or discard.</summary>
    public event EventHandler<(int Hunk, bool Discard)>? HunkInvoked;

    /// <summary>A staging key pressed in a unified diff that has staging: <c>S</c> (stage / unstage) or <c>Delete</c> (discard).</summary>
    public event EventHandler<string>? KeyPressed;

    /// <summary>The text of the conflict result changed by hand.</summary>
    public event EventHandler? TextChanged;

    /// <summary>Shows a unified diff; <paramref name="staging"/> null for a read-only one.</summary>
    public void ShowDiff(string path, DiffDocument document, IReadOnlyList<int> selected, EditorStaging? staging, bool keepScroll)
    {
        if (!_closed) ShowNativeDiff(path, document, selected, staging, keepScroll);
    }

    public void SetSelection(IReadOnlyList<int> lines)
    {
        if (IsNativeDiffShown) SetNativeDiffSelection(lines);
    }

    public void RevealHunk(int hunk)
    {
        if (IsNativeDiffShown) RevealNativeHunk(hunk);
    }

    /// <summary>Shows a side-by-side diff.</summary>
    public void ShowSideBySide(DiffSides sides)
    {
        if (!_closed) ShowNativeSideBySide(sides);
    }

    /// <summary>Shows the conflict editor: panes ours / theirs built from the segments, result with the chosen lines.</summary>
    public void ShowConflict(string path, EditorConflictLabels labels, ConflictFile file, IReadOnlyList<EditorConflictBlock> blocks, int current)
    {
        if (!_closed) ShowNativeConflict(path, labels, file, blocks, current);
    }

    /// <summary>
    /// New state of the conflicts (labels, kept lines, current one); <paramref name="changed"/>: conflicts whose lines
    /// are replaced in the result.
    /// </summary>
    public void SetConflictState(IReadOnlyList<EditorConflictBlock> blocks, int current, bool reveal, IReadOnlyList<int> changed) =>
        SetNativeConflictState(blocks, current, reveal, changed);

    /// <summary>A check box of a conflict line was clicked in a pane.</summary>
    public event EventHandler<(int Block, ConflictSide Side, int Line)>? ConflictLineToggled;

    /// <summary>"Prendre ce bloc" of a pane, or Ours / Theirs / Les deux of an unresolved conflict of the result.</summary>
    public event EventHandler<(int Block, ConflictTake Take)>? ConflictTaken;

    /// <summary>"Tout prendre" of a pane.</summary>
    public event EventHandler<ConflictSide>? ConflictAllTaken;

    /// <summary>Text of the result pane of the conflict editor; null when no conflict is shown or the editor is closed.</summary>
    public Task<string?> GetConflictResultAsync() =>
        Task.FromResult(_closed ? null : GetNativeConflictResult());

    /// <summary>Shows a file read-only with the blame column on its left (empty until <see cref="SetBlameState"/>).</summary>
    public void ShowBlame(string path, string text)
    {
        if (!_closed) ShowNativeBlame(path, text);
    }

    /// <summary>Commits of the blame column and the commit of each line (index in <paramref name="commits"/>, -1: unknown yet).</summary>
    public void SetBlameState(IReadOnlyList<EditorBlameCommit> commits, IReadOnlyList<int> lines, int selected) =>
        SetNativeBlameState(commits, lines, selected);

    /// <summary>Commit whose lines are highlighted (index of <see cref="SetBlameState"/>; -1: none).</summary>
    public void SetBlameSelection(int selected) => SetNativeBlameSelection(selected);

    /// <summary>The caret of the blame moved to another line (1-based): click, arrow keys, click in the column.</summary>
    public event EventHandler<int>? BlameLineSelected;

    public void Clear() => ClearNative();

    /// <summary>Gives the keyboard focus to the editor.</summary>
    public void FocusEditor() => NativeFocusTarget?.Focus(FocusState.Programmatic);

    /// <summary>Releases the editors when the tab is closed for good.</summary>
    public void Release()
    {
        if (_closed) return;
        _closed = true;
        Appearance.Current.PropertyChanged -= OnAppearanceChanged;
        ReleaseNative();
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Appearance.Metrics) or nameof(Appearance.Editor)) ApplyNativeTheme();
    }

    private static readonly Palette Dark = new(
        DesignTokens.Dark.Sunken, DesignTokens.Dark.Layer, DesignTokens.Dark.Layer2, DesignTokens.Dark.Text, DesignTokens.Dark.Text2,
        DesignTokens.Dark.Text3, DesignTokens.Dark.StrokeSubtle, DesignTokens.Dark.StrokeStrong, DesignTokens.Dark.Stroke,
        DesignTokens.Dark.Ctrl, DesignTokens.Dark.Accent, DesignTokens.Dark.AccentSoft2, DesignTokens.Dark.Add, DesignTokens.Dark.Add2,
        DesignTokens.Dark.Del, DesignTokens.Dark.Del2, DesignTokens.Dark.AddText, DesignTokens.Dark.DelText, DesignTokens.Dark.Danger,
        DesignTokens.Dark.SubHover, DesignTokens.Dark.SubPress,
        DesignTokens.Dark.SynCom, DesignTokens.Dark.SynKw, DesignTokens.Dark.SynStr, DesignTokens.Dark.SynNum, DesignTokens.Dark.SynType,
        DesignTokens.Dark.SynFn, DesignTokens.Dark.SynVar, DesignTokens.Dark.SynProp, DesignTokens.Dark.SynCtrl);

    // The light design tokens tint added / removed lines at 8-9 %, almost invisible on the near-white editor background:
    // the editor uses stronger tints (line ~16 %, changed text ~25 %) so the diff stays readable in the light theme.
    private static readonly Palette Light = new(
        DesignTokens.Light.Sunken, DesignTokens.Light.Layer, DesignTokens.Light.Layer2, DesignTokens.Light.Text, DesignTokens.Light.Text2,
        DesignTokens.Light.Text3, DesignTokens.Light.StrokeSubtle, DesignTokens.Light.StrokeStrong, DesignTokens.Light.Stroke,
        DesignTokens.Light.Ctrl, DesignTokens.Light.Accent, DesignTokens.Light.AccentSoft2,
        WithAlpha(DesignTokens.Light.Add, 0x2A), WithAlpha(DesignTokens.Light.Add2, 0x40),
        WithAlpha(DesignTokens.Light.Del, 0x2A), WithAlpha(DesignTokens.Light.Del2, 0x40),
        DesignTokens.Light.AddText, DesignTokens.Light.DelText, DesignTokens.Light.Danger,
        DesignTokens.Light.SubHover, DesignTokens.Light.SubPress,
        DesignTokens.Light.SynCom, DesignTokens.Light.SynKw, DesignTokens.Light.SynStr, DesignTokens.Light.SynNum, DesignTokens.Light.SynType,
        DesignTokens.Light.SynFn, DesignTokens.Light.SynVar, DesignTokens.Light.SynProp, DesignTokens.Light.SynCtrl);

    private sealed record Palette(
        Color Background, Color Layer, Color Header, Color Text, Color Text2, Color Text3, Color Stroke, Color StrokeStrong,
        Color StrokeControl, Color Ctrl, Color Accent, Color Selected, Color Add, Color AddStrong, Color Del, Color DelStrong,
        Color AddText, Color DelText, Color Danger, Color Scrollbar, Color ScrollbarHover,
        Color Comment, Color Keyword, Color String, Color Number, Color Type, Color Function, Color Variable, Color Property,
        Color Control);

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>VS Code theme color: <c>#RRGGBBAA</c>.</summary>
    private static string Hex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}");
}

/// <summary>Texts of the conflict editor (user-facing: localized by the caller).</summary>
public sealed record EditorConflictLabels(
    string OursTitle, string OursDetail, string TheirsTitle, string TheirsDetail, string TakeAll, string ResultTitle,
    string ResultDetail, string LegendOurs, string LegendTheirs, string Take, string Ours, string Theirs, string Both, string ToggleTip);

/// <summary>State of one conflict for the editor: labels, kept lines of each side, lines of the result.</summary>
public sealed record EditorConflictBlock(
    string OursLabel, string TheirsLabel, string UnresolvedLabel, bool IsResolved,
    IReadOnlyList<bool> KeptOurs, IReadOnlyList<bool> KeptTheirs, IReadOnlyList<(string Text, ConflictSide Side)> Result);

/// <summary>Commit of the blame column: texts (user-facing: localized by the caller), lane color, age from 0 (oldest) to 1.</summary>
public sealed record EditorBlameCommit(string Summary, string Date, string Initials, int ColorIndex, double Age, string ToolTip);

/// <summary>Texts of the staging controls drawn in a unified diff (user-facing: localized by the caller).</summary>
public sealed record EditorStaging(string ApplyLabel, string? DiscardLabel, string Hint, string ToggleTip);
