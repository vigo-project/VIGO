using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Sections of the preferences tab (mockup VIGO Outils.dc.html#prefs, plus "Général").</summary>
public enum PreferencesSection
{
    General,
    Repository,
    Appearance,
    Shortcuts,
    Profiles,
    Projects,
    Icons,
    SshKeys,
    Integrations,
    Tools,
    About,
}

/// <summary>Entry of the navigation of the preferences tab; the sections of later milestones are shown disabled.</summary>
public sealed record PreferencesSectionItem(PreferencesSection Section, string Label, string IconName, string? AvailableAt, string? DisabledTooltip = null)
{
    public bool IsEnabled => AvailableAt is null && DisabledTooltip is null;

    /// <summary>Tooltip of a disabled section.</summary>
    public string? Tooltip => DisabledTooltip ?? (AvailableAt is null ? null : Loc.Format("Preferences.Section.AvailableAt", AvailableAt));

    public override string ToString() => Label;
}

/// <summary>Choice of the display language.</summary>
public sealed record LanguageOption(AppLanguage Language, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Choice of the automatic fetch interval.</summary>
public sealed record AutoFetchOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Préférences" tab (Ctrl+,): general settings (automatic fetch, checkout after creating a branch) and appearance
/// (theme, density). Every change is applied at once and saved; changes made elsewhere (palette) are shown here.
/// </summary>
/// <remarks>Texts: Preferences.* keys of the .resw files.</remarks>
public sealed partial class PreferencesTabViewModel : TabViewModel, IDisposable
{
    private static readonly int[] AutoFetchMinutes = [1, 2, 5, 10, 15, 30, 60];

    private readonly Preferences _preferences;
    private readonly ISshKeyService? _sshKeys;
    private readonly IUserNotifier? _notifier;
    private readonly IUrlLauncher? _launcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppLanguage _startupLanguage;
    private bool _syncing;

    public PreferencesTabViewModel(
        Preferences preferences, ISshKeyService? sshKeys = null, IUserNotifier? notifier = null, HostAccounts? accounts = null,
        GitProfiles? profiles = null, IConfirmationService? confirmations = null, Func<string, Task>? switchProfile = null,
        ShortcutMap? shortcuts = null, TabProjects? projects = null, IUrlLauncher? launcher = null,
        IGpgKeyService? gpgKeys = null, CommitMessageGenerator? commitMessages = null, TerminalService? terminal = null,
        IGitService? git = null, IFolderPicker? folderPicker = null)
    {
        _git = git;
        _folderPicker = folderPicker;
        _gpgKeys = gpgKeys;
        _commitMessages = commitMessages;
        _terminal = terminal;
        _launcher = launcher;
        _tabProjects = projects;
        _shortcuts = shortcuts;
        _switchProfile = switchProfile;
        _preferences = preferences;
        _startupLanguage = preferences.Current.Language;
        _sshKeys = sshKeys;
        _notifier = notifier;
        _accounts = accounts;
        _profiles = profiles;
        _confirmations = confirmations;
        Title = Loc.Get("Preferences.Title");
        Sections =
        [
            new(PreferencesSection.General, Loc.Get("Preferences.Section.General"), "settings", null),
            new(PreferencesSection.Repository, Loc.Get("Preferences.Section.Repository"), "folder", null, Loc.Get("Preferences.Section.NoRepository")),
            new(PreferencesSection.Appearance, Loc.Get("Preferences.Section.Appearance"), "paint_brush", null),
            new(PreferencesSection.Shortcuts, Loc.Get("Preferences.Section.Shortcuts"), "keyboard", shortcuts is null ? "J7" : null),
            new(PreferencesSection.Profiles, Loc.Get("Preferences.Section.Profiles"), "person", profiles is null ? "J6" : null),
            new(PreferencesSection.Projects, Loc.Get("Preferences.Section.Projects"), "folder", projects is null ? "J7" : null),
            new(PreferencesSection.Icons, Loc.Get("Preferences.Section.Icons"), "emoji", null),
            new(PreferencesSection.SshKeys, Loc.Get("Preferences.Section.SshKeys"), "key", sshKeys is null ? "J7" : null),
            new(PreferencesSection.Integrations, Loc.Get("Preferences.Section.Integrations"), "plug_connected", accounts is null ? "J6" : null),
            new(PreferencesSection.Tools, Loc.Get("Preferences.Section.Tools"), "document_edit", null),
            new(PreferencesSection.About, Loc.Get("Preferences.Section.About"), "info", null),
        ];
        MergeToolOptions =
        [
            new(null, Loc.Get("Preferences.MergeTool.Builtin")),
            .. MergeToolNames.Known.Select(k => new MergeToolOption(k.Tool, k.Name)),
            new(UserPreferences.GitConfiguredMergeTool, Loc.Get("Preferences.MergeTool.GitConfigured")),
        ];
        LanguageOptions =
        [
            new(AppLanguage.System, Loc.Get("Preferences.Language.System")),
            new(AppLanguage.French, "Français"),
            new(AppLanguage.English, "English"),
        ];
        SelectedSection = Sections[0];
        AutoFetchOptions =
        [
            new(0, Loc.Get("Preferences.AutoFetch.Off")),
            .. AutoFetchMinutes.Select(m => new AutoFetchOption(m, m == 1 ? Loc.Get("Preferences.AutoFetch.EveryMinute") : Loc.Format("Preferences.AutoFetch.EveryMinutes", m))),
        ];
        _startupUiFont = preferences.Current.UiFontFamily;
        _startupUiTextScale = preferences.Current.UiTextScalePercent;
        _preferences.Changed += OnPreferencesChanged;
        _ = LoadFontsAsync();
        InitializeIntegrations();
        InitializeProfiles();
        InitializeProjects();
        InitializeShortcuts();
        Sync();
    }

    public override string IconName => "settings";

    public ObservableCollection<PreferencesSectionItem> Sections { get; }

    [ObservableProperty]
    public partial PreferencesSectionItem SelectedSection { get; set; }

    public bool IsGeneral => SelectedSection.Section == PreferencesSection.General;

    public bool IsAppearance => SelectedSection.Section == PreferencesSection.Appearance;

    public bool IsRepository => SelectedSection.Section == PreferencesSection.Repository;

    /// <summary>Heading of the section: its name, or the repository's for "Dépôt en cours".</summary>
    public string SelectedSectionTitle => IsRepository && Repository is { } repository ? repository.Name : SelectedSection.Label;

    private readonly IGitService? _git;
    private readonly IFolderPicker? _folderPicker;

    /// <summary>Settings of the repository shown before the preferences; null when there was none (the section is disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSectionTitle))]
    public partial RepositorySettingsViewModel? Repository { get; private set; }

    /// <summary>Points the "Dépôt en cours" section at a repository (null: the tab shown before was not one: the section is disabled).</summary>
    public void SetRepository(string? path, string? name, GitFlowActionsViewModel? gitFlow = null)
    {
        if (Repository is { } current ? current.Path == path && current.Name == name : path is null) return;

        Repository = path is null || name is null || _git is null ? null : new RepositorySettingsViewModel(_git, path, name, _notifier, _lifetime.Token, _folderPicker);
        Repository?.AttachGitFlow(gitFlow);
        var old =Sections.First(s => s.Section == PreferencesSection.Repository);
        // The list entry is only swapped when it turns on or off (swapping the selected one would clear the selection).
        if (old.IsEnabled != (Repository is not null))
        {
            if (Repository is null && SelectedSection == old) SelectedSection = Sections[0];
            Sections[Sections.IndexOf(old)] = old with { DisabledTooltip = Repository is null ? Loc.Get("Preferences.Section.NoRepository") : null };
        }
        if (Repository is { } settings) _ = settings.LoadAsync();
    }

    public bool IsSshKeys => SelectedSection.Section == PreferencesSection.SshKeys;

    public bool IsTools => SelectedSection.Section == PreferencesSection.Tools;

    public bool IsAbout => SelectedSection.Section == PreferencesSection.About;

    /// <summary>Product name and its meaning, shown in the "À propos" section.</summary>
    public static string AboutName => "VIGO";

    public static string AboutMeaning => "Visual Git Overview";

    public static string AboutVersion => Loc.Format("Preferences.About.Version", AppVersion.Current);

    /// <summary>Release notes of the UI language (<see cref="Core.Services.Changelog"/>).</summary>
    public static IReadOnlyList<Markdown.MarkdownBlock> Changelog => field ??= Markdown.MarkdownParser.Parse(Core.Services.Changelog.Text);

    partial void OnSelectedSectionChanged(PreferencesSectionItem value)
    {
        OnPropertyChanged(nameof(IsAbout));
        OnPropertyChanged(nameof(IsGeneral));
        OnPropertyChanged(nameof(IsRepository));
        OnPropertyChanged(nameof(SelectedSectionTitle));
        OnPropertyChanged(nameof(IsAppearance));
        OnPropertyChanged(nameof(IsShortcuts));
        OnPropertyChanged(nameof(IsSshKeys));
        OnPropertyChanged(nameof(IsTools));
        OnPropertyChanged(nameof(IsIntegrations));
        OnPropertyChanged(nameof(IsProfiles));
        OnPropertyChanged(nameof(IsProjects));
        OnPropertyChanged(nameof(IsIcons));
        OnPropertyChanged(nameof(IsNotIcons));
        // Keys may have been added in ~/.ssh meanwhile: read again each time the section is shown.
        if (IsSshKeys) _ = LoadSshKeysAsync(_lifetime.Token);
    }

    /// <summary>Selects a section by its kind; ignored for a disabled one.</summary>
    public void Show(PreferencesSection section)
    {
        if (Sections.FirstOrDefault(s => s.Section == section) is { IsEnabled: true } item) SelectedSection = item;
    }

    // ---- Général ------------------------------------------------------------------------------------------------------

    public ObservableCollection<AutoFetchOption> AutoFetchOptions { get; }

    /// <summary>Choice matching the interval (an interval set by hand outside the list is added to it).</summary>
    [ObservableProperty]
    public partial AutoFetchOption? AutoFetch { get; set; }

    partial void OnAutoFetchChanged(AutoFetchOption? value)
    {
        if (value is not null) Update(p => p with { AutoFetchIntervalMinutes = value.Minutes });
    }

    [ObservableProperty]
    public partial bool CheckoutNewBranch { get; set; }

    partial void OnCheckoutNewBranchChanged(bool value) => Update(p => p with { CheckoutNewBranch = value });

    [ObservableProperty]
    public partial bool AutoPrune { get; set; }

    partial void OnAutoPruneChanged(bool value) => Update(p => p with { AutoPrune = value });

    [ObservableProperty]
    public partial bool UpdatePreReleases { get; set; }

    partial void OnUpdatePreReleasesChanged(bool value) => Update(p => p with { UpdatePreReleases = value });

    [ObservableProperty]
    public partial bool KeepSubmodulesUpToDate { get; set; }

    partial void OnKeepSubmodulesUpToDateChanged(bool value) => Update(p => p with { KeepSubmodulesUpToDate = value });

    [ObservableProperty]
    public partial bool GraphLazyLoad { get; set; }

    partial void OnGraphLazyLoadChanged(bool value) => Update(p => p with { GraphLazyLoad = value });

    /// <summary>Bound to a NumberBox (double); cleared box = NaN, ignored.</summary>
    [ObservableProperty]
    public partial double GraphInitialCommits { get; set; }

    partial void OnGraphInitialCommitsChanged(double value)
    {
        if (!double.IsNaN(value)) Update(p => p with { GraphInitialCommits = (int)Math.Clamp(value, UserPreferences.MinGraphInitialCommits, UserPreferences.MaxGraphInitialCommits) });
    }

    // ---- Langue -------------------------------------------------------------------------------------------------------

    /// <summary>Languages offered (the names of the languages themselves are not translated).</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    public partial LanguageOption? Language { get; set; }

    partial void OnLanguageChanged(LanguageOption? value)
    {
        if (value is not null) Update(p => p with { Language = value.Language });
    }

    /// <summary>The language chosen differs from the one the application started with: the texts change at the next launch.</summary>
    public bool NeedsRestart => Language is not null && Language.Language != _startupLanguage;

    // ---- Apparence ----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    public bool IsSystemTheme
    {
        get => Theme == AppTheme.System;
        set { if (value) Theme = AppTheme.System; }
    }

    public bool IsLightTheme
    {
        get => Theme == AppTheme.Light;
        set { if (value) Theme = AppTheme.Light; }
    }

    public bool IsDarkTheme
    {
        get => Theme == AppTheme.Dark;
        set { if (value) Theme = AppTheme.Dark; }
    }

    partial void OnThemeChanged(AppTheme value)
    {
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        Update(p => p with { Theme = value });
    }

    [ObservableProperty]
    public partial bool IsCompact { get; set; }

    public bool IsNormal
    {
        get => !IsCompact;
        set { if (value) IsCompact = false; }
    }

    partial void OnIsCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNormal));
        Update(p => p with { Density = value ? Density.Compact : Density.Normal });
    }

    // ---- Éditeur et outils --------------------------------------------------------------------------------------------

    /// <summary>"Outil de fusion externe" (mockup): VIGO's conflict editor, a known tool, or the one of the Git configuration.</summary>
    public ObservableCollection<MergeToolOption> MergeToolOptions { get; }

    [ObservableProperty]
    public partial MergeToolOption? MergeTool { get; set; }

    partial void OnMergeToolChanged(MergeToolOption? value)
    {
        if (value is not null) Update(p => p with { MergeTool = value.Tool });
    }

    // ---- Clés SSH -----------------------------------------------------------------------------------------------------

    /// <summary>Keys of <c>~/.ssh</c> then imported ones; the one VIGO uses is marked <see cref="SshKeyItem.IsDefault"/>.</summary>
    public ObservableCollection<SshKeyItem> SshKeys { get; } = [];

    /// <summary>The key list was read at least once (no "Aucune clé" flash before the first read).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSshKey))]
    public partial bool SshKeysLoaded { get; private set; }

    public bool HasNoSshKey => SshKeysLoaded && SshKeys.Count == 0;

    /// <summary>No key chosen: ssh picks one itself (<c>~/.ssh/config</c>, standard names).</summary>
    [ObservableProperty]
    public partial bool HasNoDefaultSshKey { get; private set; } = true;

    [ObservableProperty]
    public partial bool UseWindowsSshAgent { get; set; }

    partial void OnUseWindowsSshAgentChanged(bool value)
    {
        Update(p => p with { UseWindowsSshAgent = value });
        OnPropertyChanged(nameof(SshAgentWarning));
        OnPropertyChanged(nameof(HasSshAgentWarning));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SshAgentStatus), nameof(SshAgentWarning), nameof(HasSshAgentWarning))]
    public partial SshAgentState SshAgentState { get; private set; }

    /// <summary>State of the ssh-agent service, next to the switch.</summary>
    public string SshAgentStatus => SshAgentState switch
    {
        SshAgentState.Running => Loc.Get("Preferences.SshAgent.Running"),
        SshAgentState.Stopped => Loc.Get("Preferences.SshAgent.Stopped"),
        SshAgentState.Disabled => Loc.Get("Preferences.SshAgent.Disabled"),
        SshAgentState.NotInstalled => Loc.Get("Preferences.SshAgent.NotInstalled"),
        _ => string.Empty,
    };

    /// <summary>What to do when the agent is wanted but cannot serve the keys; null when nothing is needed.</summary>
    public string? SshAgentWarning => !UseWindowsSshAgent ? null : SshAgentState switch
    {
        SshAgentState.Stopped or SshAgentState.Disabled =>
            Loc.Get("Preferences.SshAgent.StoppedWarning"),
        SshAgentState.NotInstalled =>
            Loc.Get("Preferences.SshAgent.NotInstalledWarning"),
        _ => null,
    };

    public bool HasSshAgentWarning => SshAgentWarning is not null;

    /// <summary>Folder of the keys, suggested by the "Importer une clé…" picker.</summary>
    public string? SshKeyDirectory => _sshKeys?.KeyDirectory;

    /// <summary>Reads the keys and the state of the agent again.</summary>
    public async Task LoadSshKeysAsync(CancellationToken cancellationToken = default)
    {
        if (_sshKeys is null) return;
        try
        {
            var agent = _sshKeys.GetAgentStateAsync(cancellationToken);
            var keys = await _sshKeys.ListAsync(_preferences.Current.ImportedSshKeys, cancellationToken);
            SshKeys.Clear();
            foreach (var key in keys) SshKeys.Add(new SshKeyItem(key, IsImported(key.PrivateKeyPath)));
            SshKeysLoaded = true;
            OnPropertyChanged(nameof(HasNoSshKey));
            SyncDefaultSshKey();
            SshAgentState = await agent;
        }
        catch (OperationCanceledException)
        {
            // Tab closed.
        }
    }

    /// <summary>Makes VIGO use this key, or stops using it when it already does.</summary>
    [RelayCommand]
    private void ToggleDefaultSshKey(SshKeyItem item) =>
        Update(p => p with { SshKeyPath = item.IsDefault ? null : item.Key.PrivateKeyPath });

    /// <summary>Removes an imported key from the list (the files stay where they are).</summary>
    [RelayCommand]
    private async Task RemoveSshKeyAsync(SshKeyItem item)
    {
        var path = item.Key.PrivateKeyPath;
        await _preferences.UpdateAsync(p => p with
        {
            ImportedSshKeys = p.ImportedSshKeys.RemoveAll(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase)),
            SshKeyPath = string.Equals(p.SshKeyPath, path, StringComparison.OrdinalIgnoreCase) ? null : p.SshKeyPath,
        }, _lifetime.Token);
        await LoadSshKeysAsync(_lifetime.Token);
    }

    /// <summary>Deletes the key pair files from disk after a confirmation, and forgets the key (list, default key).</summary>
    [RelayCommand]
    private async Task DeleteSshKeyAsync(SshKeyItem item)
    {
        if (_sshKeys is null) return;
        var path = item.Key.PrivateKeyPath;
        var confirmed = _confirmations is null || (await _confirmations.ConfirmAsync(new ConfirmationRequest(
            Loc.Get("SshKeys.Delete.Title"),
            Loc.Format("SshKeys.Delete.Message", item.Key.Name, path),
            Loc.Get("SshKeys.Delete.Confirm"), IsDestructive: true), _lifetime.Token)).Confirmed;
        if (!confirmed) return;

        switch ((await _sshKeys.DeleteAsync(path, _lifetime.Token)).Value)
        {
            case Done:
                await _preferences.UpdateAsync(p => p with
                {
                    ImportedSshKeys = p.ImportedSshKeys.RemoveAll(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase)),
                    SshKeyPath = string.Equals(p.SshKeyPath, path, StringComparison.OrdinalIgnoreCase) ? null : p.SshKeyPath,
                }, _lifetime.Token);
                await LoadSshKeysAsync(_lifetime.Token);
                _notifier?.Notify(NotificationSeverity.Success, Loc.Format("SshKeys.Deleted.Title", item.Key.Name), Loc.Get("SshKeys.Deleted.Message"));
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Error, f.Message, f.Details);
                await LoadSshKeysAsync(_lifetime.Token);
                break;
            case Canceled or Conflict or AuthenticationFailed:
                break;
        }
    }

    /// <summary>Adds the key chosen in the file picker (private key or its .pub) and makes VIGO use it.</summary>
    public async Task ImportSshKeyAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_sshKeys is null) return;
        var result = await _sshKeys.ReadAsync(path, cancellationToken);
        switch (result.Value)
        {
            case Success<SshKey> { Value: var key }:
                var inKeyDirectory = string.Equals(Path.GetDirectoryName(key.PrivateKeyPath), Path.GetFullPath(_sshKeys.KeyDirectory), StringComparison.OrdinalIgnoreCase);
                await _preferences.UpdateAsync(p => p with
                {
                    ImportedSshKeys = inKeyDirectory || IsImported(key.PrivateKeyPath) ? p.ImportedSshKeys : p.ImportedSshKeys.Add(key.PrivateKeyPath),
                    SshKeyPath = key.PrivateKeyPath,
                }, cancellationToken);
                await LoadSshKeysAsync(cancellationToken);
                _notifier?.Notify(NotificationSeverity.Success, Loc.Format("SshKeys.Imported.Title", key.Name), Loc.Get("SshKeys.Imported.Message"));
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Error, Loc.Get("SshKeys.ImportFailed"), f.Message);
                break;
            case Canceled or Conflict or AuthenticationFailed:
                break;
        }
    }

    /// <summary>Generates an ED25519 key in <c>~/.ssh</c> and makes VIGO use it.</summary>
    [RelayCommand]
    private async Task GenerateSshKeyAsync(CancellationToken cancellationToken)
    {
        if (_sshKeys is null) return;
        var result = await _sshKeys.GenerateAsync(cancellationToken);
        switch (result.Value)
        {
            case Success<SshKey> { Value: var key }:
                await _preferences.UpdateAsync(p => p with { SshKeyPath = key.PrivateKeyPath }, cancellationToken);
                await LoadSshKeysAsync(cancellationToken);
                _notifier?.Notify(NotificationSeverity.Success, Loc.Format("SshKeys.Created.Title", key.Name), Loc.Get("SshKeys.Created.Message"));
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Error, f.Message, f.Details);
                break;
            case Canceled or Conflict or AuthenticationFailed:
                break;
        }
    }

    /// <summary>The view copied the public key to the clipboard.</summary>
    public void OnPublicKeyCopied(SshKeyItem item) =>
        _notifier?.Notify(NotificationSeverity.Success, Loc.Get("SshKeys.Copied.Title"),
            Loc.Format("SshKeys.Copied.Message", item.Key.Name));

    private bool IsImported(string privateKeyPath) =>
        _preferences.Current.ImportedSshKeys.Contains(privateKeyPath, StringComparer.OrdinalIgnoreCase);

    private void SyncDefaultSshKey()
    {
        var current = _preferences.Current.SshKeyPath;
        foreach (var item in SshKeys)
            item.IsDefault = string.Equals(item.Key.PrivateKeyPath, current, StringComparison.OrdinalIgnoreCase);
        HasNoDefaultSshKey = current is null;
    }

    public void Dispose()
    {
        _preferences.Changed -= OnPreferencesChanged;
        DisposeIntegrations();
        DisposeProfiles();
        DisposeProjects();
        DisposeShortcuts();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnPreferencesChanged(object? sender, EventArgs e) => Sync();

    /// <summary>Shows the current preferences without saving them again.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            var current = _preferences.Current;
            var minutes = current.AutoFetchIntervalMinutes;
            var option = AutoFetchOptions.FirstOrDefault(o => o.Minutes == minutes);
            if (option is null)
            {
                option = new AutoFetchOption(minutes, Loc.Format("Preferences.AutoFetch.EveryMinutes", minutes));
                var index = AutoFetchOptions.TakeWhile(o => o.Minutes < minutes).Count();
                AutoFetchOptions.Insert(index, option);
            }
            AutoFetch = option;
            CheckoutNewBranch = current.CheckoutNewBranch;
            AutoPrune = current.AutoPrune;
            UpdatePreReleases = current.UpdatePreReleases;
            KeepSubmodulesUpToDate = current.KeepSubmodulesUpToDate;
            GraphInitialCommits = current.GraphInitialCommits;
            GraphLazyLoad = current.GraphLazyLoad;
            Language = LanguageOptions.First(o => o.Language == current.Language);
            Theme = current.Theme;
            IsCompact = current.Density == Density.Compact;
            UseWindowsSshAgent = current.UseWindowsSshAgent;
            var tool = MergeToolOptions.FirstOrDefault(o => o.Tool == current.MergeTool);
            if (tool is null)
            {
                // A tool set by hand in the file: kept and shown under its own name.
                tool = new MergeToolOption(current.MergeTool, current.MergeTool ?? string.Empty);
                MergeToolOptions.Insert(MergeToolOptions.Count - 1, tool);
            }
            MergeTool = tool;
            SyncDefaultSshKey();
            SyncLaneColors(current);
            SyncAi(current);
            SyncTerminal(current);
            SyncTextSettings(current);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Update(Func<UserPreferences, UserPreferences> change)
    {
        if (!_syncing) _ = _preferences.UpdateAsync(change);
    }
}

/// <summary>Choice of "Outil de fusion externe": a <c>git mergetool</c> name; null for VIGO's conflict editor.</summary>
public sealed record MergeToolOption(string? Tool, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Key of the "Clés SSH" section.</summary>
public sealed partial class SshKeyItem(SshKey key, bool isImported) : ObservableObject
{
    public SshKey Key { get; } = key;

    /// <summary>Added with "Importer une clé…" (outside <c>~/.ssh</c>): can be removed from the list.</summary>
    public bool IsImported { get; } = isImported;

    /// <summary>VIGO hands this key to ssh.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Usage), nameof(DefaultActionLabel))]
    public partial bool IsDefault { get; set; }

    /// <summary>Third line of the card (mockup: "Utilisée par GitHub…").</summary>
    public string Usage => (Loc.Get(IsDefault ? "SshKeys.Usage.Default" : IsImported ? "SshKeys.Usage.Imported" : "SshKeys.Usage.InSsh"))
        + (Key.Comment is { } comment ? $" · {comment}" : string.Empty);

    public string DefaultActionLabel => Loc.Get(IsDefault ? "SshKeys.Action.Stop" : "SshKeys.Action.Use");

    public override string ToString() => Key.Name;
}
