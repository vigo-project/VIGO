using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.UI;

namespace Vigo.App.Controls;

/// <summary>Key pressed in the editor page that belongs to the app (see <see cref="CodeEditor.KeyPressed"/>).</summary>
/// <param name="Key">
/// <c>S</c>, <c>Delete</c>, <c>AltUp</c>, <c>AltDown</c>, <c>Escape</c> for the diff keys; otherwise a DOM key code
/// (<c>KeyT</c>, <c>F5</c>, <c>Comma</c>…) of a shortcut the editor did not use.
/// </param>
/// <param name="VirtualKey">Windows virtual-key code of the active keyboard layout; 0 for the diff keys.</param>
public sealed record EditorKey(string Key, int VirtualKey, bool Control, bool Shift, bool Alt)
{
    public Windows.System.VirtualKeyModifiers Modifiers =>
        (Control ? Windows.System.VirtualKeyModifiers.Control : 0)
        | (Shift ? Windows.System.VirtualKeyModifiers.Shift : 0)
        | (Alt ? Windows.System.VirtualKeyModifiers.Menu : 0);
}

/// <summary>
/// Code editor (Monaco in a WebView2, ADR-038): unified diff with line staging, side-by-side diff, plain text
/// (read-only or editable); the blame is native (LoomEditor, <c>CodeEditor.Native.cs</c>), the other modes follow. The page is served from the app folder (<c>Editor/</c>, built by
/// <c>tools/editor/build.mjs</c>); messages go both ways as JSON. One WebView2 environment (browser process) is
/// shared by every editor of the app and started early (<see cref="Prewarm"/>).
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private const string HostName = "vigo.editor";
    private static readonly string PageUri = $"https://{HostName}/index.html?lang={EditorLanguage}";

    private static Task<CoreWebView2Environment>? s_environment;

    // Created with the first content to show: a repository tab whose diff is never opened costs no renderer.
    private WebView2? _view;
    private readonly List<string> _pending = [];
    private bool _starting;
    private bool _ready;
    private bool _closed;

    public CodeEditor()
    {
        IsTabStop = false;
        Loaded += (_, _) =>
        {
            if (_pending.Count > 0) Start();
            // ActualThemeChanged is not raised while the control is out of the tree (tab not shown, view cached): a
            // theme changed meanwhile is sent when it comes back.
            SendTheme();
            ApplyNativeTheme();
        };
        ActualThemeChanged += (_, _) =>
        {
            SendTheme();
            ApplyNativeTheme();
        };
        Appearance.Current.PropertyChanged += OnAppearanceChanged;
    }

    /// <summary>Language of the editor's own texts (find box, context menu): the UI language when Monaco has it.</summary>
    private static string EditorLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en";

    /// <summary>A changed line's selection box was clicked (document line, 1-based; Shift for a range).</summary>
    public event EventHandler<(int Line, bool Extend)>? LineToggled;

    /// <summary>A hunk header button was clicked: stage / unstage, or discard.</summary>
    public event EventHandler<(int Hunk, bool Discard)>? HunkInvoked;

    public event EventHandler<EditorKey>? KeyPressed;

    /// <summary>The text of an editable editor changed.</summary>
    public event EventHandler? TextChanged;

    /// <summary>Starts the browser process in the background, so that the first editor opens at once.</summary>
    public static void Prewarm() => _ = EnvironmentAsync();

    /// <summary>The WebView2 environment shared by the editors of the app.</summary>
    internal static Task<CoreWebView2Environment> EnvironmentAsync() => s_environment ??= CreateEnvironmentAsync();

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        // VIGO_WEBVIEW2_USER_DATA: a test instance (replays with a DevTools port) must not share the browser process of the
        // user's own VIGO, whose arguments differ.
        var userData = Environment.GetEnvironmentVariable("VIGO_WEBVIEW2_USER_DATA") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "WebView2");
        var options = new CoreWebView2EnvironmentOptions { Language = EditorLanguage };
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
    }

    /// <summary>Shows a unified diff; <paramref name="staging"/> null for a read-only one.</summary>
    public void ShowDiff(string path, DiffDocument document, IReadOnlyList<int> selected, EditorStaging? staging, bool keepScroll) =>
        Send(w =>
        {
            w.WriteString("type", "diff");
            w.WriteString("path", path);
            w.WriteString("text", document.Text);
            w.WriteString("kinds", document.Kinds);
            WriteNumbers(w, "old", document.OldNumbers);
            WriteNumbers(w, "new", document.NewNumbers);
            w.WriteStartArray("hunks");
            foreach (var hunk in document.Hunks)
            {
                w.WriteStartObject();
                w.WriteNumber("line", hunk.FirstLine);
                w.WriteString("header", hunk.Header);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            WriteNumbers(w, "selected", selected);
            if (staging is { } s)
            {
                w.WriteStartObject("staging");
                w.WriteString("apply", s.ApplyLabel);
                if (s.DiscardLabel is { } discard) w.WriteString("discard", discard);
                w.WriteString("hint", s.Hint);
                w.WriteString("toggleTip", s.ToggleTip);
                w.WriteEndObject();
            }
            w.WriteBoolean("keepScroll", keepScroll);
        }, content: true);

    public void SetSelection(IReadOnlyList<int> lines) => Send(w =>
    {
        w.WriteString("type", "selection");
        WriteNumbers(w, "lines", lines);
    });

    public void RevealHunk(int hunk) => Send(w =>
    {
        w.WriteString("type", "revealHunk");
        w.WriteNumber("hunk", hunk);
    });

    /// <summary>
    /// Shows a side-by-side diff; <paramref name="native"/> false keeps it in the page, for the inline review of a pull
    /// request until the review moves to the native editor (ADR-090).
    /// </summary>
    public void ShowSideBySide(DiffSides sides, bool native = true)
    {
        if (_closed) return;
        if (native) ShowNativeSideBySide(sides);
        else ShowPageSideBySide(sides);
    }

    private void ShowPageSideBySide(DiffSides sides) => Send(w =>
    {
        w.WriteString("type", "sideBySide");
        w.WriteString("path", sides.Path);
        w.WriteString("oldPath", sides.OldPath);
        w.WriteString("original", sides.Original);
        w.WriteString("modified", sides.Modified);
    }, content: true);

    /// <summary>Shows the conflict editor: panes ours / theirs built from the segments, result with the chosen lines.</summary>
    public void ShowConflict(string path, EditorConflictLabels labels, ConflictFile file, IReadOnlyList<EditorConflictBlock> blocks, int current) =>
        Send(w =>
        {
            w.WriteString("type", "conflict");
            w.WriteString("path", path);
            w.WriteStartObject("labels");
            w.WriteString("oursTitle", labels.OursTitle);
            w.WriteString("oursDetail", labels.OursDetail);
            w.WriteString("theirsTitle", labels.TheirsTitle);
            w.WriteString("theirsDetail", labels.TheirsDetail);
            w.WriteString("takeAll", labels.TakeAll);
            w.WriteString("resultTitle", labels.ResultTitle);
            w.WriteString("resultDetail", labels.ResultDetail);
            w.WriteString("legendOurs", labels.LegendOurs);
            w.WriteString("legendTheirs", labels.LegendTheirs);
            w.WriteString("take", labels.Take);
            w.WriteString("ours", labels.Ours);
            w.WriteString("theirs", labels.Theirs);
            w.WriteString("both", labels.Both);
            w.WriteString("toggleTip", labels.ToggleTip);
            w.WriteEndObject();
            w.WriteStartArray("segments");
            foreach (var segment in file.Segments)
            {
                w.WriteStartObject();
                switch (segment)
                {
                    case CommonLines common:
                        WriteStrings(w, "c", common.Lines);
                        break;
                    case ConflictHunk hunk:
                        WriteStrings(w, "o", hunk.Ours);
                        WriteStrings(w, "t", hunk.Theirs);
                        break;
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("state");
            WriteConflictState(w, blocks, current);
            w.WriteEndObject();
            w.WriteStartArray("results");
            foreach (var block in blocks)
            {
                w.WriteStartArray();
                foreach (var line in block.Result) WriteResultLine(w, line);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }, content: true);

    /// <summary>
    /// New state of the conflicts (labels, kept lines, current one); <paramref name="changed"/>: conflicts whose lines
    /// are replaced in the result.
    /// </summary>
    public void SetConflictState(IReadOnlyList<EditorConflictBlock> blocks, int current, bool reveal, IReadOnlyList<int> changed) => Send(w =>
    {
        w.WriteString("type", "conflictState");
        WriteConflictState(w, blocks, current);
        w.WriteBoolean("reveal", reveal);
        w.WriteStartArray("set");
        foreach (var index in changed)
        {
            w.WriteStartObject();
            w.WriteNumber("block", index);
            w.WriteStartArray("lines");
            foreach (var line in blocks[index].Result) WriteResultLine(w, line);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private static void WriteConflictState(Utf8JsonWriter w, IReadOnlyList<EditorConflictBlock> blocks, int current)
    {
        w.WriteNumber("current", current);
        w.WriteStartArray("blocks");
        foreach (var block in blocks)
        {
            w.WriteStartObject();
            w.WriteString("oursLabel", block.OursLabel);
            w.WriteString("theirsLabel", block.TheirsLabel);
            w.WriteString("unresolved", block.UnresolvedLabel);
            w.WriteBoolean("done", block.IsResolved);
            WriteBooleans(w, "ours", block.KeptOurs);
            WriteBooleans(w, "theirs", block.KeptTheirs);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteResultLine(Utf8JsonWriter w, (string Text, ConflictSide Side) line)
    {
        w.WriteStartObject();
        w.WriteString("t", line.Text);
        w.WriteString("s", line.Side == ConflictSide.Ours ? "o" : "t");
        w.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values) w.WriteStringValue(value);
        w.WriteEndArray();
    }

    private static void WriteBooleans(Utf8JsonWriter w, string name, IReadOnlyList<bool> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values) w.WriteBooleanValue(value);
        w.WriteEndArray();
    }

    /// <summary>A check box of a conflict line was clicked in a pane.</summary>
    public event EventHandler<(int Block, ConflictSide Side, int Line)>? ConflictLineToggled;

    /// <summary>"Prendre ce bloc" of a pane, or Ours / Theirs / Les deux of an unresolved conflict of the result.</summary>
    public event EventHandler<(int Block, ConflictTake Take)>? ConflictTaken;

    /// <summary>"Tout prendre" of a pane.</summary>
    public event EventHandler<ConflictSide>? ConflictAllTaken;

    private readonly Dictionary<int, TaskCompletionSource<string?>> _textRequests = [];
    private int _nextRequest;

    /// <summary>Text of the result pane of the conflict editor; null when the page is not ready or closed.</summary>
    public Task<string?> GetConflictResultAsync()
    {
        if (!_ready || _closed) return Task.FromResult<string?>(null);
        var id = ++_nextRequest;
        var request = _textRequests[id] = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(w =>
        {
            w.WriteString("type", "getText");
            w.WriteNumber("id", id);
            w.WriteString("target", "result");
        });
        return request.Task;
    }

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

    public void Clear()
    {
        ClearNative();
        // The page is not started just to be cleared.
        if (_view is not null) Send(w => w.WriteString("type", "clear"), content: true);
    }

    /// <summary>Gives the keyboard focus to the editor.</summary>
    public void FocusEditor()
    {
        if (NativeFocusTarget is { } target)
        {
            target.Focus(FocusState.Programmatic);
            return;
        }
        _view?.Focus(FocusState.Programmatic);
        Send(w => w.WriteString("type", "focus"));
    }

    /// <summary>Closes the page and its renderer when the tab is closed for good.</summary>
    public void Release()
    {
        if (_closed) return;
        _closed = true;
        Appearance.Current.PropertyChanged -= OnAppearanceChanged;
        _pending.Clear();
        foreach (var request in _textRequests.Values) request.TrySetResult(null);
        _textRequests.Clear();
        _view?.Close();
        ReleaseNative();
    }

    /// <summary>Creates the WebView2 and loads the page (once, when there is something to show and the control is loaded).</summary>
    private async void Start()
    {
        if (_starting || _closed || !IsLoaded) return;
        _starting = true;
        // No white flash while the page loads: the page background until then.
        var view = _view = new WebView2 { DefaultBackgroundColor = PageBackground() };
        Content = view;
        try
        {
            await view.EnsureCoreWebView2Async(await EnvironmentAsync());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException
            or FileNotFoundException or UnauthorizedAccessException)
        {
            // Closed while starting (tab closed at once): not a failure.
            if (_closed) return;
            LogStartFailed(Logger, ex);
            Failed = true;
            EditorFailed?.Invoke(this, ex);
            return;
        }
        if (_closed) return;

        var core = view.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreHostObjectsAllowed = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "Editor"), CoreWebView2HostResourceAccessKind.DenyCors);
        // Only the editor page: links and drops never navigate away.
        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate(PageUri);
    }

    /// <summary>The WebView2 runtime could not start (missing runtime, broken install).</summary>
    public event EventHandler<Exception>? EditorFailed;

    /// <summary>The editor could not start (<see cref="EditorFailed"/> was raised).</summary>
    public bool Failed { get; private set; }

    /// <summary>Log of the editors (set once at startup).</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Error, Message = "The code editor (WebView2) could not start")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Code editor page error: {Message}")]
    private static partial void LogPageError(ILogger logger, string message);

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // Only the page of the virtual host talks to the app.
        if (!args.Source.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase)) return;

        using var json = JsonDocument.Parse(args.WebMessageAsJson);
        var message = json.RootElement;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type)) return;

        switch (type.GetString())
        {
            case "ready":
                _ready = true;
                SendTheme();
                foreach (var pending in _pending) _view!.CoreWebView2.PostWebMessageAsJson(pending);
                _pending.Clear();
                ReplayReview();
                break;
            case "toggleLine":
                LineToggled?.Invoke(this, (message.GetProperty("line").GetInt32(), message.GetProperty("extend").GetBoolean()));
                break;
            case "applyHunk":
                HunkInvoked?.Invoke(this, (message.GetProperty("hunk").GetInt32(), false));
                break;
            case "discardHunk":
                HunkInvoked?.Invoke(this, (message.GetProperty("hunk").GetInt32(), true));
                break;
            case "key":
                KeyPressed?.Invoke(this, new EditorKey(
                    message.GetProperty("key").GetString() ?? string.Empty,
                    message.GetProperty("vk").GetInt32(),
                    message.GetProperty("ctrl").GetBoolean(),
                    message.GetProperty("shift").GetBoolean(),
                    message.GetProperty("alt").GetBoolean()));
                break;
            case "changed":
                TextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "text":
                if (_textRequests.Remove(message.GetProperty("id").GetInt32(), out var request))
                    request.TrySetResult(message.GetProperty("text").GetString());
                break;
            case "conflictToggle":
                ConflictLineToggled?.Invoke(this, (message.GetProperty("block").GetInt32(), SideOf(message.GetProperty("side").GetString()),
                    message.GetProperty("line").GetInt32()));
                break;
            case "conflictTake":
                ConflictTaken?.Invoke(this, (message.GetProperty("block").GetInt32(), message.GetProperty("take").GetString() switch
                {
                    "ours" => ConflictTake.Ours,
                    "theirs" => ConflictTake.Theirs,
                    _ => ConflictTake.Both,
                }));
                break;
            case "conflictTakeAll":
                ConflictAllTaken?.Invoke(this, SideOf(message.GetProperty("side").GetString()));
                break;
            case "blameSelect":
                BlameLineSelected?.Invoke(this, message.GetProperty("line").GetInt32());
                break;
            case "error":
                LogPageError(Logger, message.GetProperty("message").GetString() ?? string.Empty);
                break;
            default:
                TryHandleReview(type.GetString(), message);
                break;
        }
    }

    private static ConflictSide SideOf(string? side) => side == "theirs" ? ConflictSide.Theirs : ConflictSide.Ours;

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Appearance.Metrics) or nameof(Appearance.Editor))) return;
        SendTheme();
        ApplyNativeTheme();
    }

    /// <summary>Design tokens of the current theme, density line height (ADR-032: changed live).</summary>
    private void SendTheme()
    {
        if (!_ready) return;
        var dark = ActualTheme != ElementTheme.Light;
        _view!.DefaultBackgroundColor = PageBackground();
        var editor = Appearance.Current.Editor;
        Post(w =>
        {
            w.WriteString("type", "theme");
            w.WriteBoolean("dark", dark);
            w.WriteNumber("fontSize", editor.FontSize);
            // The density's line height is for the default size; it grows with the chosen one.
            w.WriteNumber("lineHeight", Math.Round(Appearance.Current.Metrics.CodeLineHeight * editor.FontSize / UserPreferences.DefaultEditorFontSize));
            if (editor.FontFamily is { } family) w.WriteString("fontFamily", family);
            w.WriteNumber("tabSize", editor.TabSize);
            w.WriteString("eol", editor.EndOfLine switch { EditorEndOfLine.Lf => "lf", EditorEndOfLine.Crlf => "crlf", _ => "auto" });
            w.WriteBoolean("syntax", editor.SyntaxHighlighting);
            w.WriteBoolean("lineNumbers", editor.LineNumbers);
            w.WriteBoolean("wordWrap", editor.WordWrap);
            w.WriteBoolean("ligatures", editor.Ligatures);
            w.WriteStartObject("colors");
            if (dark) WriteColors(w, Dark);
            else WriteColors(w, Light);
            WriteConflictColors(w, dark);
            w.WriteEndObject();
        });
    }

    private Color PageBackground() => ActualTheme == ElementTheme.Light ? DesignTokens.Light.Sunken : DesignTokens.Dark.Sunken;

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

    // Names read by editor.js (CSS variables --vg-<name> and the Monaco theme).
    private static void WriteColors(Utf8JsonWriter w, Palette p)
    {
        w.WriteString("background", Hex(p.Background));
        w.WriteString("layer", Hex(p.Layer));
        w.WriteString("header", Hex(p.Header));
        w.WriteString("text", Hex(p.Text));
        w.WriteString("text2", Hex(p.Text2));
        w.WriteString("text3", Hex(p.Text3));
        w.WriteString("stroke", Hex(p.Stroke));
        w.WriteString("strokeStrong", Hex(p.StrokeStrong));
        w.WriteString("strokeControl", Hex(p.StrokeControl));
        w.WriteString("ctrl", Hex(p.Ctrl));
        w.WriteString("accent", Hex(p.Accent));
        w.WriteString("selection", Hex(p.Selected));
        w.WriteString("selected", Hex(p.Selected));
        w.WriteString("add", Hex(p.Add));
        w.WriteString("addStrong", Hex(p.AddStrong));
        w.WriteString("del", Hex(p.Del));
        w.WriteString("delStrong", Hex(p.DelStrong));
        w.WriteString("addText", Hex(p.AddText));
        w.WriteString("delText", Hex(p.DelText));
        w.WriteString("danger", Hex(p.Danger));
        w.WriteString("scrollbar", Hex(p.Scrollbar));
        w.WriteString("scrollbarHover", Hex(p.ScrollbarHover));
        w.WriteString("synComment", Hex(p.Comment));
        w.WriteString("synKeyword", Hex(p.Keyword));
        w.WriteString("synString", Hex(p.String));
        w.WriteString("synNumber", Hex(p.Number));
        w.WriteString("synType", Hex(p.Type));
        w.WriteString("synFunction", Hex(p.Function));
        w.WriteString("synVariable", Hex(p.Variable));
        w.WriteString("synProperty", Hex(p.Property));
        w.WriteString("synControl", Hex(p.Control));
    }

    /// <summary>
    /// Conflict editor (mockup): ours in lane color 5 (violet), theirs in lane color 1 (blue), tinted at 14 % in the
    /// panes and 8 % in the result; warning colors of the unresolved conflicts.
    /// </summary>
    private static void WriteConflictColors(Utf8JsonWriter w, bool dark)
    {
        var ours = DesignTokens.Lanes[4];
        var theirs = DesignTokens.Lanes[0];
        w.WriteString("ours", Hex(ours));
        w.WriteString("theirs", Hex(theirs));
        w.WriteString("oursSoft", Hex(WithAlpha(ours, 0x24)));
        w.WriteString("theirsSoft", Hex(WithAlpha(theirs, 0x24)));
        w.WriteString("oursFaint", Hex(WithAlpha(ours, 0x14)));
        w.WriteString("theirsFaint", Hex(WithAlpha(theirs, 0x14)));
        w.WriteString("warning", Hex(dark ? DesignTokens.Dark.Warning : DesignTokens.Light.Warning));
        w.WriteString("warningSoft", Hex(dark ? DesignTokens.Dark.WarningSoft : DesignTokens.Light.WarningSoft));
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>CSS / Monaco color: <c>#RRGGBBAA</c>.</summary>
    private static string Hex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}");

    private static void WriteNumbers(Utf8JsonWriter w, string name, IReadOnlyList<int> numbers)
    {
        w.WriteStartArray(name);
        foreach (var n in numbers) w.WriteNumberValue(n);
        w.WriteEndArray();
    }

    /// <summary>
    /// Posts a message now, or once the page is ready (starting it if needed). A new <paramref name="content"/>
    /// replaces what is still waiting: only the last diff opened before the page is ready is shown.
    /// </summary>
    private void Send(Action<Utf8JsonWriter> write, bool content = false)
    {
        if (_closed) return;
        if (content && _view is not null && !ReferenceEquals(Content, _view)) Content = _view;
        var json = Serialize(write);
        if (_ready)
        {
            _view!.CoreWebView2.PostWebMessageAsJson(json);
            return;
        }
        if (content) _pending.Clear();
        _pending.Add(json);
        Start();
    }

    private void Post(Action<Utf8JsonWriter> write) => _view!.CoreWebView2.PostWebMessageAsJson(Serialize(write));

    // Read by JSON.parse in the page, never inserted as HTML: non-ASCII text (French, code) is sent as is.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Serialize(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>Texts of the conflict editor page (user-facing: localized by the caller).</summary>
public sealed record EditorConflictLabels(
    string OursTitle, string OursDetail, string TheirsTitle, string TheirsDetail, string TakeAll, string ResultTitle,
    string ResultDetail, string LegendOurs, string LegendTheirs, string Take, string Ours, string Theirs, string Both, string ToggleTip);

/// <summary>State of one conflict for the page: labels, kept lines of each side, lines of the result.</summary>
public sealed record EditorConflictBlock(
    string OursLabel, string TheirsLabel, string UnresolvedLabel, bool IsResolved,
    IReadOnlyList<bool> KeptOurs, IReadOnlyList<bool> KeptTheirs, IReadOnlyList<(string Text, ConflictSide Side)> Result);

/// <summary>Commit of the blame column: texts (user-facing: localized by the caller), lane color, age from 0 (oldest) to 1.</summary>
public sealed record EditorBlameCommit(string Summary, string Date, string Initials, int ColorIndex, double Age, string ToolTip);

/// <summary>Texts of the staging controls drawn in a unified diff (user-facing: localized by the caller).</summary>
public sealed record EditorStaging(string ApplyLabel, string? DiscardLabel, string Hint, string ToggleTip);
