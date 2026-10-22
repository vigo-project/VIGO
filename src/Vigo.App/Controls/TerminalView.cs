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
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace Vigo.App.Controls;

/// <summary>
/// One terminal of the integrated terminal panel (J7.10, ADR-071): a shell behind a pseudo console (ConPTY), drawn by
/// xterm.js in a WebView2 (page <c>terminal.html</c> of the editor bundle, same virtual host and WebView2 environment as
/// <see cref="CodeEditor"/>). The shell starts when the page reports its size; its output is sent to the page in
/// batches with a limit on what the page has not drawn yet (flow control). When the shell exits the terminal stays,
/// shows the exit code, and Enter starts the shell again. A later native control (second step of ADR-071) can replace
/// the page without changing this contract.
/// </summary>
public sealed partial class TerminalView : UserControl, IDisposable
{
    private const string HostName = "vigo.editor";

    // Characters sent to the page and not yet acknowledged: the pump waits above the high mark.
    private const int HighWater = 512 * 1024;
    private const int BatchChars = 64 * 1024;

    private readonly TerminalTabViewModel _tab;
    private readonly ITerminalSessionFactory _sessions;
#if DEBUG
    private const string PageFlags = "&debug=1";
#else
    private const string PageFlags = "";
#endif
    private readonly string _pageUri = $"https://{HostName}/terminal.html?build={Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture)}{PageFlags}";
    private readonly CancellationTokenSource _closed = new();
    private WebView2? _view;
    private ITerminalSession? _session;
    private bool _starting;
    private bool _ready;
    private bool _released;
    private bool _wantsFocus;
    private bool _exited;
    private int _columns = 80;
    private int _rows = 24;
    private long _inFlight;
    private TaskCompletionSource? _drained;

    public TerminalView(TerminalTabViewModel tab, ITerminalSessionFactory sessions)
    {
        _tab = tab;
        _sessions = sessions;
        IsTabStop = false;
        Loaded += (_, _) =>
        {
            Start();
            SendTheme();
        };
        ActualThemeChanged += (_, _) => SendTheme();
        ShortcutMap.Current.Changed += OnShortcutsChanged;
        Appearance.Current.PropertyChanged += OnAppearanceChanged;
    }

