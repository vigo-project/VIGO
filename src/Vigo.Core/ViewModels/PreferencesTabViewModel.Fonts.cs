using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A font of a picker: <see cref="Family"/> null for the default font of the zone.</summary>
public sealed record FontOption(string? Family, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A size of the interface text: "110 %" (100: "100 % (par défaut)").</summary>
public sealed record TextScaleOption(int Percent)
{
    public string Label => Percent == 100 ? Loc.Format("Preferences.TextSize.Default", Percent) : Loc.Format("Preferences.TextSize.Percent", Percent);

    public override string ToString() => Label;
}

/// <summary>Choice of the line ending of the editor.</summary>
public sealed record EndOfLineOption(EditorEndOfLine Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Choice of the cursor shape of the terminal.</summary>
public sealed record CursorOption(TerminalCursorStyle Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Fonts and text settings of Préférences: the interface font (Apparence › Mise en forme du texte), the editor and the
/// integrated terminal (Éditeur et outils). The installed fonts are read in the background once.
/// </summary>
public sealed partial class PreferencesTabViewModel
{
    private IReadOnlyList<string> _allFonts = [];
    private IReadOnlyList<string> _monospaceFonts = [];
    private readonly string? _startupUiFont;
    private readonly int _startupUiTextScale;

    public ObservableCollection<FontOption> UiFontOptions { get; } = [];

    public ObservableCollection<FontOption> EditorFontOptions { get; } = [];

    public ObservableCollection<FontOption> TerminalFontOptions { get; } = [];

    public ObservableCollection<EndOfLineOption> EndOfLineOptions { get; } =
    [
        new(EditorEndOfLine.Auto, Loc.Get("Preferences.Eol.Auto")),
        new(EditorEndOfLine.Lf, "LF"),
        new(EditorEndOfLine.Crlf, "CRLF"),
    ];

    public ObservableCollection<CursorOption> CursorOptions { get; } =
    [
        new(TerminalCursorStyle.Bar, Loc.Get("Preferences.Cursor.Bar")),
        new(TerminalCursorStyle.Block, Loc.Get("Preferences.Cursor.Block")),
        new(TerminalCursorStyle.Underline, Loc.Get("Preferences.Cursor.Underline")),
    ];

    // ---- Interface font (restart: the styles read it when a view loads) ----------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiFontNeedsRestart))]
    public partial FontOption? UiFont { get; set; }

    partial void OnUiFontChanged(FontOption? value)
    {
        if (value is not null) Update(p => p with { UiFontFamily = value.Family });
    }

    /// <summary>Sizes offered for the interface text, in percent of the design's.</summary>
    public ObservableCollection<TextScaleOption> UiTextScaleOptions { get; } = [.. new[] { 90, 100, 110, 125, 150 }.Select(p => new TextScaleOption(p))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiFontNeedsRestart))]
    public partial TextScaleOption? UiTextScale { get; set; }

    partial void OnUiTextScaleChanged(TextScaleOption? value)
    {
        if (value is not null) Update(p => p with { UiTextScalePercent = value.Percent });
    }

    /// <summary>The font or the size chosen differs from the one the application started with.</summary>
    public bool UiFontNeedsRestart =>
        (UiFont is not null && UiFont.Family != _startupUiFont) || (UiTextScale is not null && UiTextScale.Percent != _startupUiTextScale);

    // ---- Editor ------------------------------------------------------------------------------------------------------

    /// <summary>"Uniquement les polices à chasse fixe" (the list only; not saved).</summary>
    [ObservableProperty]
    public partial bool EditorMonospaceOnly { get; set; } = true;

    partial void OnEditorMonospaceOnlyChanged(bool value) => FillFonts(EditorFontOptions, value, EditorFont?.Family);

    [ObservableProperty]
    public partial FontOption? EditorFont { get; set; }

    partial void OnEditorFontChanged(FontOption? value)
    {
        if (value is not null) Update(p => p with { EditorFontFamily = value.Family });
    }

    /// <summary>Bound to a NumberBox; a cleared box is NaN, ignored.</summary>
    [ObservableProperty]
    public partial double EditorFontSize { get; set; }

    partial void OnEditorFontSizeChanged(double value)
    {
        if (!double.IsNaN(value)) Update(p => p with { EditorFontSize = (int)Math.Round(value) });
    }

    [ObservableProperty]
    public partial double EditorTabSize { get; set; }

    partial void OnEditorTabSizeChanged(double value)
    {
        if (!double.IsNaN(value)) Update(p => p with { EditorTabSize = (int)Math.Round(value) });
    }

    [ObservableProperty]
    public partial EndOfLineOption? EditorEol { get; set; }

    partial void OnEditorEolChanged(EndOfLineOption? value)
    {
        if (value is not null) Update(p => p with { EditorEndOfLine = value.Value });
    }

    [ObservableProperty]
    public partial bool EditorSyntaxHighlighting { get; set; }

    partial void OnEditorSyntaxHighlightingChanged(bool value) => Update(p => p with { EditorSyntaxHighlighting = value });

    [ObservableProperty]
    public partial bool EditorLineNumbers { get; set; }

    partial void OnEditorLineNumbersChanged(bool value) => Update(p => p with { EditorLineNumbers = value });

    [ObservableProperty]
    public partial bool EditorWordWrap { get; set; }

    partial void OnEditorWordWrapChanged(bool value) => Update(p => p with { EditorWordWrap = value });

    [ObservableProperty]
    public partial bool EditorLigatures { get; set; }

    partial void OnEditorLigaturesChanged(bool value) => Update(p => p with { EditorLigatures = value });

    // ---- Integrated terminal -----------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool TerminalMonospaceOnly { get; set; } = true;

    partial void OnTerminalMonospaceOnlyChanged(bool value) => FillFonts(TerminalFontOptions, value, TerminalFont?.Family);

    [ObservableProperty]
    public partial FontOption? TerminalFont { get; set; }

    partial void OnTerminalFontChanged(FontOption? value)
    {
        if (value is not null) Update(p => p with { TerminalFontFamily = value.Family });
    }

    [ObservableProperty]
    public partial double TerminalFontSize { get; set; }

    partial void OnTerminalFontSizeChanged(double value)
    {
        if (!double.IsNaN(value)) Update(p => p with { TerminalFontSize = (int)Math.Round(value) });
    }

    [ObservableProperty]
    public partial double TerminalLineHeight { get; set; }

    partial void OnTerminalLineHeightChanged(double value)
    {
        if (!double.IsNaN(value)) Update(p => p with { TerminalLineHeight = value });
    }

    [ObservableProperty]
    public partial CursorOption? TerminalCursor { get; set; }

    partial void OnTerminalCursorChanged(CursorOption? value)
    {
        if (value is not null) Update(p => p with { TerminalCursorStyle = value.Value });
    }

    [ObservableProperty]
    public partial bool TerminalDimWhenUnfocused { get; set; }

    partial void OnTerminalDimWhenUnfocusedChanged(bool value) => Update(p => p with { TerminalDimWhenUnfocused = value });

    // ---- Loading and sync ---------------------------------------------------------------------------------------------

    /// <summary>Reads the installed families in the background, then fills the pickers (the current choices are kept).</summary>
    private async Task LoadFontsAsync()
    {
        if (FontCatalog.Current is not { } catalog) return;
        try
        {
            var all = await Task.Run(() => catalog.GetFamilies(false), _lifetime.Token);
            var mono = await Task.Run(() => catalog.GetFamilies(true), _lifetime.Token);
            (_allFonts, _monospaceFonts) = (all, mono);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _syncing = true;
        try
        {
            SyncFonts(_preferences.Current);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncFonts(UserPreferences current)
    {
        FillFonts(UiFontOptions, false, current.UiFontFamily);
        FillFonts(EditorFontOptions, EditorMonospaceOnly, current.EditorFontFamily);
        FillFonts(TerminalFontOptions, TerminalMonospaceOnly, current.TerminalFontFamily);
        SyncTextSettings(current);
    }

    /// <summary>Shows the current text settings (the lists are not rebuilt).</summary>
    private void SyncTextSettings(UserPreferences current)
    {
        UiFont = Pick(UiFontOptions, current.UiFontFamily);
        UiTextScale = UiTextScaleOptions.FirstOrDefault(o => o.Percent == current.UiTextScalePercent) ?? UiTextScaleOptions[1];
        EditorFont = Pick(EditorFontOptions, current.EditorFontFamily);
        TerminalFont = Pick(TerminalFontOptions, current.TerminalFontFamily);
        EditorFontSize = current.EditorFontSize;
        EditorTabSize = current.EditorTabSize;
        EditorEol = EndOfLineOptions.First(o => o.Value == current.EditorEndOfLine);
        EditorSyntaxHighlighting = current.EditorSyntaxHighlighting;
        EditorLineNumbers = current.EditorLineNumbers;
        EditorWordWrap = current.EditorWordWrap;
        EditorLigatures = current.EditorLigatures;
        TerminalFontSize = current.TerminalFontSize;
        TerminalLineHeight = current.TerminalLineHeight;
        TerminalCursor = CursorOptions.First(o => o.Value == current.TerminalCursorStyle);
        TerminalDimWhenUnfocused = current.TerminalDimWhenUnfocused;
    }

    /// <summary>The option of a family; one is added when the list does not have it (not loaded yet, font removed).</summary>
    private static FontOption Pick(ObservableCollection<FontOption> options, string? family)
    {
        var option = options.FirstOrDefault(o => string.Equals(o.Family, family, StringComparison.OrdinalIgnoreCase));
        if (option is not null) return option;
        option = new FontOption(family, family ?? Loc.Get("Preferences.Font.Default"));
        options.Insert(Math.Min(1, options.Count), option);
        return option;
    }

    /// <summary>The default entry, the chosen family even when the filter hides it, then the installed ones.</summary>
    private void FillFonts(ObservableCollection<FontOption> options, bool monospaceOnly, string? chosen)
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            var families = monospaceOnly ? _monospaceFonts : _allFonts;
            options.Clear();
            options.Add(new FontOption(null, Loc.Get("Preferences.Font.Default")));
            if (chosen is not null && !families.Contains(chosen, StringComparer.OrdinalIgnoreCase)) options.Add(new FontOption(chosen, chosen));
            foreach (var family in families) options.Add(new FontOption(family, family));
        }
        finally
        {
            _syncing = wasSyncing;
        }
        // Rebuilding the list clears the selection of the picker: bring it back.
        if (ReferenceEquals(options, EditorFontOptions)) EditorFont = Pick(options, chosen);
        else if (ReferenceEquals(options, TerminalFontOptions)) TerminalFont = Pick(options, chosen);
    }
}
