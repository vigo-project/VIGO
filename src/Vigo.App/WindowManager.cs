using Microsoft.Extensions.Logging;
using Vigo.App.Services;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.App;

/// <summary>
/// Application windows (J4.7, ADR-033): creates each window with its own services (dialogs, toasts, folder picker,
/// shell), all on the UI thread, and saves the session when the application exits — when the last window closes, or
/// on "Quitter VIGO", which keeps every window in the session.
/// </summary>
public sealed partial class WindowManager
{
    private readonly IGitService _git;
    private readonly RecentRepositories _recent;
    private readonly TimeProvider _time;
    private readonly IRepositoryWatcherFactory _watchers;
    private readonly Preferences _preferences;
    private readonly ISshKeyService _sshKeys;
    private readonly IGpgKeyService _gpgKeys;
    private readonly CommitMessageGenerator _commitMessages;
    private readonly TerminalService _terminal;
    private readonly HostAccounts _accounts;
    private readonly GitProfiles _profiles;
    private readonly IUrlLauncher _urlLauncher;
    private readonly LayoutState _layout;
    private readonly JsonSessionStore _session;
    private readonly JsonHiddenRefsStore _hiddenRefs;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<WindowManager> _logger;
    private readonly List<MainWindow> _windows = [];
    private MainWindow? _lastActive;
    private bool _quitting;
    private bool _switching;
    private readonly AppUpdates _updates;

