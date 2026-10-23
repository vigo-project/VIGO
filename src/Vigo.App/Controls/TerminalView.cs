using System.ComponentModel;
using System.Globalization;
using LoomTerminal.Presentation.Session;
using LoomTerminal.Presentation.Theming;
using LoomTerminal.Pty;
using LoomTerminal.WinUI.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.System;
using Windows.UI;
using LoomCursor = LoomTerminal.Presentation.Session.TerminalCursorStyle;
using VigoCursor = Vigo.Core.Models.TerminalCursorStyle;

namespace Vigo.App.Controls;

/// <summary>
/// One terminal of the integrated terminal panel (J7.10, ADR-071, ADR-076): a shell behind a ConPTY pseudo console,
/// drawn by the native LoomTerminal control (Win2D, no WebView2). The package owns the VT engine, the selection, the
/// copy / paste and the links; this class starts the shell of the tab, applies the VIGO theme and preferences, relays the
/// app shortcuts and keeps the end-of-shell behavior: the terminal stays, shows the exit code, and Enter starts the
/// shell again.
/// </summary>
public sealed partial class TerminalView : UserControl, IDisposable
{
    private const int Scrollback = 10_000;

    // Nerd Fonts after Cascadia: prompts (oh-my-posh, starship) draw their icons with them, per glyph, when installed.
    private const string DefaultFonts =
        "Cascadia Mono, Cascadia Code, CaskaydiaCove NFM, CaskaydiaCove NF, MesloLGM Nerd Font Mono, MesloLGS Nerd Font Mono, " +
        "JetBrainsMono Nerd Font Mono, FiraCode Nerd Font Mono, Hack Nerd Font Mono, Consolas, Courier New";

    private readonly TerminalTabViewModel _tab;
    private readonly TerminalControl _terminal = new();
    private readonly TextBlock _failure = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
    private TerminalSession? _session;
    private bool _started;
    private bool _released;
    private bool _wantsFocus;
    private bool _exited;
    private bool _focused;

