using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>One application window: multi-repository tabs. Several can be open at once (<see cref="ShellWorkspace"/>).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IFolderPicker _folderPicker;
    private readonly IUserNotifier _notifier;
    private readonly RecentRepositories _recent;
    private readonly TimeProvider _time;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly IRepositoryWatcherFactory? _watchers;
    private readonly IConfirmationService? _confirmations;
    private readonly IAuthenticationPrompt? _authentication;
    private readonly IInputDialogService? _inputs;
    private readonly ICloneDialog? _cloneDialog;
    private readonly Preferences? _preferences;
    private readonly ISshKeyService? _sshKeys;
    private readonly IGpgKeyService? _gpgKeys;
    private readonly IFileDialogs? _fileDialogs;
    private readonly CommitMessageGenerator? _commitMessages;
    private readonly TerminalService? _terminal;
    private readonly HostAccounts? _accounts;
    private readonly GitProfiles? _profiles;
    private readonly IUrlLauncher? _urlLauncher;
    private readonly ShellWorkspace? _workspace;
    private readonly JsonHiddenRefsStore? _hiddenRefs;
    private bool _autoFetchStarted;

    public ShellViewModel(
        IGitService git,
        IFolderPicker folderPicker,
        IUserNotifier notifier,
        RecentRepositories recent,
        TimeProvider time,
        ILogger<ShellViewModel> logger,
        IRepositoryWatcherFactory? watchers = null,
        IConfirmationService? confirmations = null,
        IAuthenticationPrompt? authentication = null,
        IInputDialogService? inputs = null,
        ICloneDialog? cloneDialog = null,
        Preferences? preferences = null,
        ShellWorkspace? workspace = null,
        bool withWelcomeTab = true,
        ISshKeyService? sshKeys = null,
        HostAccounts? accounts = null,
        IUrlLauncher? urlLauncher = null,
        GitProfiles? profiles = null,
        IGpgKeyService? gpgKeys = null,
        CommitMessageGenerator? commitMessages = null,
        TerminalService? terminal = null,
        IFileDialogs? fileDialogs = null,
        JsonHiddenRefsStore? hiddenRefs = null)
    {
        _hiddenRefs = hiddenRefs;
        _fileDialogs = fileDialogs;
        _gpgKeys = gpgKeys;
        _commitMessages = commitMessages;
        _terminal = terminal;
        _git = git;
        _folderPicker = folderPicker;
        _notifier = notifier;
        _recent = recent;
        _time = time;
        _logger = logger;
        _watchers = watchers;
        _confirmations = confirmations;
        _authentication = authentication;
        _inputs = inputs;
        _cloneDialog = cloneDialog;
        _preferences = preferences;
        _recentCommands = [.. preferences?.Current.RecentPaletteCommands ?? []];
        _sshKeys = sshKeys;
        _accounts = accounts;
        _urlLauncher = urlLauncher;
        _profiles = profiles;
        _workspace = workspace;
        StatusBar = new StatusBarViewModel(time, preferences, profiles, workspace?.Updates);
        workspace?.Add(this);
        if (workspace is not null) workspace.Projects.Changed += OnProjectsChanged;
        // A window created for a torn-out tab starts empty: the tab is moved into it right away.
        if (withWelcomeTab) NewTab();
    }

    // Window-bound services, reached by the tabs through their relay (TabServices).
    internal IUserNotifier Notifier => _notifier;
    internal IFolderPicker FolderPicker => _folderPicker;
    internal IConfirmationService? Confirmations => _confirmations;
    internal IAuthenticationPrompt? Authentication => _authentication;
    internal IInputDialogService? Inputs => _inputs;
    internal IFileDialogs? FileDialogs => _fileDialogs;

    /// <summary>The window should come to the front (one of its tabs was asked for from another window).</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>The window has no tab left (its last one was moved to another window): it should close.</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>Status bar of the window, following the selected tab.</summary>
    public StatusBarViewModel StatusBar { get; }

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [RelayCommand]
    private void NewTab()
    {
        var existing = Tabs.OfType<WelcomeTabViewModel>().FirstOrDefault();
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var services = new TabServices(this);
        var welcome = new WelcomeTabViewModel(_git, services, _recent, _time, services.OpenRepositoryAsync, _cloneDialog is null ? null : services.CloneAsync,
            _accounts, _preferences is null || _accounts is null ? null : OpenIntegrations, _profiles, _preferences is null || _profiles is null ? null : OpenProfiles,
            _workspace?.SwitchProfile is null ? null : SwitchProfileAsync)
        {
            Services = services,
        };
        Tabs.Add(welcome);
        SelectedTab = welcome;
    }

    /// <summary>"Ouvrir un dépôt…" (Ctrl+O): folder picker of the welcome tab, which is opened or activated first.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        NewTab();
        if (SelectedTab is WelcomeTabViewModel welcome) await welcome.OpenCommand.ExecuteAsync(null);
    }

    /// <summary>"Initialiser un dépôt…" (Ctrl+Alt+N), from the welcome tab like <see cref="OpenAsync"/>.</summary>
    [RelayCommand]
    private async Task InitAsync()
    {
        NewTab();
        if (SelectedTab is WelcomeTabViewModel welcome) await welcome.InitCommand.ExecuteAsync(null);
    }

    /// <summary>"Préférences" (Ctrl+,): a single tab, activated when already open.</summary>
    [RelayCommand(CanExecute = nameof(HasPreferences))]
    private void OpenPreferences()
    {
        if (_preferences is null) return;
        // A single preferences tab for the whole application, possibly in another window.
        if (FindTab<PreferencesTabViewModel>(_ => true) is { } open)
        {
            open.Shell.Activate(open.Tab);
            return;
        }
        var services = new TabServices(this);
        var tab = new PreferencesTabViewModel(_preferences, _sshKeys, services, _accounts, _profiles, services,
            _workspace?.SwitchProfile is null ? null : SwitchProfileAsync, ShortcutMap.Current, _workspace?.Projects, _urlLauncher, _gpgKeys, _commitMessages, _terminal, _git, services)
        {
            Services = services,
            RecentRepositories = _recent,
            AssignRepositoryIcon = SetRepositoryIcon,
        };
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    // "Se connecter" of the welcome screen: the preferences tab, on its integrations section.
    private void OpenIntegrations()
    {
        OpenPreferences();
        if (FindTab<PreferencesTabViewModel>(_ => true) is { } open)
            open.Tab.SelectedSection = open.Tab.Sections.First(s => s.Section == PreferencesSection.Integrations);
    }

    // "Gérer les profils" of the welcome screen and of the status bar profile menu: the preferences tab, on its Git profiles section.
    public void OpenProfiles()
    {
        OpenPreferences();
        if (FindTab<PreferencesTabViewModel>(_ => true) is { } open)
            open.Tab.SelectedSection = open.Tab.Sections.First(s => s.Section == PreferencesSection.Profiles);
    }

    private bool HasPreferences => _preferences is not null;

    /// <summary>Address of the online documentation.</summary>
    public const string OnlineHelpUrl = "https://vigo-doc.vercel.app";

    /// <summary>"Aide en ligne": the documentation site, in the browser.</summary>
    [RelayCommand(CanExecute = nameof(HasUrlLauncher))]
    private Task OpenOnlineHelpAsync() => _urlLauncher?.OpenAsync(OnlineHelpUrl) ?? Task.CompletedTask;

    private bool HasUrlLauncher => _urlLauncher is not null;

    private IEnumerable<PaletteEntry> UpdateEntries()
    {
        if (_workspace?.Updates is not { CanUpdate: true } updates) yield break;
        if (updates.ReadyVersion is { } ready)
            yield return new PaletteEntry(PaletteGroup.Commands, "arrow_sync", Loc.Format("Shell.Palette.RestartToUpdate", ready), () =>
            {
                updates.RestartToUpdate();
                return Task.CompletedTask;
            });
        else
            yield return new PaletteEntry(PaletteGroup.Commands, "arrow_sync", Loc.Get("Shell.Palette.CheckUpdates"), updates.CheckNowAsync);
    }

    /// <summary>
    /// Theme shown by the window (set by the view: with the "system" theme, Windows decides). Lets "Basculer thème
    /// clair / sombre" pick the opposite one.
    /// </summary>
    public bool IsDarkThemeShown { get; set; }

    /// <summary>Palette: switches to the light theme when the dark one is shown, and conversely.</summary>
    [RelayCommand(CanExecute = nameof(HasPreferences))]
    private Task ToggleThemeAsync() =>
        _preferences?.UpdateAsync(p => p with { Theme = IsDarkThemeShown ? AppTheme.Light : AppTheme.Dark }) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasPreferences))]
    private Task ToggleDensityAsync() =>
        _preferences?.UpdateAsync(p => p with { Density = p.Density == Density.Compact ? Density.Normal : Density.Compact }) ?? Task.CompletedTask;

    private readonly List<string> _recentCommands;

    /// <summary>
    /// Command palette (Ctrl+P) over the application commands and, in a repository tab, its commands, branches,
    /// modified files and loaded commits. <paramref name="query"/> may start with a scope prefix (&gt;, @, #).
    /// </summary>
    public CommandPaletteViewModel CreatePalette(string query = "") => new(new ShellPaletteSource(this), query, _recentCommands, SaveRecentCommands);

    private void SaveRecentCommands()
    {
        var recent = _recentCommands.ToImmutableArray();
        if (_preferences is { } preferences) _ = preferences.UpdateAsync(p => p with { RecentPaletteCommands = recent });
    }

    /// <summary>"Nouvelle fenêtre" (Ctrl+Maj+N): an application window with a welcome tab.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenWindows))]
    private void NewWindow() => _workspace?.OpenWindow?.Invoke(true);

    /// <summary>"Déplacer vers une nouvelle fenêtre": the tab (the selected one by default) alone in a new window.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenWindows))]
    private void MoveTabToNewWindow(TabViewModel? tab)
    {
        tab ??= SelectedTab;
        // The only tab of a window is already alone in it.
        if (tab is null || Tabs.Count < 2 || !Tabs.Contains(tab) || _workspace?.OpenWindow?.Invoke(false) is not { } target) return;
        MoveTab(tab, target);
    }

    private bool CanOpenWindows => _workspace?.OpenWindow is not null;

    /// <summary>"Quitter VIGO" (Ctrl+Q): closes every window, the next launch reopening all of them.</summary>
    [RelayCommand(CanExecute = nameof(CanQuit))]
    private void Quit() => _workspace?.Quit?.Invoke();

    private bool CanQuit => _workspace?.Quit is not null;

    /// <summary>Profiles to choose from, with the active one flagged; empty without profiles.</summary>
    public IReadOnlyList<(string Id, string Label, bool IsActive, string? Icon)> ProfileChoices =>
        _profiles is null ? [] : [.. _profiles.Profiles.Select(p => (p.Id, p.Label, p.Id == _profiles.Active?.Id, p.Icon))];

    private bool CanSwitchProfile => _workspace?.SwitchProfile is not null && _profiles is { Profiles.Count: > 1 };

    /// <summary>
    /// Makes another profile the active one, from anywhere (status bar, palette, preferences): the repository tabs of
    /// every window are replaced by those the profile had open, in their order.
    /// </summary>
    [RelayCommand]
    public Task SwitchProfileAsync(string? id) =>
        string.IsNullOrEmpty(id) || _workspace?.SwitchProfile is not { } switchProfile ? Task.CompletedTask : switchProfile(id);

    /// <summary>
    /// Closes the repository tabs of this window (welcome and preferences tabs stay), ahead of a profile switch. A
    /// window left without tab gets a welcome tab.
    /// </summary>
    public void CloseRepositoryTabs()
    {
        _unrestored.Clear();
        foreach (var tab in Tabs.OfType<RepositoryTabViewModel>().ToList())
        {
            if (ReferenceEquals(SelectedTab, tab)) SelectedTab = null;
            Tabs.Remove(tab);
            tab.Dispose();
        }
        if (Tabs.Count == 0)
            NewTab();
        else if (SelectedTab is null)
            SelectedTab = Tabs[0];
    }

    /// <summary>Tabs that are not repositories (welcome, preferences) of this window.</summary>
    public IReadOnlyList<TabViewModel> NonRepositoryTabs => [.. Tabs.Where(t => t is not RepositoryTabViewModel)];

    public void Notify(NotificationSeverity severity, string title, string? message = null) => _notifier.Notify(severity, title, message);

    /// <summary>
    /// Moves <paramref name="tab"/> to the window of <paramref name="target"/>, at <paramref name="index"/> (the end
    /// when out of range), where it becomes the selected tab. The tab keeps its state (graph, selection, diff, commit
    /// form, operations in progress); its toasts and dialogs now show in the target window. This window asks to
    /// close when it has no tab left.
    /// </summary>
    public void MoveTab(TabViewModel tab, ShellViewModel target, int index = -1)
    {
        if (ReferenceEquals(target, this)) return;
        var from = Tabs.IndexOf(tab);
        if (from < 0) return;

        var wasSelected = ReferenceEquals(SelectedTab, tab);
        Tabs.RemoveAt(from);
        tab.Services?.Host = target;
        target.Tabs.Insert(index >= 0 && index <= target.Tabs.Count ? index : target.Tabs.Count, tab);
        target.GroupProjectTabs();
        target.SelectedTab = tab;
        LogTabMoved(_logger, tab.Title);

        if (Tabs.Count == 0)
        {
            SelectedTab = null;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (wasSelected || SelectedTab is null)
        {
            SelectedTab = Tabs[Math.Clamp(from, 0, Tabs.Count - 1)];
        }
    }

    /// <summary>Selects <paramref name="tab"/> and brings this window to the front.</summary>
    internal void Activate(TabViewModel tab)
    {
        SelectedTab = tab;
        ActivationRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>First tab of type <typeparamref name="T"/> matching <paramref name="match"/>: in this window first, then in the others.</summary>
    private (ShellViewModel Shell, T Tab)? FindTab<T>(Func<T, bool> match)
        where T : TabViewModel
    {
        IEnumerable<ShellViewModel> shells = _workspace is null ? [this] : [this, .. _workspace.Shells.Where(s => s != this)];
        foreach (var shell in shells)
        {
            if (shell.Tabs.OfType<T>().FirstOrDefault(match) is { } tab) return (shell, tab);
        }
        return null;
    }

    /// <summary>
    /// The window is closed while the application keeps running: its tabs are released (watchers, loads in progress)
    /// and it leaves the workspace.
    /// </summary>
    public void CloseWindow()
    {
        _workspace?.Remove(this);
        if (_workspace is not null) _workspace.Projects.Changed -= OnProjectsChanged;
        SelectedTab = null;
        StatusBar.Detach();
        foreach (var tab in Tabs.ToList())
        {
            Tabs.Remove(tab);
            (tab as IDisposable)?.Dispose();
        }
    }

    [RelayCommand]
    private void CloseTab(TabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        (tab as IDisposable)?.Dispose();
        if (Tabs.Count == 0)
        {
            NewTab();
            return;
        }
        if (SelectedTab == tab || SelectedTab is null)
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    // ---- Projets d'onglets (J7.6, ADR-058) ----------------------------------------------------------------------------

    /// <summary>Projects of the active profile, for the tab menu and the palette.</summary>
    public IReadOnlyList<TabProject> Projects => _workspace?.Projects.Projects ?? [];

    /// <summary>The window can group its repository tabs into projects.</summary>
    public bool HasProjects => _workspace is not null;

    private static string PathOf(RepositoryTabViewModel tab) => tab.Info.WorkingDirectory ?? tab.Info.GitDirectory;

    /// <summary>Project of a tab (null for the welcome and preferences tabs, or a repository outside any project).</summary>
    public TabProject? ProjectOf(TabViewModel? tab) =>
        tab is RepositoryTabViewModel repository ? _workspace?.Projects.ProjectOf(PathOf(repository)) : null;

    /// <summary>Puts the repository tab (the selected one by default) in a project; its tab joins the project's tabs.</summary>
    public void AssignToProject(TabViewModel? tab, string? projectId)
    {
        tab ??= SelectedTab;
        if (tab is RepositoryTabViewModel repository && !string.IsNullOrEmpty(projectId))
            _workspace?.Projects.Assign(PathOf(repository), projectId);
    }

    /// <summary>Takes the repository tab (the selected one by default) out of its project.</summary>
    public void RemoveFromProject(TabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is RepositoryTabViewModel repository) _workspace?.Projects.Unassign(PathOf(repository));
    }

    /// <summary>"Nouveau projet…": asks a name, creates the project (next free color) and puts the tab in it.</summary>
    public async Task NewProjectAsync(TabViewModel? tab, CancellationToken cancellationToken = default)
    {
        tab ??= SelectedTab;
        if (tab is not RepositoryTabViewModel repository || _workspace is not { Projects: var projects } || _inputs is null) return;
        var dialog = new InputDialogViewModel(Loc.Get("Shell.NewProject.Title"), Loc.Get("Shell.NewProject.Confirm"), Loc.Get("Shell.NewProject.NameHeader"),
            name => name.Length == 0 ? string.Empty : projects.IsNameTaken(name) ? Loc.Get("Shell.NewProject.NameTaken") : null)
        {
            Description = Loc.Format("Shell.NewProject.Description", repository.Title),
            NamePlaceholder = Loc.Get("Shell.NewProject.Placeholder"),
        };
        if (!await _inputs.ShowAsync(dialog, cancellationToken)) return;
        var project = projects.Create(dialog.Value);
        projects.Assign(PathOf(repository), project.Id);
    }

    private IEnumerable<PaletteEntry> ProjectEntries()
    {
        if (!HasProjects || SelectedTab is not RepositoryTabViewModel tab) yield break;
        var current = ProjectOf(tab);
        foreach (var project in Projects.Where(p => p.Id != current?.Id))
        {
            var id = project.Id;
            yield return new PaletteEntry(PaletteGroup.Commands, "folder", Loc.Format("Shell.Palette.AssignProject", project.Name), () =>
            {
                AssignToProject(tab, id);
                return Task.CompletedTask;
            });
        }

        if (_inputs is not null)
            yield return new PaletteEntry(PaletteGroup.Commands, "folder_add", Loc.Get("Shell.Palette.NewProject"), () => NewProjectAsync(tab));
        if (current is not null)
            yield return new PaletteEntry(PaletteGroup.Commands, "folder", Loc.Format("Shell.Palette.RemoveProject", current.Name), () =>
            {
                RemoveFromProject(tab);
                return Task.CompletedTask;
            });
    }

    private void OnProjectsChanged(object? sender, EventArgs e)
    {
        foreach (var tab in Tabs.OfType<RepositoryTabViewModel>()) ApplyProject(tab);
        GroupProjectTabs();
    }

    /// <summary>Icon chosen for the repository of <paramref name="tab"/>; null restores the default icon.</summary>
    public void SetTabIcon(RepositoryTabViewModel tab, string? icon) => SetRepositoryIcon(PathOf(tab), icon);

    /// <summary>Icon of the repository at <paramref name="path"/>: its open tabs of this window change at once, and the choice is saved.</summary>
    public void SetRepositoryIcon(string path, string? icon)
    {
        foreach (var tab in Tabs.OfType<RepositoryTabViewModel>().Where(t => string.Equals(PathOf(t), path, StringComparison.OrdinalIgnoreCase)))
            tab.CustomIcon = icon;
        _preferences?.UpdateAsync(p => p with
        {
            RepositoryIcons = icon is null
                ? p.RepositoryIcons.RemoveRange(p.RepositoryIcons.Keys.Where(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase)))
                : p.RepositoryIcons.SetItem(path, icon),
        });
    }

    private void ApplyIcon(RepositoryTabViewModel tab)
    {
        var path = PathOf(tab);
        tab.CustomIcon = _preferences?.Current.RepositoryIcons
            .FirstOrDefault(i => string.Equals(i.Key, path, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } icon ? icon : null;
    }

    private void ApplyProject(RepositoryTabViewModel tab)
    {
        var project = _workspace?.Projects.ProjectOf(PathOf(tab));
        tab.ProjectColorIndex = project?.ColorIndex ?? -1;
        tab.ProjectColor = project?.Color;
        tab.ProjectName = project?.Name;
    }

    /// <summary>
    /// Makes the tabs of each project contiguous: the tabs of a project gather where its first tab is, in their order;
    /// the other tabs keep their place.
    /// </summary>
    private void GroupProjectTabs()
    {
        if (_workspace is not { Projects: var projects } || Tabs.Count < 2) return;
        var ids = Tabs.ToDictionary(t => t, t => t is RepositoryTabViewModel r ? projects.ProjectOf(PathOf(r))?.Id : null);
        var order = new List<TabViewModel>(Tabs.Count);
        foreach (var tab in Tabs)
        {
            if (order.Contains(tab)) continue;
            order.Add(tab);
            if (ids[tab] is { } id) order.AddRange(Tabs.Where(t => !ReferenceEquals(t, tab) && ids[t] == id));
        }

        for (var i = 0; i < order.Count; i++)
        {
            var current = Tabs.IndexOf(order[i]);
            if (current != i) Tabs.Move(current, i);
        }
    }

    /// <summary>"Cloner un dépôt" dialog (Start screen, Ctrl+Maj+O); the cloned repository opens in a tab.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync(CancellationToken cancellationToken)
    {
        if (_cloneDialog is null) return;
        using var clone = new CloneViewModel(_git, _folderPicker, _authentication, _preferences, _accounts, _time);
        if (await clone.RunAsync(_cloneDialog, cancellationToken) is { } cloned)
            await OpenRepositoryAsync(cloned.WorkingDirectory ?? cloned.GitDirectory, cancellationToken);
        else if (clone.OpenIntegrationsRequested)
            OpenIntegrations();
    }

    private bool CanClone => _cloneDialog is not null;

    /// <summary>The clone dialog exists: the "Dépôt" selector offers "Cloner…".</summary>
    public bool HasClone => CanClone;

    private RepositoryPickerViewModel? _repositoryPicker;

    /// <summary>List behind the "Dépôt" selector of the toolbar: the tabs of this window, then the recent repositories.</summary>
    public RepositoryPickerViewModel RepositoryPicker => _repositoryPicker ??= new RepositoryPickerViewModel(this, _recent);

    /// <summary>
    /// Automatic fetch of the visible repository tab, every <see cref="UserPreferences.AutoFetchIntervalMinutes"/>
    /// (checked every 10 s, and as soon as a tab is shown). Call once from the UI thread, after the session is
    /// restored; runs until <paramref name="cancellationToken"/> is canceled (window closed).
    /// </summary>
    public async Task RunAutoFetchAsync(CancellationToken cancellationToken)
    {
        _autoFetchStarted = true;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), _time);
        try
        {
            do
            {
                AutoFetchIfDue();
                // "fetch il y a 3 min" moves on.
                StatusBar.Refresh();
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
    }

    /// <summary>Starts an automatic fetch of the selected repository tab when one is due.</summary>
    internal void AutoFetchIfDue()
    {
        if (!_autoFetchStarted || _preferences?.Current.AutoFetchInterval is not { } interval) return;
        if (SelectedTab is RepositoryTabViewModel { Remote: var remote } && remote.IsAutoFetchDue(interval))
            _ = remote.AutoFetchAsync();
    }

    // The repository tab shown last: the "Dépôt en cours" preferences are about it (J7.29).
    private RepositoryTabViewModel? _lastRepository;

    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        if (value is RepositoryTabViewModel shown) _lastRepository = shown;
        else if (value is PreferencesTabViewModel preferences)
        {
            var repository = _lastRepository is { } last && Tabs.Contains(last) ? last : null;
            preferences.SetRepository(repository is null ? null : repository.Info.WorkingDirectory ?? repository.Info.GitDirectory, repository?.Title, repository?.GitFlow);
        }
        StatusBar.Attach(value);
        AutoFetchIfDue();
    }

    /// <summary>Opens a repository in a tab (or activates the existing tab).</summary>
    public Task OpenRepositoryAsync(string path, CancellationToken cancellationToken = default) =>
        OpenRepositoryAsync(path, restoring: false, cancellationToken);

    private readonly List<string> _unrestored = [];

    /// <summary>Repository tabs of the window, to reopen them at the next launch.</summary>
    public SessionWindow CaptureSession()
    {
        var repositories = Tabs.OfType<RepositoryTabViewModel>().ToList();
        List<string> paths = [.. repositories.Select(t => t.Info.WorkingDirectory ?? t.Info.GitDirectory)];
        // Repositories that could not be opened at startup (network share, VPN, USB key not ready) stay in the session.
        // After the open tabs, so that SelectedIndex still points at the right one.
        paths.AddRange(_unrestored.Where(u => !paths.Contains(u, StringComparer.OrdinalIgnoreCase)));
        return new SessionWindow(
            paths,
            SelectedTab is RepositoryTabViewModel selected ? repositories.IndexOf(selected) : -1,
            Unrestored: [.. _unrestored]);
    }

    /// <summary>
    /// Reopens the repository tabs of the previous session, in order, then selects the tab that was selected (or the
    /// welcome tab). Repositories that cannot be opened any more (moved, deleted) are skipped, reported in one toast the first time and forgotten the second (J9.44).
    /// The recent list is left as it is: reopening is not a new use.
    /// </summary>
    public async Task RestoreSessionAsync(SessionWindow session, CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();
        var opened = new List<RepositoryTabViewModel?>();
        foreach (var path in session.Repositories)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var tab = await OpenRepositoryAsync(path, restoring: true, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (tab is null)
            {
                // Missing at the previous launch too: forgotten, without a warning (J9.44).
                if (session.Unrestored?.Contains(path, StringComparer.OrdinalIgnoreCase) == true)
                {
                    LogSessionRepositoryForgotten(_logger, path);
                }
                else
                {
                    missing.Add(path);
                    _unrestored.Add(path);
                }
            }
            opened.Add(tab);
        }

        if (session.SelectedIndex >= 0 && session.SelectedIndex < opened.Count && opened[session.SelectedIndex] is { } selected
            && Tabs.Contains(selected))
            SelectedTab = selected;
        else if (session.SelectedIndex < 0 && opened.Any(t => t is not null))
            NewTab();

        if (missing.Count > 0)
        {
            LogSessionRepositoriesMissing(_logger, missing.Count, string.Join(", ", missing));
            _notifier.Notify(NotificationSeverity.Warning,
                missing.Count == 1 ? Loc.Get("Shell.SessionMissing.One") : Loc.Format("Shell.SessionMissing.Many", missing.Count),
                string.Join(Environment.NewLine, missing));
        }
    }

    private async Task<RepositoryTabViewModel?> OpenRepositoryAsync(string path, bool restoring, CancellationToken cancellationToken)
    {
        var result = await _git.OpenAsync(path, cancellationToken);
        switch (result)
        {
            case Success<RepositoryInfo> s:
                // The window closed while git was opening it: no tab would ever be released.
                if (restoring && cancellationToken.IsCancellationRequested) return null;
                // A repository is open once in the whole application: its tab is activated, possibly in another window.
                if (FindTab<RepositoryTabViewModel>(t => string.Equals(t.Info.GitDirectory, s.Value.GitDirectory, StringComparison.OrdinalIgnoreCase)) is { } open)
                {
                    if (!restoring) open.Shell.Activate(open.Tab);
                    return open.Tab;
                }
                var services = new TabServices(this);
                var tab = new RepositoryTabViewModel(s.Value, _git, _time, _logger, _watchers, services,
                    _confirmations is null ? null : services, _authentication is null ? null : services, _inputs is null ? null : services, _preferences,
                    _accounts, _urlLauncher, path => services.OpenRepositoryAsync(path, CancellationToken.None), _profiles, _commitMessages, _terminal,
                    _fileDialogs is null ? null : services, _hiddenRefs)
                {
                    Services = services,
                };
                // The welcome tab is replaced by the opened repository, as in the mockups.
                var welcomeIndex = SelectedTab is WelcomeTabViewModel w ? Tabs.IndexOf(w) : -1;
                if (welcomeIndex >= 0)
                {
                    var welcome = (WelcomeTabViewModel)Tabs[welcomeIndex];
                    Tabs[welcomeIndex] = tab;
                    welcome.Dispose();
                }
                else
                {
                    Tabs.Add(tab);
                }
                // Restoring: tabs are not shown one after the other (each would load its graph), only the welcome tab
                // being replaced must give way.
                ApplyProject(tab);
                ApplyIcon(tab);
                GroupProjectTabs();
                if (!restoring || welcomeIndex >= 0) SelectedTab = tab;
                LogRepositoryOpened(_logger, s.Value.GitDirectory);
                if (!restoring) await _recent.RecordOpenedAsync(s.Value, cancellationToken);
                return tab;
            case Failure f:
                if (!restoring) _notifier.Notify(NotificationSeverity.Error, Loc.Get("Shell.OpenFailed"), f.Message);
                return null;
            case Canceled:
                return null;
            case Conflict or AuthenticationFailed:
                if (!restoring) _notifier.Notify(NotificationSeverity.Error, Loc.Get("Shell.OpenFailed"), path);
                return null;
        }
        return null;
    }

    private sealed class ShellPaletteSource : IPaletteSource
    {
        private readonly RepositoryTabViewModel? _repository;

        public ShellPaletteSource(ShellViewModel shell)
        {
            _repository = shell.SelectedTab as RepositoryTabViewModel;
            var repository = _repository?.GetPaletteEntries() ?? [];
            // Repository commands first (most used), then the application ones.
            Entries =
            [
                .. repository.Where(e => e.Group == PaletteGroup.Commands),
                Command("add", Loc.Get("Shell.Palette.NewTab"), shell.NewTabCommand, Shortcut(ShortcutIds.NewTab)),
                Command("folder_open", Loc.Get("Shell.Palette.Open"), shell.OpenCommand, Shortcut(ShortcutIds.OpenRepository)),
                .. shell.CanClone ? [Command("cloud_arrow_down", Loc.Get("Shell.Palette.Clone"), shell.CloneCommand, Shortcut(ShortcutIds.CloneRepository))] : Array.Empty<PaletteEntry>(),
                Command("folder_add", Loc.Get("Shell.Palette.Init"), shell.InitCommand, Shortcut(ShortcutIds.InitRepository)),
                .. shell.SelectedTab is not null ? [Command("dismiss", Loc.Get("Shell.Palette.CloseTab"), shell.CloseTabCommand, Shortcut(ShortcutIds.CloseTab))] : Array.Empty<PaletteEntry>(),
                .. shell.ProjectEntries(),
                .. shell.CanOpenWindows ? [Command("window_new", Loc.Get("Shell.Palette.NewWindow"), shell.NewWindowCommand, Shortcut(ShortcutIds.NewWindow))] : Array.Empty<PaletteEntry>(),
                .. shell.CanOpenWindows && shell.Tabs.Count > 1
                    ? [Command("window_arrow_up", Loc.Get("Shell.Palette.MoveTab"), shell.MoveTabToNewWindowCommand)]
                    : Array.Empty<PaletteEntry>(),
                .. shell.HasPreferences
                    ? [
                        Command("dark_theme", Loc.Get("Shell.Palette.ToggleTheme"), shell.ToggleThemeCommand),
                        Command("text_align_justify", Loc.Get("Shell.Palette.ToggleDensity"), shell.ToggleDensityCommand),
                        Command("settings", Loc.Get("Shell.Palette.Preferences"), shell.OpenPreferencesCommand, Shortcut(ShortcutIds.Preferences)),
                    ]
                    : Array.Empty<PaletteEntry>(),
                .. shell.HasUrlLauncher
                    ? [Command("question_circle", Loc.Get("Shell.Palette.OnlineHelp"), shell.OpenOnlineHelpCommand, Shortcut(ShortcutIds.OnlineHelp))]
                    : Array.Empty<PaletteEntry>(),
                .. shell.CanSwitchProfile
                    ? shell.ProfileChoices.Where(p => !p.IsActive).Select(p =>
                        new PaletteEntry(PaletteGroup.Commands, p.Icon ?? "person", Loc.Format("Shell.Palette.SwitchProfile", p.Label), () => shell.SwitchProfileAsync(p.Id)))
                    : Array.Empty<PaletteEntry>(),
                .. shell.UpdateEntries(),
                .. shell.CanQuit ? [Command("arrow_exit", Loc.Get("Shell.Palette.Quit"), shell.QuitCommand, Shortcut(ShortcutIds.Quit))] : Array.Empty<PaletteEntry>(),
                .. repository.Where(e => e.Group != PaletteGroup.Commands),
            ];
        }

        public IReadOnlyList<PaletteEntry> Entries { get; }

        public IEnumerable<PaletteEntry> FindCommits(Func<string, bool> matches, int limit) =>
            _repository?.FindCommits(matches, limit) ?? [];

        private static string? Shortcut(string id) => ShortcutMap.Current.Display(id);

        private static PaletteEntry Command(string icon, string label, System.Windows.Input.ICommand command, string? shortcut = null) =>
            new(PaletteGroup.Commands, icon, label, () =>
            {
                if (command is IAsyncRelayCommand async) return async.ExecuteAsync(null);
                command.Execute(null);
                return Task.CompletedTask;
            }, Shortcut: shortcut);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} repositories of the previous session could not be reopened: {Paths}")]
    private static partial void LogSessionRepositoriesMissing(ILogger logger, int count, string paths);

    [LoggerMessage(Level = LogLevel.Information, Message = "Repository {Path} is still missing; removed from the session")]
    private static partial void LogSessionRepositoryForgotten(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tab {Title} moved to another window")]
    private static partial void LogTabMoved(ILogger logger, string title);

    [LoggerMessage(Level = LogLevel.Information, Message = "Repository opened: {GitDir}")]
    private static partial void LogRepositoryOpened(ILogger logger, string gitDir);
}
