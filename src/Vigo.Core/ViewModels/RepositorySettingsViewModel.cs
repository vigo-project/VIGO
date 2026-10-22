using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A choice of the default encoding; <see cref="Value"/> is what <c>gui.encoding</c> holds (null: automatic detection).</summary>
public sealed record EncodingOption(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Dépôt en cours" section of the preferences (J7.29): settings kept in the repository's own git config (<c>git config --local</c>), so that
/// they travel with the repository and other tools read them. Built for the repository tab that was shown before the preferences.
/// </summary>
public sealed partial class RepositorySettingsViewModel : ObservableObject
{
    // gui.encoding names (git gui's list, plus "system" for the ANSI code page of Windows).
    private static readonly (string Value, string Label)[] Encodings =
    [
        ("utf-8", "UTF-8"), ("utf-16", "UTF-16 LE"), ("windows-1252", "Windows-1252"), ("iso-8859-1", "ISO-8859-1"), ("iso-8859-15", "ISO-8859-15"),
        ("windows-1250", "Windows-1250"), ("shift_jis", "Shift JIS"), ("gbk", "GBK"), ("big5", "Big5"), ("koi8-r", "KOI8-R"),
    ];

    private readonly IGitService _git;
    private readonly string _path;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;
    private bool _loading;

    private readonly IFolderPicker? _folderPicker;

    public RepositorySettingsViewModel(
        IGitService git, string path, string name, IUserNotifier? notifier, CancellationToken lifetime, IFolderPicker? folderPicker = null)
    {
        _folderPicker = folderPicker;
        _git = git;
        _path = path;
        Name = name;
        _notifier = notifier;
        _lifetime = lifetime;
        EncodingOptions =
        [
            new(null, Loc.Get("Preferences.Repository.Encoding.Auto")),
            .. Encodings.Select(e => new EncodingOption(e.Value, e.Label)),
            new("system", Loc.Get("Preferences.Repository.Encoding.System")),
        ];
        // Not a choice of the user: nothing to write.
        _loading = true;
        SelectedEncoding = EncodingOptions[0];
        _loading = false;
    }

    /// <summary>Name of the repository: title of the section.</summary>
    public string Name { get; }

    public string Path => _path;

    public ObservableCollection<EncodingOption> EncodingOptions { get; }

    [ObservableProperty]
    public partial EncodingOption SelectedEncoding { get; set; }

    /// <summary>The write of the last choice (tests await it).</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    partial void OnSelectedEncodingChanged(EncodingOption value)
    {
        if (!_loading) Saving = SaveEncodingAsync(value.Value);
    }

    private Task? _loaded;

    /// <summary>Reads the settings of the repository (once: a second call waits for the first, which must not overwrite a choice made since).</summary>
    public Task LoadAsync() => _loaded ??= ReadAsync();

    private async Task ReadAsync()
    {
        await LoadGitFlowAsync();
        await LoadHooksAsync();
        var encoding = await _git.GetEncodingAsync(_path, _lifetime);
        if (encoding.Value is not Success<string?> { Value: var value }) return;

        _loading = true;
        try
        {
            // A value written by hand that the list lacks is added to it, not lost.
            var option = EncodingOptions.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
            if (option is null && value is not null) EncodingOptions.Add(option = new EncodingOption(value, value));
            SelectedEncoding = option ?? EncodingOptions[0];
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- Git Flow (J7.30) ------------------------------------------------------------------------------------------

    private GitFlowConfig? _savedFlow;
    private int _flowCheck;

    [ObservableProperty]
    public partial string FlowMain { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FlowDevelop { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FlowFeature { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FlowRelease { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FlowHotfix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FlowVersionTag { get; set; } = string.Empty;

    /// <summary>Why the fields cannot be saved (under them); null when they can, or when nothing changed.</summary>
    [ObservableProperty]
    public partial string? FlowMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGitFlowCommand))]
    public partial bool CanSaveGitFlow { get; private set; }

    /// <summary>The repository uses Git Flow: the fields are shown, else the button that initializes it.</summary>
    [ObservableProperty]
    public partial bool IsGitFlowInitialized { get; private set; }

    public bool CanInitializeGitFlow => !IsGitFlowInitialized && _flow is { CanPrompt: true };

    private GitFlowConfig FlowFields => new(FlowMain, FlowDevelop, FlowFeature, FlowRelease, FlowHotfix, FlowVersionTag);

    partial void OnFlowMainChanged(string value) => CheckGitFlow();

    partial void OnFlowDevelopChanged(string value) => CheckGitFlow();

    partial void OnFlowFeatureChanged(string value) => CheckGitFlow();

    partial void OnFlowReleaseChanged(string value) => CheckGitFlow();

    partial void OnFlowHotfixChanged(string value) => CheckGitFlow();

    partial void OnFlowVersionTagChanged(string value) => CheckGitFlow();

    partial void OnIsGitFlowInitializedChanged(bool value) => OnPropertyChanged(nameof(CanInitializeGitFlow));

    private async Task LoadGitFlowAsync()
    {
        var result = await _git.GetGitFlowAsync(_path, _lifetime);
        if (result.Value is not Success<GitFlowConfig?> read) return;
        ApplyGitFlow(read.Value);
    }

    private void ApplyGitFlow(GitFlowConfig? config)
    {
        _savedFlow = config;
        _loading = true;
        try
        {
            var shown = config ?? GitFlowConfig.Defaults();
            (FlowMain, FlowDevelop, FlowFeature, FlowRelease, FlowHotfix, FlowVersionTag) =
                (shown.Main, shown.Develop, shown.FeaturePrefix, shown.ReleasePrefix, shown.HotfixPrefix, shown.VersionTagPrefix);
        }
        finally
        {
            _loading = false;
        }
        IsGitFlowInitialized = config is not null;
        CheckGitFlow();
    }

    // Enabled only when modified and usable; the check reads git (branches exist), so a late answer for an older edit is dropped.
    private void CheckGitFlow()
    {
        if (_loading) return;
        _ = CheckGitFlowAsync();
    }

    private async Task CheckGitFlowAsync()
    {
        var token = ++_flowCheck;
        var fields = FlowFields;
        if (_savedFlow is null || fields == _savedFlow)
        {
            FlowMessage = null;
            CanSaveGitFlow = false;
            return;
        }
        var problem = await _git.GitFlowProblemAsync(_path, fields, _lifetime);
        if (token != _flowCheck) return;
        FlowMessage = problem;
        CanSaveGitFlow = problem is null;
    }

    /// <summary>Awaited by the tests: the last check of the fields.</summary>
    internal Task WaitForGitFlowCheckAsync() => CheckGitFlowAsync();

    private GitFlowActionsViewModel? _flow;

    /// <summary>The left panel's Git Flow, reloaded after a save and used to initialize (null in tests).</summary>
    internal void AttachGitFlow(GitFlowActionsViewModel? flow) => _flow = flow;

    [RelayCommand(CanExecute = nameof(CanSaveGitFlow))]
    private async Task SaveGitFlowAsync()
    {
        var config = FlowFields;
        switch ((await _git.SetGitFlowAsync(_path, config, _lifetime)).Value)
        {
            case Done:
                ApplyGitFlow(config);
                if (_flow is { } flow) await flow.LoadAsync();
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Preferences.Repository.GitFlow.Failed"), f.Message);
                break;
            case AuthenticationFailed a:
                _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Preferences.Repository.GitFlow.Failed"), a.Message);
                break;
        }
    }

    [RelayCommand]
    private async Task InitializeGitFlowAsync()
    {
        if (_flow is null) return;
        await _flow.InitializeCommand.ExecuteAsync(null);
        await LoadGitFlowAsync();
    }

    // ---- Hooks (J7.31) -----------------------------------------------------------------------------------------------

    private string? _savedHooksPath;

    /// <summary>The hooks folder as typed (<c>core.hooksPath</c>; empty: <c>.git/hooks</c>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveHooksPathCommand))]
    public partial string HooksPath { get; set; } = string.Empty;

    public ObservableCollection<HookItem> Hooks { get; } = [];

    public bool HasNoHooks => Hooks.Count == 0;

    /// <summary>Commits and pushes made by VIGO skip the hooks (<c>vigo.skiphooks</c>).</summary>
    [ObservableProperty]
    public partial bool SkipHooks { get; set; }

    private bool CanSaveHooksPath => HooksPath.Trim() != (_savedHooksPath ?? string.Empty);

    partial void OnSkipHooksChanged(bool value)
    {
        if (!_loading) Saving = SaveSkipHooksAsync(value);
    }

    private async Task LoadHooksAsync()
    {
        var result = await _git.GetHooksAsync(_path, _lifetime);
        if (result.Value is not Success<RepositoryHooks> { Value: var hooks }) return;

        _loading = true;
        try
        {
            _savedHooksPath = hooks.HooksPath;
            HooksPath = hooks.HooksPath ?? string.Empty;
            SkipHooks = hooks.SkipHooks;
            Hooks.Clear();
            foreach (var hook in hooks.Hooks) Hooks.Add(new HookItem(hook.Name, hook.IsEnabled, this));
            OnPropertyChanged(nameof(HasNoHooks));
        }
        finally
        {
            _loading = false;
        }
        SaveHooksPathCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSaveHooksPath))]
    private async Task SaveHooksPathAsync()
    {
        if (Report(await _git.SetHooksPathAsync(_path, HooksPath, _lifetime))) await LoadHooksAsync();
    }

    [RelayCommand]
    private async Task ResetHooksPathAsync()
    {
        HooksPath = string.Empty;
        await SaveHooksPathAsync();
    }

    [RelayCommand]
    private async Task BrowseHooksPathAsync()
    {
        if (_folderPicker is null || await _folderPicker.PickFolderAsync(_lifetime) is not { } folder) return;
        HooksPath = folder;
        await SaveHooksPathAsync();
    }

    /// <summary>A hook's switch was flipped: renames its file, then re-reads the folder (which also restores the switch when refused).</summary>
    internal async Task SetHookEnabledAsync(HookItem hook, bool enabled)
    {
        Report(await _git.SetHookEnabledAsync(_path, hook.Name, enabled, _lifetime));
        await LoadHooksAsync();
    }

    private async Task SaveSkipHooksAsync(bool value) => Report(await _git.SetSkipHooksAsync(_path, value, _lifetime));

    /// <summary>Shows a failure as a toast; true when the operation succeeded.</summary>
    private bool Report(GitResult result)
    {
        var (message, details) = result.Value switch
        {
            Failure f => (f.Message, f.Details),
            AuthenticationFailed a => (a.Message, null),
            _ => (null, null),
        };
        if (message is null) return true;
        _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Preferences.Repository.Hooks.Failed"), details ?? message);
        return false;
    }

    private async Task SaveEncodingAsync(string? value)
    {
        switch ((await _git.SetEncodingAsync(_path, value, _lifetime)).Value)
        {
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Preferences.Repository.Encoding.Failed"), f.Message);
                break;
            case AuthenticationFailed a:
                _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Preferences.Repository.Encoding.Failed"), a.Message);
                break;
        }
    }
}

/// <summary>A hook of the list: its switch renames the file (<c>x</c> to <c>x.disabled</c> and back).</summary>
public sealed partial class HookItem(string name, bool isEnabled, RepositorySettingsViewModel owner) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = isEnabled;

    // The list is rebuilt after the rename: a flip by the user is the only change this item sees.
    partial void OnIsEnabledChanged(bool value) => _ = owner.SetHookEnabledAsync(this, value);
}