    public WindowManager(
        IGitService git, RecentRepositories recent, TimeProvider time, IRepositoryWatcherFactory watchers, Preferences preferences,
        LayoutState layout, JsonSessionStore session, ILoggerFactory loggers, ISshKeyService sshKeys, HostAccounts accounts, IUrlLauncher urlLauncher, GitProfiles profiles, AppUpdates updates,
        IGpgKeyService gpgKeys, CommitMessageGenerator commitMessages, TerminalService terminal, JsonHiddenRefsStore hiddenRefs)
    {
        _hiddenRefs = hiddenRefs;
        _commitMessages = commitMessages;
        _terminal = terminal;
        _updates = updates;
        _git = git;
        _recent = recent;
        _time = time;
        _watchers = watchers;
        _preferences = preferences;
        _layout = layout;
        _session = session;
        _loggers = loggers;
        _sshKeys = sshKeys;
        _gpgKeys = gpgKeys;
        _accounts = accounts;
        _profiles = profiles;
        _urlLauncher = urlLauncher;
        _logger = loggers.CreateLogger<WindowManager>();
        Workspace = new ShellWorkspace
        {
            OpenWindow = withWelcomeTab => OpenWindow(withWelcomeTab).ViewModel,
            Quit = Quit,
            SwitchProfile = SwitchProfileAsync,
            Updates = updates,
        };
        updates.Notice += OnUpdateNotice;
        Workspace.Projects.Changed += (_, _) => RequestSave();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autosave;

    /// <summary>
    /// Saves the session a few seconds after the last change of tabs or projects, so a crash or a forced shutdown loses
    /// little (it was only saved on a normal exit). Not during a restoration, which saves nothing (<see cref="RestoreAsync"/>).
    /// </summary>
    private void RequestSave()
    {
        if (_quitting || _restoring > 0 || _windows.Count == 0 || Program.Options.StartupProbeFile is not null) return;
        if (_autosave is null)
        {
            _autosave = _windows[0].DispatcherQueue.CreateTimer();
            _autosave.Interval = TimeSpan.FromSeconds(3);
            _autosave.IsRepeating = false;
            _autosave.Tick += (_, _) => SaveSessionInBackground();
        }
        _autosave.Stop();
        _autosave.Start();
    }

    private void SaveSessionInBackground()
    {
        if (_quitting || _restoring > 0 || _windows.Count == 0) return;
        var profile = _profiles.ActiveId;
        var (session, _) = CaptureSessions(_windows.ToList());
        _ = Task.Run(() => _session.SaveAsync(profile, session));
    }

    public ShellWorkspace Workspace { get; }

    /// <summary>Profile whose session is reopened at launch and saved at exit (the active one, J6.7).</summary>
    public string SessionProfileId => _profiles.ActiveId;

    /// <summary>The window opened at launch, at the saved place; it restores the session after its first frame.</summary>
    public MainWindow CreateMainWindow() =>
        Create(withWelcomeTab: true, _layout.WindowBounds, _layout.IsMaximized, isMain: true);

    /// <summary>A second launch (J9.5): brings the last active window forward and opens <paramref name="path"/> there.</summary>
    public async Task ActivateAsync(string? path)
    {
        if ((_lastActive ?? _windows.FirstOrDefault()) is not { } window) return;
        window.Activate();
        if (path is not null) await window.ViewModel.OpenRepositoryAsync(path, window.ClosingToken);
    }

    /// <summary>Tab being dragged from one of the windows (null otherwise): drops between windows stay in-process.</summary>
    public TabViewModel? DraggedTab { get; set; }

    /// <summary>
    /// A new window with the size of the last active one, shown at once: cascaded from it, or with its title bar under
    /// <paramref name="at"/> (screen point, physical pixels: a tab dropped outside the windows).
    /// </summary>
    public MainWindow OpenWindow(bool withWelcomeTab, (int X, int Y)? at = null)
    {
        var bounds = (_lastActive?.NormalBounds, at) switch
        {
            ({ } source, { } point) => source with { X = point.X - Math.Min(120, source.Width / 4), Y = point.Y - 16 },
            ({ } source, null) => source with { X = source.X + Cascade(source), Y = source.Y + Cascade(source) },
            _ => null,
        };
        var window = Create(withWelcomeTab, bounds is null ? null : FitInWorkArea(bounds), isMaximized: false, isMain: false);
        window.Activate();
        return window;
    }

    /// <summary>
    /// <paramref name="bounds"/> moved (and reduced if needed) to lie within the work area of the display it is
    /// mostly on: a window placed under the pointer or cascaded must not hang off the screen.
    /// </summary>
    private static WindowBounds FitInWorkArea(WindowBounds bounds)
    {
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromRect(
            new Windows.Graphics.RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min(bounds.Width, work.Width);
        var height = Math.Min(bounds.Height, work.Height);
        return new WindowBounds(
            Math.Clamp(bounds.X, work.X, work.X + work.Width - width),
            Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - height),
            width,
            height);
    }

    private int _restoring;

    /// <summary>Runs a session restoration, during which <see cref="Save"/> leaves the saved session untouched (UI thread only).</summary>
    internal async Task RestoreAsync(Func<Task> restore)
    {
        _restoring++;
        try
        {
            await restore();
        }
        finally
        {
            _restoring--;
        }
    }

    /// <summary>Window of a previous session, at its saved place (cascaded when it had none).</summary>
    public MainWindow RestoreWindow(SessionWindow session)
    {
        if (session.Bounds is null) return OpenWindow(withWelcomeTab: true);
        var window = Create(withWelcomeTab: true, session.Bounds, session.IsMaximized, isMain: false);
        window.Activate();
        return window;
    }

    /// <summary>Shell hosting <paramref name="tab"/>, in any window.</summary>
    public ShellViewModel? FindShell(TabViewModel tab) =>
        _windows.Select(w => w.ViewModel).FirstOrDefault(s => s.Tabs.Contains(tab));

    private static int Cascade(WindowBounds bounds) => Math.Max(24, bounds.Height / 30);

    private MainWindow Create(bool withWelcomeTab, WindowBounds? bounds, bool isMaximized, bool isMain)
    {
        var context = new WindowContext();
        var notifier = new UserNotifier(context);
        var shell = new ShellViewModel(
            _git, new FolderPickerService(context), notifier, _recent, _time, _loggers.CreateLogger<ShellViewModel>(), _watchers,
            new ConfirmationService(context), new AuthenticationPromptService(context), new InputDialogService(context),
            new CloneDialogService(context), _preferences, Workspace, withWelcomeTab, _sshKeys, _accounts, _urlLauncher, _profiles, _gpgKeys, _commitMessages, _terminal, new FileDialogService(context),
            _hiddenRefs);
        var window = new MainWindow(shell, notifier, context, _layout, _session, _preferences, this, _loggers.CreateLogger<MainWindow>(),
            new WindowPlacement(bounds, isMaximized), isMain);
        _windows.Add(window);
        shell.Tabs.CollectionChanged += (_, _) => RequestSave();
        _lastActive ??= window;
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated) _lastActive = window;
        };
        LogWindowOpened(_logger, _windows.Count);
        return window;
    }

    /// <summary>
    /// A window was closed. The last one saves the session (itself only: the windows closed before it are gone, as
    /// in a browser); the others release their tabs. After "Quitter VIGO", everything was saved already.
    /// </summary>
    internal void OnClosed(MainWindow window)
    {
        _windows.Remove(window);
        if (ReferenceEquals(_lastActive, window)) _lastActive = _windows.LastOrDefault();
        if (_quitting) return;
        if (_windows.Count == 0)
            Save(new[] { window });
        else
            window.ViewModel.CloseWindow();
    }

    /// <summary>"Quitter VIGO": saves every window in the session, then closes them all (the process exits with the last).</summary>
    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        var windows = _windows.ToList();
        Save(windows);
        foreach (var window in windows) window.Close();
    }

    /// <summary>
    /// Saves the layout and the open repositories before the process exits (bounded wait: the files are tiny). The
    /// first window with repositories gives the placement of the main window at the next launch.
    /// </summary>
    private void Save(IReadOnlyList<MainWindow> windows)
    {
        if (Program.Options.StartupProbeFile is not null) return;
        var profile = _profiles.ActiveId;
        var (session, main) = CaptureSessions(windows);
        _layout.WindowBounds = main.Bounds;
        _layout.IsMaximized = main.IsMaximized;
        // Mid-restoration the windows hold only some of the tabs: the saved session stays as it is.
        // Also what was changed a moment ago (a hidden branch, a profile, a preference) and is still being written in the background.
        var saves = Task.WhenAll(
            Task.Run(() => _layout.SaveAsync()),
            _restoring > 0 ? Task.CompletedTask : Task.Run(() => _session.SaveAsync(profile, session)),
            _preferences.WhenSavedAsync(), _profiles.WhenSavedAsync(), _hiddenRefs.WhenSavedAsync());
        if (!saves.Wait(TimeSpan.FromSeconds(2)))
            LogSaveTimedOut(_logger);
    }

    /// <summary>An update was found, downloaded or failed: shown in the window the user last used.</summary>
    private void OnUpdateNotice(object? sender, UpdateNotice notice)
    {
        var window = _lastActive ?? _windows.FirstOrDefault();
        window?.DispatcherQueue.TryEnqueue(() => window.ViewModel.Notify(notice.Severity, notice.Title, notice.Message));
    }

    /// <summary>
    /// The session of the windows (the main one first, windows without repositories left out) and the window that gives
    /// the placement of the main one.
    /// </summary>
    private (SessionSettings Session, SessionWindow Main) CaptureSessions(IReadOnlyList<MainWindow> windows)
    {
        var sessions = windows.Select(w => w.CaptureSession()).ToArray();
        var main = sessions.FirstOrDefault(s => s.Repositories.Count > 0) ?? sessions[0];
        // Explicit array: a collection expression targeting IReadOnlyList is not AOT-safe in the WinUI project (CsWinRT1032).
        var session = new SessionSettings(
            sessions.Where(s => s.Repositories.Count > 0 && !ReferenceEquals(s, main)).Prepend(main).Where(s => s.Repositories.Count > 0).ToArray(),
            Workspace.Projects.Projects.ToArray());
        return (session, main);
    }

    /// <summary>
    /// Switches the active profile (ADR-052): the repositories open in the windows are saved under the current profile
    /// and closed, then those the new profile had open are reopened, in their order and windows. The window the user is
    /// in keeps its place and its preferences tab; the other windows are closed and the saved ones reopened. Nothing is
    /// lost: working-tree changes live on disk.
    /// </summary>
    private async Task SwitchProfileAsync(string id)
    {
        if (_switching || _windows.Count == 0 || id == _profiles.ActiveId || _profiles.Profiles.All(p => p.Id != id)) return;
        _switching = true;
        try
        {
            var target = _lastActive ?? _windows[0];
            var windows = _windows.ToList();
            var previous = _profiles.ActiveId;
            var (current, _) = CaptureSessions(windows);
            await Task.Run(() => _session.SaveAsync(previous, current));

            // The preferences tab (the profile was probably chosen there) follows the user into the target window.
            foreach (var window in windows.Where(w => !ReferenceEquals(w, target)))
            {
                foreach (var tab in window.ViewModel.NonRepositoryTabs.OfType<PreferencesTabViewModel>())
                    window.ViewModel.MoveTab(tab, target.ViewModel);
                // A window left empty by the move asks to close by itself.
                if (window.ViewModel.Tabs.Count > 0) window.Close();
            }
            target.ViewModel.CloseRepositoryTabs();

            _profiles.SetActive(id);
            LogProfileSwitched(_logger, previous, id);

            var session = await Task.Run(() => _session.LoadAsync(id));
            Workspace.Projects.Load(session.Projects);
            await RestoreAsync(async () =>
            {
                if (session.Windows is [var main, ..])
                    await target.ViewModel.RestoreSessionAsync(main, target.ClosingToken);
                foreach (var saved in session.Windows.Skip(1))
                {
                    var window = RestoreWindow(saved);
                    await window.ViewModel.RestoreSessionAsync(saved, window.ClosingToken);
                }
            });
            target.Activate();
            target.ViewModel.Notify(NotificationSeverity.Info, Loc.Format("Shell.ProfileActivated", _profiles.Active?.Label));
        }
        catch (Exception ex)
        {
            LogSwitchFailed(_logger, ex, id);
        }
        finally
        {
            _switching = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Profile switched from {Previous} to {Profile}")]
    private static partial void LogProfileSwitched(ILogger logger, string previous, string profile);

    [LoggerMessage(Level = LogLevel.Error, Message = "Switching to profile {Profile} failed")]
    private static partial void LogSwitchFailed(ILogger logger, Exception exception, string profile);

    [LoggerMessage(Level = LogLevel.Information, Message = "Window opened ({Count} open)")]
    private static partial void LogWindowOpened(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saving the layout and session at exit timed out")]
    private static partial void LogSaveTimedOut(ILogger logger);
}

/// <summary>Where a window opens: normal bounds (physical pixels; null: default size, centered) and maximized state.</summary>
public sealed record WindowPlacement(WindowBounds? Bounds, bool IsMaximized);
