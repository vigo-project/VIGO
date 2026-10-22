using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Vigo.Core.Models;

/// <summary>Color theme of the application.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    /// <summary>Follows the Windows setting (light / dark apps).</summary>
    System,
    Light,
    Dark,
}

/// <summary>Display language of the application; applied at the next launch (the texts are resolved when a view loads).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppLanguage>))]
public enum AppLanguage
{
    /// <summary>Follows the Windows display language (French when it is French, English for any other).</summary>
    System,
    French,
    English,
}

/// <summary>Where the commit message is written (Preferences › Éditeur et outils › Message de commit IA).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiProvider>))]
public enum AiProvider
{
    /// <summary>Nothing is sent anywhere: the "Générer" button stays disabled.</summary>
    Off,

    /// <summary>A command-line tool installed on the machine (<see cref="AiCliTool"/>): message read on its standard output.</summary>
    Cli,

    /// <summary>The Anthropic Messages API, with a key kept in the Credential Locker.</summary>
    Api,
}

/// <summary>Which tool the <see cref="AiProvider.Cli"/> engine runs: it sets the default command and how the prompt is handed over.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiCliTool>))]
public enum AiCliTool
{
    /// <summary>Claude Code (<c>claude -p</c>): the prompt goes to standard input.</summary>
    Claude,

    /// <summary>GitHub Copilot CLI (<c>copilot -p {prompt}</c>): the prompt is an argument of the command line.</summary>
    Copilot,
}

/// <summary>Row heights and toolbar of the application (mockup: <c>data-density</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Density>))]
public enum Density
{
    Normal,

    /// <summary>22 px rows, toolbar without labels.</summary>
    Compact,
}

/// <summary>How the diff of a file is shown (mockup: "Côte à côte" / "Unifié", Ctrl+Maj+M).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiffLayout>))]
public enum DiffLayout
{
    /// <summary>Hunks one below the other, with staging of hunks and lines.</summary>
    Unified,

    /// <summary>Old and new versions of the whole file next to each other.</summary>
    SideBySide,
}

/// <summary>Line ending of the text written by the editor (conflict result, editable text).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EditorEndOfLine>))]
public enum EditorEndOfLine
{
    /// <summary>Keeps the ending of the file.</summary>
    Auto,
    Lf,
    Crlf,
}

/// <summary>Shape of the integrated terminal's cursor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TerminalCursorStyle>))]
public enum TerminalCursorStyle
{
    Bar,
    Block,
    Underline,
}

/// <summary>
/// User preferences (<c>%LOCALAPPDATA%\VIGO\preferences.json</c>), edited in the preferences tab (Ctrl+,); the file
/// is read at launch.
/// </summary>
public sealed record UserPreferences
{
    public const int DefaultAutoFetchMinutes = 1;
    public const int MaxAutoFetchMinutes = 60;
    public const int DefaultGraphInitialCommits = 2000;
    public const int MinGraphInitialCommits = 500;
    public const int MaxGraphInitialCommits = 100_000;
    public const int DefaultEditorFontSize = 12;
    public const int DefaultTabSize = 4;
    public const int DefaultTerminalFontSize = 12;
    public const double DefaultTerminalLineHeight = 1.15;

    /// <summary>Minutes between two automatic fetches of the visible repository; 0 disables them.</summary>
    public int AutoFetchIntervalMinutes { get; init; } = DefaultAutoFetchMinutes;

    /// <summary>The graph reads more commits only as the view scrolls toward the end, after the first page (J7.28).</summary>
    public bool GraphLazyLoad { get; init; }

    /// <summary>Size of the first page of commits of the graph (J7.27), shown before the rest; applies at the next load.</summary>
    public int GraphInitialCommits { get; init; } = DefaultGraphInitialCommits;

    /// <summary>After a pull or a checkout, updates the submodules (<c>submodule update --init --recursive</c>, J7.26).</summary>
    public bool KeepSubmodulesUpToDate { get; init; }

    /// <summary>Every fetch passes <c>--prune</c> (J7.25); off leaves it to <c>fetch.prune</c> in the git config.</summary>
    public bool AutoPrune { get; init; } = true;

    /// <summary>Folder the last clone went into: the next one is proposed there.</summary>
    public string? LastCloneParent { get; init; }