    public TerminalView(TerminalTabViewModel tab)
    {
        _tab = tab;
        IsTabStop = false;
        _failure.FontSize = Appearance.Scaled(12);
        var grid = new Grid();
        grid.Children.Add(_terminal);
        grid.Children.Add(_failure);
        Content = grid;

        _terminal.UseSystemFocusVisuals = false;
        _terminal.Exited += OnExited;
        _terminal.KeyPreview += OnKeyPreview;
        _terminal.LinkActivated += OnLinkActivated;
        _terminal.GotFocus += (_, _) => { _focused = true; UpdateDim(); };
        _terminal.LostFocus += (_, _) => { _focused = false; UpdateDim(); };
        Loaded += (_, _) =>
        {
            ApplyTheme();
            StartShell();
        };
        ActualThemeChanged += (_, _) => ApplyTheme();
        Appearance.Current.PropertyChanged += OnAppearanceChanged;
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Appearance.Terminal)) ApplyTheme();
    }

    /// <summary>A link was activated with Ctrl+click: https only (the owner opens it).</summary>
    public event EventHandler<Uri>? LinkClicked;

    /// <summary>Log of the terminals (set once at startup).</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The shell of a terminal could not start ({Shell})")]
    private static partial void LogShellFailed(ILogger logger, Exception exception, string shell);

    [LoggerMessage(Level = LogLevel.Information, Message = "Terminal shell {Shell} exited with code {ExitCode}")]
    private static partial void LogShellExited(ILogger logger, string shell, int exitCode);

    /// <summary>Gives the keyboard focus to the terminal.</summary>
    public void FocusTerminal()
    {
        _wantsFocus = true;
        _terminal.Focus(FocusState.Programmatic);
    }

    /// <summary>Ends the shell when the terminal is closed for good.</summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        Appearance.Current.PropertyChanged -= OnAppearanceChanged;
        _terminal.Session = null;
        DisposeSession();
    }

    public void Dispose() => Release();

    private void DisposeSession()
    {
        var session = _session;
        _session = null;
        // Disposing the session kills the shell: off the UI thread (it waits for the reader thread).
        if (session is not null) _ = Task.Run(session.Dispose);
    }

    private async void StartShell()
    {
        if (_released || (_started && !_exited)) return;
        _started = true;
        _exited = false;
        _failure.Visibility = Visibility.Collapsed;
        _terminal.Session = null;
        DisposeSession();
        try
        {
            // Creating the pipes, the pseudo console and the process is I/O: off the UI thread.
            var (command, directory, theme) = (_tab.CommandLine, _tab.WorkingDirectory, BuildTheme());
            var terminal = Appearance.Current.Terminal;
            var options = new TerminalSessionOptions
            {
                Scrollback = Scrollback,
                Theme = theme,
                CursorStyle = CursorOf(terminal.Cursor),
                TermProgram = "VIGO",
                WindowsPtyBuildNumber = Environment.OSVersion.Version.Build,
            };
            var session = await Task.Run(() => TerminalSession.Start(new PtyStartOptions(command) { WorkingDirectory = directory, Columns = 80, Rows = 24 }, options));
            if (_released)
            {
                _ = Task.Run(session.Dispose);
                return;
            }
            _session = session;
            _terminal.Session = session;
            if (_wantsFocus) _terminal.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or ArgumentException or InvalidOperationException)
        {
            LogShellFailed(Logger, ex, _tab.Shell.Name);
            _exited = true;
            _failure.Text = Loc.Format("Terminal.ShellFailed", _tab.Shell.Name, ex.Message);
            _failure.Visibility = Visibility.Visible;
        }
    }

    private void OnExited(object? sender, int code)
    {
        if (_released || _session is not { } session) return;
        LogShellExited(Logger, _tab.Shell.Name, code);
        _exited = true;
        session.WriteToDisplay($"\r\n\u001b[2m[{Loc.Format("Terminal.Exited", code)}]\u001b[0m\r\n");
    }

    /// <summary>
    /// App shortcuts that still work with the terminal focused: the toggle of the terminal and the Ctrl+Shift
    /// combinations (the shell keeps every other key: Ctrl+C, Ctrl+P, Ctrl+W…). Ctrl+Shift+C / V are copy and paste
    /// (handled by the control). Once the shell is gone, Enter starts it again and the other keys are ignored.
    /// </summary>
    private void OnKeyPreview(object? sender, LoomTerminal.Presentation.Input.KeyPreviewEventArgs e)
    {
        var key = e.Key;
        if (key.Type != LoomTerminal.Presentation.Input.TerminalKeyEventType.KeyDown) return;
        var modifiers = (key.Ctrl ? VirtualKeyModifiers.Control : 0) | (key.Shift ? VirtualKeyModifiers.Shift : 0) | (key.Alt ? VirtualKeyModifiers.Menu : 0);
        foreach (var definition in ShortcutCatalog.All)
        {
            if (ShortcutMap.Current.Get(definition.Id) is not { } gesture || gesture.Key != key.KeyCode) continue;
            var control = (gesture.Modifiers & GestureModifiers.Ctrl) != 0;
            var shift = (gesture.Modifiers & GestureModifiers.Shift) != 0;
            var relayed = definition.Id == ShortcutIds.Terminal || (control && shift && gesture.Key is not ('C' or 'V'));
            if (!relayed) continue;
            if (KeyboardShortcuts.TryInvoke(this, (VirtualKey)key.KeyCode, modifiers))
            {
                e.Handled = true;
                return;
            }
        }
        if (_exited && !key.Ctrl && !key.Alt)
        {
            e.Handled = true;
            if (key.Key == "Enter") StartShell();
        }
    }

    private void OnLinkActivated(object? sender, LinkActivatedEventArgs e)
    {
        // Only https is opened (and checked again by the launcher).
        e.Handled = true;
        if (e.Uri.Scheme == Uri.UriSchemeHttps) LinkClicked?.Invoke(this, e.Uri);
    }

    private void UpdateDim() => _terminal.Opacity = Appearance.Current.Terminal.DimWhenUnfocused && !_focused ? 0.6 : 1;

    private static LoomCursor CursorOf(VigoCursor style) => style switch
    {
        VigoCursor.Block => LoomCursor.Block,
        VigoCursor.Underline => LoomCursor.Underline,
        _ => LoomCursor.Bar,
    };

    /// <summary>Theme (design tokens), font and cursor of the preferences, applied live to the control.</summary>
    private void ApplyTheme()
    {
        var terminal = Appearance.Current.Terminal;
        var background = SunkenColor();
        _failure.Foreground = new SolidColorBrush(ActualTheme == ElementTheme.Light ? DesignTokens.Light.Text : DesignTokens.Dark.Text);
        ((Grid)Content).Background = new SolidColorBrush(background);
        _terminal.Theme = BuildTheme();
        _terminal.FontFamily = new FontFamily(terminal.FontFamily is { Length: > 0 } family ? $"{family.Replace(",", string.Empty, StringComparison.Ordinal)}, {DefaultFonts}" : DefaultFonts);
        _terminal.FontSize = terminal.FontSize;
        _terminal.LineHeight = terminal.LineHeight;
        _terminal.CursorStyle = CursorOf(terminal.Cursor);
        UpdateDim();
    }

    private Color SunkenColor() => ActualTheme == ElementTheme.Light ? DesignTokens.Light.Sunken : DesignTokens.Dark.Sunken;

    private TerminalTheme BuildTheme()
    {
        var dark = ActualTheme != ElementTheme.Light;
        var background = SunkenColor();
        var text = dark ? DesignTokens.Dark.Text : DesignTokens.Light.Text;
        var accent = dark ? DesignTokens.Dark.Accent : DesignTokens.Light.Accent;
        var ansi = dark ? DarkAnsi : LightAnsi;
        return new TerminalTheme
        {
            Background = Hex(background),
            Foreground = Hex(text),
            Cursor = Hex(text),
            CursorAccent = Hex(background),
            SelectionBackground = Hex(Color.FromArgb(0x66, accent.R, accent.G, accent.B)),
            SelectionInactiveBackground = Hex(Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
            Black = ansi[0], Red = ansi[1], Green = ansi[2], Yellow = ansi[3],
            Blue = ansi[4], Magenta = ansi[5], Cyan = ansi[6], White = ansi[7],
            BrightBlack = ansi[8], BrightRed = ansi[9], BrightGreen = ansi[10], BrightYellow = ansi[11],
            BrightBlue = ansi[12], BrightMagenta = ansi[13], BrightCyan = ansi[14], BrightWhite = ansi[15],
        };
    }

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
}