    private void OnAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Appearance.Terminal)) SendTheme();
    }

    /// <summary>A link was clicked with Ctrl: https only (the owner opens it).</summary>
    public event EventHandler<Uri>? LinkClicked;

    /// <summary>The WebView2 runtime could not start (missing runtime, broken install).</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Log of the terminals (set once at startup).</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Error, Message = "The terminal page (WebView2) could not start")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal page error: {Message}")]
    private static partial void LogPageError(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The shell of a terminal could not start ({Shell})")]
    private static partial void LogShellFailed(ILogger logger, Exception exception, string shell);

    [LoggerMessage(Level = LogLevel.Information, Message = "Terminal shell {Shell} exited with code {ExitCode}")]
    private static partial void LogShellExited(ILogger logger, string shell, int exitCode);

    /// <summary>Gives the keyboard focus to the terminal.</summary>
    public void FocusTerminal()
    {
        _wantsFocus = true;
        _view?.Focus(FocusState.Programmatic);
        if (_ready) Post(w => w.WriteString("type", "focus"));
    }

    /// <summary>Ends the shell and closes the page when the terminal is closed for good.</summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        ShortcutMap.Current.Changed -= OnShortcutsChanged;
        Appearance.Current.PropertyChanged -= OnAppearanceChanged;
        _closed.Cancel();
        _drained?.TrySetResult();
        _session?.Dispose();
        _session = null;
        _view?.Close();
        _closed.Dispose();
    }

    public void Dispose() => Release();

    private async void Start()
    {
        if (_starting || _released || !IsLoaded) return;
        _starting = true;
        // No white flash while the page loads: the page background until then.
        var view = _view = new WebView2 { DefaultBackgroundColor = PageBackground() };
        Content = view;
        try
        {
            await view.EnsureCoreWebView2Async(await CodeEditor.EnvironmentAsync());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException
            or FileNotFoundException or UnauthorizedAccessException)
        {
            // Closed while starting (tab closed at once): not a failure.
            if (_released) return;
            LogStartFailed(Logger, ex);
            Failed?.Invoke(this, ex);
            return;
        }
        if (_released) return;

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
        // Only the terminal page: links and drops never navigate away.
        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate(_pageUri);
    }

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
                _columns = message.GetProperty("cols").GetInt32();
                _rows = message.GetProperty("rows").GetInt32();
                SendTheme();
                SendAppKeys();
                StartShell();
                if (_wantsFocus) Post(w => w.WriteString("type", "focus"));
                break;
            case "input":
                OnInput(message.GetProperty("data").GetString() ?? string.Empty, message.GetProperty("binary").GetBoolean());
                break;
            case "resize":
                _columns = message.GetProperty("cols").GetInt32();
                _rows = message.GetProperty("rows").GetInt32();
                _session?.Resize(_columns, _rows);
                break;
            case "ack":
                if (Interlocked.Add(ref _inFlight, -message.GetProperty("length").GetInt64()) <= HighWater) _drained?.TrySetResult();
                break;
            case "key":
                KeyboardShortcuts.TryInvoke(this, (VirtualKey)message.GetProperty("vk").GetInt32(), Modifiers(
                    message.GetProperty("ctrl").GetBoolean(), message.GetProperty("shift").GetBoolean(), message.GetProperty("alt").GetBoolean()));
                break;
            case "copy":
                Copy(message.GetProperty("text").GetString() ?? string.Empty);
                break;
            case "pasteRequest":
                _ = PasteAsync();
                break;
            case "link":
                // The page only reports the click: the scheme is checked here (and again by the launcher).
                if (Uri.TryCreate(message.GetProperty("url").GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    LinkClicked?.Invoke(this, uri);
                break;
            case "error":
                LogPageError(Logger, message.GetProperty("message").GetString() ?? string.Empty);
                break;
        }
    }

    private static VirtualKeyModifiers Modifiers(bool control, bool shift, bool alt) =>
        (control ? VirtualKeyModifiers.Control : 0) | (shift ? VirtualKeyModifiers.Shift : 0) | (alt ? VirtualKeyModifiers.Menu : 0);

    private void OnInput(string data, bool binary)
    {
        if (_exited)
        {
            // The shell is gone: Enter starts it again, anything else is ignored.
            if (data.Contains('\r', StringComparison.Ordinal)) StartShell();
            return;
        }
        // Binary input (old mouse encodings) is one byte per character.
        _session?.Write(binary ? Encoding.Latin1.GetBytes(data) : Encoding.UTF8.GetBytes(data));
    }

    private async void StartShell()
    {
        if (_released) return;
        _session?.Dispose();
        _session = null;
        _exited = false;
        try
        {
            // Creating the pipes, the pseudo console and the process is I/O: off the UI thread.
            var (command, directory, columns, rows) = (_tab.CommandLine, _tab.WorkingDirectory, _columns, _rows);
            var session = await Task.Run(() => _sessions.Start(command, directory, columns, rows));
            if (_released)
            {
                session.Dispose();
                return;
            }
            _session = session;
            _ = PumpAsync(session);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            LogShellFailed(Logger, ex, _tab.Shell.Name);
            _exited = true;
            SendData($"\u001b[31m{Loc.Format("Terminal.ShellFailed", _tab.Shell.Name, ex.Message)}\u001b[0m\r\n");
        }
    }

    /// <summary>Sends the output of the shell to the page, then reports the end of the shell.</summary>
    private async Task PumpAsync(ITerminalSession session)
    {
        // The token itself (not the source): still usable after Release disposed the source.
        var closed = _closed.Token;
        var decoder = Encoding.UTF8.GetDecoder();
        var text = new StringBuilder();
        var reader = session.Output;
        try
        {
            // Back on the UI thread after each await: the WebView2 is used from it only.
            while (await reader.WaitToReadAsync(closed))
            {
                text.Clear();
                while (text.Length < BatchChars && reader.TryRead(out var chunk))
                {
                    var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(chunk.Length));
                    try
                    {
                        text.Append(chars, 0, decoder.GetChars(chunk, 0, chunk.Length, chars, 0));
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(chars);
                    }
                }
                if (text.Length == 0 || !ReferenceEquals(session, _session)) continue;
                SendData(text.ToString());

                // The page is behind (a huge output): wait for it before reading more; the shell waits in turn.
                while (Interlocked.Read(ref _inFlight) > HighWater && !closed.IsCancellationRequested)
                {
                    _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (Interlocked.Read(ref _inFlight) <= HighWater) break;
                    await _drained.Task.WaitAsync(closed);
                }
            }
            var code = await session.Exited.WaitAsync(closed);
            if (!ReferenceEquals(session, _session)) return;
            LogShellExited(Logger, _tab.Shell.Name, code);
            _exited = true;
            SendData($"\r\n\u001b[2m[{Loc.Format("Terminal.Exited", code)}]\u001b[0m\r\n");
        }
        catch (OperationCanceledException)
        {
            // The terminal was closed.
        }
    }

    private void SendData(string text)
    {
        Interlocked.Add(ref _inFlight, text.Length);
        Post(w =>
        {
            w.WriteString("type", "data");
            w.WriteString("text", text);
        });
    }

    private static void Copy(string text)
    {
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private async Task PasteAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (!_released && text.Length > 0)
                Post(w =>
                {
                    w.WriteString("type", "paste");
                    w.WriteString("text", text);
                });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Clipboard held by another application: nothing is pasted.
        }
    }

    private void OnShortcutsChanged(object? sender, EventArgs e) => SendAppKeys();

    /// <summary>
    /// App shortcuts that still work with the terminal focused: the toggle of the terminal and the Ctrl+Shift
    /// combinations (the shell keeps every other key: Ctrl+C, Ctrl+P, Ctrl+W…). Ctrl+Shift+C / V are copy and paste.
    /// </summary>
    private void SendAppKeys()
    {
        if (!_ready || _released) return;
        Post(w =>
        {
            w.WriteString("type", "appKeys");
            w.WriteStartArray("keys");
            foreach (var definition in ShortcutCatalog.All)
            {
                if (ShortcutMap.Current.Get(definition.Id) is not { } gesture) continue;
                var control = (gesture.Modifiers & GestureModifiers.Ctrl) != 0;
                var shift = (gesture.Modifiers & GestureModifiers.Shift) != 0;
                var relayed = definition.Id == ShortcutIds.Terminal || (control && shift && gesture.Key is not ('C' or 'V'));
                if (!relayed) continue;
                w.WriteStartObject();
                w.WriteNumber("vk", gesture.Key);
                w.WriteBoolean("ctrl", control);
                w.WriteBoolean("shift", shift);
                w.WriteBoolean("alt", (gesture.Modifiers & GestureModifiers.Alt) != 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    /// <summary>Colors of the current theme (design tokens, ANSI palette of the shell programs).</summary>
    private void SendTheme()
    {
        if (!_ready || _released) return;
        var dark = ActualTheme != ElementTheme.Light;
        _view!.DefaultBackgroundColor = PageBackground();
        var background = PageBackground();
        var text = dark ? DesignTokens.Dark.Text : DesignTokens.Light.Text;
        var accent = dark ? DesignTokens.Dark.Accent : DesignTokens.Light.Accent;
        var scrollbar = dark ? DesignTokens.Dark.SubPress : DesignTokens.Light.SubPress;
        var terminal = Appearance.Current.Terminal;
        Post(w =>
        {
            w.WriteString("type", "theme");
            w.WriteNumber("fontSize", terminal.FontSize);
            if (terminal.FontFamily is { } family) w.WriteString("fontFamily", family);
            w.WriteNumber("lineHeight", terminal.LineHeight);
            w.WriteString("cursorStyle", terminal.Cursor switch { TerminalCursorStyle.Block => "block", TerminalCursorStyle.Underline => "underline", _ => "bar" });
            w.WriteBoolean("dimWhenUnfocused", terminal.DimWhenUnfocused);
            w.WriteString("scrollbar", Hex(scrollbar));
            w.WriteString("scrollbarHover", Hex(scrollbar));
            w.WriteStartObject("colors");
            w.WriteString("background", Hex(background));
            w.WriteString("foreground", Hex(text));
            w.WriteString("cursor", Hex(text));
            w.WriteString("cursorAccent", Hex(background));
            w.WriteString("selectionBackground", Hex(Color.FromArgb(0x66, accent.R, accent.G, accent.B)));
            w.WriteString("selectionInactiveBackground", Hex(Color.FromArgb(0x40, accent.R, accent.G, accent.B)));
            var palette = dark ? DarkAnsi : LightAnsi;
            for (var i = 0; i < palette.Length; i++) w.WriteString(AnsiNames[i], palette[i]);
            w.WriteEndObject();
        });
    }

    private Color PageBackground() => ActualTheme == ElementTheme.Light ? DesignTokens.Light.Sunken : DesignTokens.Dark.Sunken;

    private static readonly string[] AnsiNames =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
    ];

    // VS Code's default terminal palettes (Dark+ / Light+): well known, readable on the dark and light backgrounds.
    private static readonly string[] DarkAnsi =
    [
        "#000000", "#cd3131", "#0dbc79", "#e5e510", "#2472c8", "#bc3fbc", "#11a8cd", "#e5e5e5",
        "#666666", "#f14c4c", "#23d18b", "#f5f543", "#3b8eea", "#d670d6", "#29b8db", "#e5e5e5",
    ];

    private static readonly string[] LightAnsi =
    [
        "#000000", "#cd3131", "#00bc00", "#949800", "#0451a5", "#bc05bc", "#0598bc", "#555555",
        "#666666", "#cd3131", "#14ce14", "#b5ba00", "#0451a5", "#bc05bc", "#0598bc", "#a5a5a5",
    ];

    /// <summary>CSS color: <c>#RRGGBBAA</c>.</summary>
    private static string Hex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}");

    // Read by JSON.parse in the page, never inserted as HTML: shell output (escape sequences, accents) is sent as is.
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

    private void Post(Action<Utf8JsonWriter> write)
    {
        if (!_released && _view?.CoreWebView2 is { } core) core.PostWebMessageAsJson(Serialize(write));
    }
}