    /// <summary>The clone dialog fills the SSH address of a repository picked from a hosting service instead of the HTTPS one (J6.13).</summary>
    public bool CloneUseSsh { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.System;

    public Density Density { get; init; } = Density.Normal;

    /// <summary>Display language; read at launch (changing it asks for a restart).</summary>
    public AppLanguage Language { get; init; } = AppLanguage.System;

    /// <summary>Last layout chosen in the diff view.</summary>
    public DiffLayout DiffLayout { get; init; } = DiffLayout.Unified;

    /// <summary>"Extraire après création" of the new branch dialog: checked by default, the last choice is kept.</summary>
    public bool CheckoutNewBranch { get; init; } = true;

    /// <summary>
    /// Private key VIGO hands to ssh for its network commands (neither <c>~/.gitconfig</c> nor
    /// <c>~/.ssh/config</c> is changed); null leaves the choice to ssh (<c>~/.ssh/config</c>, default key names).
    /// </summary>
    public string? SshKeyPath { get; init; }

    /// <summary>Keys added with "Importer une clé…" (outside <c>~/.ssh</c>), listed with those of <c>~/.ssh</c>.</summary>
    public ImmutableArray<string> ImportedSshKeys { get; init; } = [];

    /// <summary>Runs the Windows OpenSSH client, whose ssh-agent service keeps the passphrases.</summary>
    public bool UseWindowsSshAgent { get; init; }

    /// <summary><see cref="MergeTool"/> value for the tool of the Git configuration (<c>merge.tool</c>).</summary>
    public const string GitConfiguredMergeTool = "git-config";

    /// <summary>
    /// External merge tool that replaces the conflict editor: a tool name of <c>git mergetool</c> (<c>bc</c>,
    /// <c>kdiff3</c>, <c>vscode</c>…) or <see cref="GitConfiguredMergeTool"/>; null for VIGO's conflict editor.
    /// </summary>
    public string? MergeTool { get; init; }

    /// <summary>Shortcuts that differ from <see cref="ShortcutCatalog"/> defaults; the others keep their default.</summary>
    public ImmutableArray<ShortcutBinding> Shortcuts { get; init; } = [];

    /// <summary>Engine of "Générer" in the commit form (J7.9); off until the user chooses one.</summary>
    public AiProvider AiProvider { get; init; } = AiProvider.Off;

    /// <summary>Tool run by the <see cref="AiProvider.Cli"/> engine.</summary>
    public AiCliTool AiCliTool { get; init; } = AiCliTool.Claude;

    /// <summary>Command line of the Claude tool (prompt on stdin); null for <c>claude -p</c>.</summary>
    public string? AiCliCommand { get; init; }

    /// <summary>Command line of the Copilot engine; <c>{prompt}</c> stands for the prompt; null for the default.</summary>
    public string? AiCopilotCommand { get; init; }

    /// <summary>Model asked of the engine; null for its default.</summary>
    public string? AiModel { get; init; }

    /// <summary>Shell of a new integrated terminal (<see cref="TerminalShellIds"/>); null: the first shell found.</summary>
    public string? TerminalShell { get; init; }

    /// <summary>Command line that replaces the one of the default shell (<c>pwsh.exe -NoLogo</c>); null keeps it.</summary>
    public string? TerminalCommand { get; init; }

    /// <summary>Font of the interface; null for Segoe UI Variable. Read at launch (changing it asks for a restart).</summary>
    public string? UiFontFamily { get; init; }

    /// <summary>Size of the interface text, in percent of the design's (80 to 200); read at launch like the font.</summary>
    public int UiTextScalePercent { get; init; } = 100;

    /// <summary><see cref="UiTextScalePercent"/> as a factor.</summary>
    [JsonIgnore]
    public double UiTextScale => UiTextScalePercent / 100.0;

    /// <summary>Monospace font of the code editor (diff, conflicts, blame); null for Cascadia Mono.</summary>
    public string? EditorFontFamily { get; init; }

    public int EditorFontSize { get; init; } = DefaultEditorFontSize;

    public int EditorTabSize { get; init; } = DefaultTabSize;

    public EditorEndOfLine EditorEndOfLine { get; init; } = EditorEndOfLine.Auto;

    public bool EditorSyntaxHighlighting { get; init; } = true;

    public bool EditorLineNumbers { get; init; } = true;

    public bool EditorWordWrap { get; init; }

    /// <summary>Joined glyphs for <c>=&gt;</c>, <c>!=</c>… with a font that has them (J9.42).</summary>
    public bool EditorLigatures { get; init; }

    /// <summary>Monospace font of the integrated terminal; null for Cascadia Mono and the Nerd Fonts.</summary>
    public string? TerminalFontFamily { get; init; }

    public int TerminalFontSize { get; init; } = DefaultTerminalFontSize;

    public double TerminalLineHeight { get; init; } = DefaultTerminalLineHeight;

    public TerminalCursorStyle TerminalCursorStyle { get; init; } = TerminalCursorStyle.Bar;

    /// <summary>The terminal is dimmed while it does not have the focus.</summary>
    public bool TerminalDimWhenUnfocused { get; init; } = true;

    /// <summary>Custom graph palette (see <see cref="Models.LaneColors"/>); empty keeps the design system colors.</summary>
    public ImmutableArray<string> LaneColors { get; init; } = [];

    /// <summary>Version whose release notes were shown at launch; another version shows its own once.</summary>
    public string? LastSeenVersion { get; init; }

    /// <summary>Null when automatic fetch is disabled.</summary>
    public TimeSpan? AutoFetchInterval =>
        AutoFetchIntervalMinutes > 0 ? TimeSpan.FromMinutes(AutoFetchIntervalMinutes) : null;

    /// <summary>Settings of the code editor, as the pages of the editor receive them.</summary>
    [JsonIgnore]
    public EditorSettings Editor => new(EditorFontFamily, EditorFontSize, EditorTabSize, EditorEndOfLine, EditorSyntaxHighlighting, EditorLineNumbers, EditorWordWrap, EditorLigatures);

    /// <summary>Settings of the integrated terminal.</summary>
    [JsonIgnore]
    public TerminalSettings Terminal => new(TerminalFontFamily, TerminalFontSize, TerminalLineHeight, TerminalCursorStyle, TerminalDimWhenUnfocused);

    /// <summary>What git.exe needs to know of the SSH settings.</summary>
    [JsonIgnore]
    public SshSettings Ssh => new(SshKeyPath, UseWindowsSshAgent);

    /// <summary>Values brought back into their range (hand-edited file).</summary>
    public UserPreferences Normalize() => this with
    {
        AutoFetchIntervalMinutes = Math.Clamp(AutoFetchIntervalMinutes, 0, MaxAutoFetchMinutes),
        GraphInitialCommits = Math.Clamp(GraphInitialCommits, MinGraphInitialCommits, MaxGraphInitialCommits),
        LastCloneParent = string.IsNullOrWhiteSpace(LastCloneParent) ? null : LastCloneParent,
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
        Density = Enum.IsDefined(Density) ? Density : Density.Normal,
        Language = Enum.IsDefined(Language) ? Language : AppLanguage.System,
        DiffLayout = Enum.IsDefined(DiffLayout) ? DiffLayout : DiffLayout.Unified,
        SshKeyPath = string.IsNullOrWhiteSpace(SshKeyPath) ? null : SshKeyPath.Trim(),
        ImportedSshKeys = NormalizeKeys(ImportedSshKeys),
        MergeTool = string.IsNullOrWhiteSpace(MergeTool) ? null : MergeTool.Trim(),
        Shortcuts = NormalizeShortcuts(Shortcuts),
        LaneColors = Models.LaneColors.Normalize(LaneColors),
        AiProvider = Enum.IsDefined(AiProvider) ? AiProvider : AiProvider.Off,
        AiCliTool = Enum.IsDefined(AiCliTool) ? AiCliTool : AiCliTool.Claude,
        AiCliCommand = string.IsNullOrWhiteSpace(AiCliCommand) ? null : AiCliCommand.Trim(),
        AiCopilotCommand = string.IsNullOrWhiteSpace(AiCopilotCommand) ? null : AiCopilotCommand.Trim(),
        AiModel = string.IsNullOrWhiteSpace(AiModel) ? null : AiModel.Trim(),
        TerminalShell = string.IsNullOrWhiteSpace(TerminalShell) ? null : TerminalShell.Trim(),
        TerminalCommand = string.IsNullOrWhiteSpace(TerminalCommand) ? null : TerminalCommand.Trim(),
        UiFontFamily = string.IsNullOrWhiteSpace(UiFontFamily) ? null : UiFontFamily.Trim(),
        UiTextScalePercent = Math.Clamp(UiTextScalePercent, 80, 200),
        EditorFontFamily = string.IsNullOrWhiteSpace(EditorFontFamily) ? null : EditorFontFamily.Trim(),
        EditorFontSize = Math.Clamp(EditorFontSize, 8, 32),
        EditorTabSize = Math.Clamp(EditorTabSize, 1, 8),
        EditorEndOfLine = Enum.IsDefined(EditorEndOfLine) ? EditorEndOfLine : EditorEndOfLine.Auto,
        TerminalFontFamily = string.IsNullOrWhiteSpace(TerminalFontFamily) ? null : TerminalFontFamily.Trim(),
        TerminalFontSize = Math.Clamp(TerminalFontSize, 8, 32),
        TerminalLineHeight = double.IsFinite(TerminalLineHeight) ? Math.Clamp(Math.Round(TerminalLineHeight, 2), 1.0, 2.0) : DefaultTerminalLineHeight,
        TerminalCursorStyle = Enum.IsDefined(TerminalCursorStyle) ? TerminalCursorStyle : TerminalCursorStyle.Bar,
    };

    // Last binding of a command wins; an unreadable gesture is dropped (the default applies); "" stays: unassigned.
    private static ImmutableArray<ShortcutBinding> NormalizeShortcuts(ImmutableArray<ShortcutBinding> bindings)
    {
        if (bindings.IsDefaultOrEmpty) return [];
        var cleaned = bindings
            .Where(b => !string.IsNullOrWhiteSpace(b.Command) && (b.Gesture == string.Empty || KeyGesture.TryParse(b.Gesture, out _)))
            .Select(b => b.Gesture == string.Empty ? b : b with { Gesture = KeyGesture.TryParse(b.Gesture, out var g) ? g.Serialize() : b.Gesture })
            .GroupBy(b => b.Command, StringComparer.Ordinal).Select(g => g.Last()).ToImmutableArray();
        return cleaned.SequenceEqual(bindings) ? bindings : cleaned;
    }

    private static ImmutableArray<string> NormalizeKeys(ImmutableArray<string> keys)
    {
        if (keys.IsDefaultOrEmpty) return [];
        var cleaned = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        // Same content: keep the instance, so that the record stays equal (no needless save).
        return cleaned.SequenceEqual(keys) ? keys : cleaned;
    }
}

/// <summary>A shortcut changed by the user.</summary>
/// <param name="Command">Identifier of the command (<see cref="ShortcutIds"/>).</param>
/// <param name="Gesture">Serialized gesture (<c>Ctrl+Shift+L</c>); empty when the command has no shortcut.</param>
public sealed record ShortcutBinding(string Command, string Gesture);

/// <summary>Editor preferences handed to the Monaco page (family null: the page's default monospace font).</summary>
public sealed record EditorSettings(
    string? FontFamily, int FontSize, int TabSize, EditorEndOfLine EndOfLine, bool SyntaxHighlighting, bool LineNumbers, bool WordWrap, bool Ligatures)
{
    public static EditorSettings Default { get; } = new UserPreferences().Editor;
}

/// <summary>Terminal preferences handed to the xterm.js page (family null: the page's default monospace fonts).</summary>
public sealed record TerminalSettings(string? FontFamily, int FontSize, double LineHeight, TerminalCursorStyle Cursor, bool DimWhenUnfocused)
{
    public static TerminalSettings Default { get; } = new UserPreferences().Terminal;
}

/// <summary>SSH settings applied by VIGO to the network commands of git.exe.</summary>
/// <param name="KeyPath">Private key to use; null: ssh's own choice.</param>
/// <param name="UseWindowsAgent">Windows OpenSSH client (and its ssh-agent service) instead of the one of Git.</param>
public sealed record SshSettings(string? KeyPath, bool UseWindowsAgent);

/// <summary>Sizes that depend on the density, shared by the XAML views and the Win2D controls.</summary>
public sealed record DensityMetrics(double RowHeight, double LaneWidth, double CodeLineHeight, double ToolbarButtonWidth, double ToolbarButtonHeight, bool ShowToolbarLabels)
{
    /// <summary>Mockup: <c>--row:28px</c>, lanes 14 px, code 20 px, 56 × 44 toolbar buttons with labels.</summary>
    public static DensityMetrics Normal { get; } = new(28, 14, 20, 56, 44, true);

    /// <summary>Mockup: <c>--row:22px</c>, lanes 11 px, code 18 px, 32 × 30 toolbar buttons without labels.</summary>
    public static DensityMetrics Compact { get; } = new(22, 11, 18, 32, 30, false);

    public static DensityMetrics For(Density density) => density == Density.Compact ? Compact : Normal;
}
