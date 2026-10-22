using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Submodules of a repository tab (J7.2, ADR-050): the list shown in the left panel, initializing / updating one or all of
/// them, opening one in its own tab, deinitializing one, adding one.
/// </summary>
/// <remarks>
/// Operations go through <see cref="StagingActions"/> (one at a time, toast on failure); the list is read again after
/// each one and whenever the refs change (a checkout or a pull moves the commits the repository records).
/// </remarks>
public sealed partial class SubmoduleActionsViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly StagingActions _actions;
    private readonly IInputDialogService? _inputs;
    private readonly IUserNotifier? _notifier;
    private readonly Func<string, Task>? _open;
    private readonly Preferences? _preferences;
    private readonly CancellationToken _lifetime;
    private bool _loaded;

    /// <param name="open">Opens a directory as a repository tab; null disables "Ouvrir".</param>
    public SubmoduleActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, StagingActions actions,
        IInputDialogService? inputs, IUserNotifier? notifier, Func<string, Task>? open, CancellationToken lifetime,
        Preferences? preferences = null)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _actions = actions;
        _inputs = inputs;
        _notifier = notifier;
        _open = open;
        _preferences = preferences;
        _lifetime = lifetime;
        _graph.RefsChanged += OnRefsChanged;
    }

    /// <summary>Submodules declared by the repository; empty until the first read.</summary>
    public IReadOnlyList<Submodule> Submodules { get; private set; } = [];

    /// <summary>The list of submodules changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The dialogs can be shown (not in tests or headless).</summary>
    public bool CanPrompt => _inputs is not null;

    public bool CanOpen => _open is not null;

    /// <summary>The repository declares at least one submodule: the section is shown.</summary>
    public bool HasSubmodules => Submodules.Count > 0;

    public async Task LoadAsync()
    {
        var result = await _git.GetSubmodulesAsync(_repositoryPath, _lifetime);
        if (_lifetime.IsCancellationRequested || result.Value is not Success<IReadOnlyList<Submodule>> submodules) return;
        _loaded = true;
        if (submodules.Value.SequenceEqual(Submodules)) return;
        Submodules = submodules.Value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Update ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Initialiser et mettre à jour" / "Mettre à jour": clones a missing submodule, or puts it back on the commit the
    /// repository records (after a confirmation when it is somewhere else: what was checked out there is left behind).
    /// </summary>
    [RelayCommand]
    private async Task UpdateAsync(Submodule submodule)
    {
        if (_actions.IsBusy) return;
        if (submodule.State is SubmoduleState.OutOfSync or SubmoduleState.Conflict && !await ConfirmResetAsync(submodule.Label)) return;
        await RunUpdateAsync(submodule.Path, Loc.Format("Submodule.Update.Failed", submodule.Label), Loc.Format("Submodule.Update.UpToDate", submodule.Label));
    }

    /// <summary>"Mettre à jour les submodules": every submodule is cloned if needed and put on its recorded commit.</summary>
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (_actions.IsBusy || Submodules.Count == 0) return;
        var moved = Submodules.Where(s => s.State is SubmoduleState.OutOfSync or SubmoduleState.Conflict).Select(s => s.Label).ToList();
        if (moved.Count > 0 && !await ConfirmResetAsync(string.Join(", ", moved))) return;
        await RunUpdateAsync(null, Loc.Get("Submodule.UpdateAll.Failed"), Loc.Get("Submodule.UpdateAll.UpToDate"));
    }

    private async Task<bool> ConfirmResetAsync(string names)
    {
        var answer = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Get("Submodule.Reset.Title"),
            Loc.Format("Submodule.Reset.Message", names), Loc.Get("Submodule.Reset.Confirm"), IsDestructive: true));
        return answer.Confirmed;
    }

    /// <summary>
    /// After a pull or a checkout moved HEAD (J7.26): with "Garder les submodules à jour", runs
    /// <c>submodule update --init --recursive</c>. A failure leaves the pull or checkout done and only shows a toast.
    /// </summary>
    public async Task UpdateAfterHeadMoveAsync()
    {
        if (_preferences?.Current.KeepSubmodulesUpToDate != true || _actions.IsBusy || _lifetime.IsCancellationRequested) return;
        // The list may predate a submodule the pull just brought: ask the working directory, not Submodules.
        if (!await Task.Run(() => File.Exists(Path.Combine(_repositoryPath, ".gitmodules")), _lifetime)) return;
        await _actions.RunAsync(Loc.Get("Submodule.AfterMove.Failed"), ct => _git.UpdateSubmodulesAsync(_repositoryPath, null, ct));
        await LoadAsync();
    }

    private async Task RunUpdateAsync(string? path, string failureTitle, string success)
    {
        var updated = await _actions.RunAsync(failureTitle, ct => _git.UpdateSubmodulesAsync(_repositoryPath, path, ct));
        await LoadAsync();
        if (updated) _notifier?.Notify(NotificationSeverity.Success, "Submodules", success);
    }

    // ---- Open, deinitialize ----------------------------------------------------------------------------------------

    /// <summary>"Ouvrir": the submodule in a tab of its own (the tab of an already open one is shown).</summary>
    [RelayCommand]
    private async Task OpenAsync(Submodule submodule)
    {
        if (_open is null) return;
        var folder = FolderOf(submodule);
        if (!submodule.IsInitialized || !Directory.Exists(folder))
        {
            _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Submodule.NotInitialized.Title"), Loc.Format("Submodule.NotInitialized.Detail", submodule.Label));
            return;
        }
        await _open(folder);
    }

    /// <summary>
    /// "Désinitialiser…": empties the working directory of the submodule and unregisters it; the commit the repository
    /// records and its <c>.gitmodules</c> entry stay. Local modifications are only discarded after a second confirmation.
    /// </summary>
    [RelayCommand]
    private async Task DeinitAsync(Submodule submodule)
    {
        if (!submodule.IsInitialized || _actions.IsBusy) return;
        var answer = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("Submodule.Deinit.Title", submodule.Label),
            Loc.Format("Submodule.Deinit.Message", submodule.Path), Loc.Get("Submodule.Deinit.Confirm"), IsDestructive: true));
        if (!answer.Confirmed) return;

        Failure? refused = null;
        var done = await _actions.RunAsync(Loc.Format("Submodule.Deinit.Failed", submodule.Label), async ct =>
        {
            var result = await _git.DeinitSubmoduleAsync(_repositoryPath, submodule.Path, force: false, ct);
            if (result.Value is not Failure { Reason: FailureReason.SubmoduleNeedsForce } needsForce) return result;
            refused = needsForce;
            return Done.Instance;
        });

        // The first attempt "succeeds" when git only asked for --force: the user decides.
        if (refused is not null)
        {
            var again = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("Submodule.Force.Title", submodule.Label),
                Loc.Format("Submodule.Force.Message", refused.Message), Loc.Get("Submodule.Force.Confirm"), IsDestructive: true));
            done = again.Confirmed && await _actions.RunAsync(Loc.Format("Submodule.Deinit.Failed", submodule.Label),
                ct => _git.DeinitSubmoduleAsync(_repositoryPath, submodule.Path, force: true, ct));
        }

        await LoadAsync();
        if (done) _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Submodule.Deinit.Done"), submodule.Path);
    }

    // ---- Add -------------------------------------------------------------------------------------------------------

    /// <summary>"Ajouter un submodule…": the URL to clone and, optionally, the folder (the repository name by default).</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (_inputs is null || _actions.IsBusy) return;
        var dialog = new InputDialogViewModel(Loc.Get("Submodule.Add.Title"), Loc.Get("Submodule.Add.Confirm"), Loc.Get("Submodule.Add.Header"), CheckUrl,
            validateSecond: CheckPath)
        {
            Description = Loc.Get("Submodule.Add.Description"),
            NamePlaceholder = Loc.Get("Submodule.Add.UrlPlaceholder"),
            SecondHeader = Loc.Get("Submodule.Add.FolderHeader"),
            SecondPlaceholder = "libs/depot",
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        var path = dialog.SecondValue.Length == 0 ? null : dialog.SecondValue;
        var added = await _actions.RunAsync(Loc.Get("Submodule.Add.Failed"), ct => _git.AddSubmoduleAsync(_repositoryPath, dialog.Value, path, ct));
        await LoadAsync();
        if (added) _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Submodule.Add.Done"), Loc.Get("Submodule.Add.DoneDetail"));
    }

    public void Dispose() => _graph.RefsChanged -= OnRefsChanged;

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    // A checkout or a pull moves the commit the repository records for each submodule.
    private void OnRefsChanged(object? sender, EventArgs e)
    {
        if (_loaded) _ = LoadAsync();
    }

    private string FolderOf(Submodule submodule) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(_repositoryPath, submodule.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    private static string? CheckUrl(string url) =>
        url.Length == 0 ? Loc.Get("RefActions.Url.Required")
        : url.StartsWith('-') ? Loc.Get("RefActions.Url.StartsWithDash")
        : url.Any(char.IsControl) || url.Any(char.IsWhiteSpace) ? Loc.Get("Submodule.Url.Whitespace")
        : null;

    private static string? CheckPath(string path) =>
        path.Length == 0 ? null
        : path.StartsWith('-') ? Loc.Get("Worktree.Path.StartsWithDash")
        : System.IO.Path.IsPathRooted(path) ? Loc.Get("Submodule.Path.NotRelative")
        : path.Split('/', '\\').Contains("..") ? Loc.Get("Submodule.Path.Outside")
        : null;
}
