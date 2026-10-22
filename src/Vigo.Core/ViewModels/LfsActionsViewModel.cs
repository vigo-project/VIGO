using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Git LFS of a repository tab (J7.5, ADR-055): the patterns routed through LFS and the state of the LFS files shown in the
/// left panel, tracking a pattern, untracking one, downloading the missing objects.
/// </summary>
/// <remarks>
/// Operations go through <see cref="StagingActions"/> (one at a time, toast on failure); the state is read again after each
/// one and whenever the refs change (a checkout or a pull changes the LFS files).
/// </remarks>
public sealed partial class LfsActionsViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly StagingActions _actions;
    private readonly IInputDialogService? _inputs;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;
    private bool _loaded;

    public LfsActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, StagingActions actions,
        IInputDialogService? inputs, IUserNotifier? notifier, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _actions = actions;
        _inputs = inputs;
        _notifier = notifier;
        _lifetime = lifetime;
        _graph.RefsChanged += OnRefsChanged;
    }

    /// <summary>What the repository stores with LFS; <see cref="LfsInfo.NotUsed"/> until the first read.</summary>
    public LfsInfo Info { get; private set; } = LfsInfo.NotUsed;

    /// <summary>The state changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The dialogs can be shown (not in tests or headless).</summary>
    public bool CanPrompt => _inputs is not null;

    /// <summary>Some attributes file routes patterns through LFS: the section is shown.</summary>
    public bool IsUsed => Info.IsUsed;

    /// <summary>"Récupérer les fichiers LFS" has something to download.</summary>
    public bool CanPull => Info is { IsUsed: true, IsInstalled: true } && Info.MissingCount > 0;

    public async Task LoadAsync()
    {
        var result = await _git.GetLfsAsync(_repositoryPath, _lifetime);
        if (_lifetime.IsCancellationRequested || result.Value is not Success<LfsInfo> read) return;
        _loaded = true;
        if (read.Value.SameAs(Info)) return;
        Info = read.Value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Track, untrack --------------------------------------------------------------------------------------------

    /// <summary>"Suivre avec Git LFS…": a file pattern whose files LFS stores from now on.</summary>
    [RelayCommand]
    private async Task TrackAsync()
    {
        if (_inputs is null || _actions.IsBusy) return;
        var dialog = new InputDialogViewModel(Loc.Get("Lfs.Track.Title"), Loc.Get("Lfs.Track.Confirm"), Loc.Get("Lfs.Track.Header"), CheckPattern)
        {
            Description = Loc.Get("Lfs.Track.Description"),
            NamePlaceholder = "*.psd",
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        var pattern = dialog.Value;
        var tracked = await _actions.RunAsync(Loc.Get("Lfs.Track.Failed"), ct => _git.LfsTrackAsync(_repositoryPath, pattern, ct));
        await LoadAsync();
        if (tracked) _notifier?.Notify(NotificationSeverity.Success, "Git LFS", Loc.Format("Lfs.Track.Done", pattern));
    }

    /// <summary>"Ne plus suivre" a pattern: new files are stored normally again; files already in LFS keep their pointers.</summary>
    [RelayCommand]
    private async Task UntrackAsync(LfsPattern pattern)
    {
        if (_actions.IsBusy) return;
        var untracked = await _actions.RunAsync(Loc.Get("Lfs.Untrack.Failed"), ct => _git.LfsUntrackAsync(_repositoryPath, pattern.Pattern, ct));
        await LoadAsync();
        if (untracked) _notifier?.Notify(NotificationSeverity.Success, "Git LFS", Loc.Format("Lfs.Untrack.Done", pattern.Pattern, pattern.Source));
    }

    // ---- Download --------------------------------------------------------------------------------------------------

    /// <summary>"Récupérer les fichiers LFS": downloads the objects whose files are only pointers in the working directory.</summary>
    [RelayCommand]
    private async Task PullAsync()
    {
        if (_actions.IsBusy || !CanPull) return;
        var missing = Info.MissingCount;
        var pulled = await _actions.RunAsync(Loc.Get("Lfs.Pull.Failed"), ct => _git.LfsPullAsync(_repositoryPath, ct));
        await LoadAsync();
        if (pulled)
            _notifier?.Notify(NotificationSeverity.Success, "Git LFS", missing == 1 ? Loc.Get("Lfs.Pull.DoneOne") : Loc.Format("Lfs.Pull.DoneMany", missing));
    }

    public void Dispose() => _graph.RefsChanged -= OnRefsChanged;

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    // A checkout or a pull changes which files are stored in LFS and whether their content is there.
    private void OnRefsChanged(object? sender, EventArgs e)
    {
        if (_loaded) _ = LoadAsync();
    }

    private static string? CheckPattern(string pattern) =>
        pattern.Length == 0 ? Loc.Get("Lfs.Pattern.Required")
        : pattern.StartsWith('-') ? Loc.Get("Lfs.Pattern.StartsWithDash")
        : pattern.Any(char.IsControl) ? Loc.Get("Lfs.Pattern.ControlChar")
        : null;
}
