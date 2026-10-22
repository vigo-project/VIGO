using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Stashes of a repository tab (J4.4): stash the changes (toolbar, working directory panel, Ctrl+Maj+S; with a message
/// from the menu), apply / pop / delete a stash (left panel, toolbar "Pop"), and checkout carrying the local changes
/// when git refuses to overwrite them (stash, checkout, pop).
/// </summary>
/// <remarks>
/// Operations run through <see cref="StagingActions"/> (one at a time with the index operations, status reread); the
/// refs are read again after each one. A stash is designated by its commit, never by its index: indexes shift when a
/// stash is pushed or dropped.
/// </remarks>
public sealed partial class StashActionsViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly WorkingTreeViewModel _tree;
    private readonly IInputDialogService? _inputs;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;

    public StashActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, WorkingTreeViewModel tree,
        IInputDialogService? inputs, IUserNotifier? notifier, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _tree = tree;
        _inputs = inputs;
        _notifier = notifier;
        _lifetime = lifetime;
        _tree.StatusChanged += OnStateChanged;
        _graph.RefsChanged += OnStateChanged;
        _tree.Actions.PropertyChanged += OnActionsPropertyChanged;
    }

    private StagingActions Actions => _tree.Actions;

    /// <summary>Changes to put aside, none of them in conflict (git cannot stash an unmerged index), nothing running.</summary>
    public bool CanStash => _tree.IsAvailable && _tree.ChangeCount > 0 && !_tree.HasConflicts && !Actions.IsBusy;

    /// <summary>Newest stash (<c>stash@{0}</c>), popped by the toolbar button; null when there is none.</summary>
    public GitRef? LatestStash => _graph.Refs?.OfKind(GitRefKind.Stash).FirstOrDefault();

    public bool CanPop => _tree.IsAvailable && LatestStash is not null && !Actions.IsBusy;

    /// <summary>Tooltip of the toolbar button (mockup: "Pop stash@{0}").</summary>
    public string PopTooltip => LatestStash is { } stash
        ? Loc.Format("Toolbar.Stash.Pop", stash.Name, Subject(stash))
        : Loc.Get("Toolbar.Stash.PopNone");

    // ---- Stash -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Button and Ctrl+Maj+S: puts every change aside at once, untracked files included (mockup), under git's default
    /// message ("WIP on main: …").
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStash))]
    private Task StashAsync()
    {
        // A message typed in the commit form names the stash and leaves the form; "Pop" puts it back.
        var form = _tree.CommitForm;
        if (form.IsAmend || string.IsNullOrWhiteSpace(form.Summary)) return PushAsync(new StashRequest(null, IncludeUntracked: true));
        return PushAsync(new StashRequest(form.Summary.Trim(), IncludeUntracked: true, Description: form.Description), () =>
        {
            form.Summary = string.Empty;
            form.Description = string.Empty;
        });
    }

    /// <summary>"Stash avec message…": message and "Inclure les fichiers non suivis" (checked) in a dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanStash))]
    private async Task StashWithMessageAsync()
    {
        if (_inputs is null || !CanStash) return;
        var branch = _tree.Status?.Branch ?? Loc.Get("Stash.Dialog.Detached");
        InputDialogViewModel? dialog = null;
        // A description without a title is refused: the confirm button stays disabled (empty error = nothing to say).
        dialog = new InputDialogViewModel(Loc.Get("Stash.Dialog.Title"), Loc.Get("Stash.Dialog.Confirm"), Loc.Get("Stash.Dialog.Header"),
            name => name.Length == 0 && dialog?.SecondValue.Length > 0 ? string.Empty : null)
        {
            SecondHeader = Loc.Get("Stash.Dialog.DetailsHeader"),
            SecondPlaceholder = Loc.Get("Stash.Dialog.DetailsPlaceholder"),
            IsSecondMultiline = true,
            Description = _tree.ChangeCount == 1
                ? Loc.Format("Stash.Dialog.DescriptionOne", branch)
                : Loc.Format("Stash.Dialog.DescriptionMany", _tree.ChangeCount, branch),
            NamePlaceholder = Loc.Get("Stash.Dialog.Placeholder"),
            OptionText = Loc.Get("Stash.Dialog.IncludeUntracked"),
            OptionChecked = true,
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;
        await PushAsync(new StashRequest(dialog.Value, dialog.OptionChecked, Description: dialog.SecondValue));
    }

    /// <summary>"Stash du dossier" (right click on a folder of the working directory panel): the changes under it, untracked files included.</summary>
    [RelayCommand]
    private Task StashFolderAsync(FolderRow folder) => CanStash ? PushAsync(new StashRequest(null, IncludeUntracked: true, [folder.Path])) : Task.CompletedTask;

    private async Task PushAsync(StashRequest request, Action? stashed = null)
    {
        if (!CanStash) return;
        var entries = _tree.Status?.Entries ?? [];
        var count = request.Paths is { } paths
            ? entries.Count(e => paths.Any(p => e.Path.StartsWith(p + "/", StringComparison.Ordinal) || e.Path == p))
            : request.IncludeUntracked ? _tree.ChangeCount : entries.Count(e => !e.IsUntracked);
        await Actions.RunAsync(Loc.Get("Stash.Failed"), ct => _git.StashAsync(_repositoryPath, request, ct),
            _ =>
            {
                stashed?.Invoke();
                _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Stash.Created.Title"),
                    count == 1 ? Loc.Format("Stash.Created.DetailOne") : Loc.Format("Stash.Created.DetailMany", count));
            },
            _ => { });
        await RefreshRefsAsync();
    }

    // ---- Apply, pop, delete -----------------------------------------------------------------------------------------

    /// <summary>Toolbar "Pop": the newest stash.</summary>
    [RelayCommand(CanExecute = nameof(CanPop))]
    private Task PopLatestAsync() => LatestStash is { } stash ? ApplyCoreAsync(stash, drop: true) : Task.CompletedTask;

    /// <summary>"Appliquer": the changes come back, the stash stays.</summary>
    [RelayCommand]
    private Task ApplyAsync(GitRef stash) => ApplyCoreAsync(stash, drop: false);

    /// <summary>"Pop": the changes come back and the stash is deleted (kept when they conflict).</summary>
    [RelayCommand]
    private Task PopAsync(GitRef stash) => ApplyCoreAsync(stash, drop: true);

    private async Task ApplyCoreAsync(GitRef stash, bool drop)
    {
        if (!_tree.IsAvailable || stash.Kind != GitRefKind.Stash) return;
        var before = _tree.ChangeCount;
        // Read before a pop drops the stash: the full message holds the description.
        var saved = drop ? await ReadMessageAsync(stash) : null;
        var applied = await Actions.RunAsync(Loc.Format("Stash.Apply.Failed", stash.Name), async Task<GitResult<bool>> (CancellationToken ct) =>
            (await _git.ApplyStashAsync(_repositoryPath, stash.Target, drop, ct)).Value switch
            {
                Done => new Success<bool>(true),
                Conflict c => c,
                AuthenticationFailed a => a,
                Canceled c => c,
                Failure f => f,
                _ => new Failure(Loc.Get("Common.UnexpectedResult")),
            },
            _ => { },
            conflict => NotifyConflict(conflict, Loc.Format("Stash.Apply.ConflictPrefix", stash.Name)));
        if (applied)
        {
            var form = _tree.CommitForm;
            if (saved is { } message && !form.IsAmend && form.Summary.Length == 0 && form.Description.Length == 0)
            {
                form.Summary = message.Summary;
                form.Description = message.Description;
            }

            // The status is reread by now: on a directory that was clean, every change listed came from the stash.
            var after = _tree.ChangeCount;
            _notifier?.Notify(NotificationSeverity.Success, Loc.Format("Stash.Apply.Done", stash.Name),
                before == 0 && after > 0
                    ? after == 1 ? Loc.Get("Stash.Apply.RestoredOne") : Loc.Format("Stash.Apply.RestoredMany", after)
                    : Loc.Get("Stash.Apply.Restored"));
        }
        await RefreshRefsAsync();
    }

    /// <summary>
    /// "Appliquer la sélection" of a stash selected in the graph (J7.16): only the changes the stash made to <paramref name="files"/> come
    /// back, merged with the local ones; the stash stays.
    /// </summary>
    public async Task ApplyFilesAsync(GitRef stash, IReadOnlyList<ChangedFile> files)
    {
        if (!_tree.IsAvailable || stash.Kind != GitRefKind.Stash || files.Count == 0) return;
        var paths = files.SelectMany(f => f.OldPath is { } old ? new[] { old, f.Path } : [f.Path]).Distinct().ToList();
        var applied = await Actions.RunAsync(Loc.Format("Stash.ApplyFiles.Failed", stash.Name), async Task<GitResult<bool>> (CancellationToken ct) =>
            (await _git.ApplyStashFilesAsync(_repositoryPath, stash.Target, paths, ct)).Value switch
            {
                Done => new Success<bool>(true),
                Conflict c => c,
                AuthenticationFailed a => a,
                Canceled c => c,
                Failure f => f,
                _ => new Failure(Loc.Get("Common.UnexpectedResult")),
            },
            _ => { },
            conflict => NotifyConflict(conflict, Loc.Format("Stash.Apply.ConflictPrefix", stash.Name)));
        if (applied)
        {
            _notifier?.Notify(NotificationSeverity.Success, Loc.Format("Stash.Apply.Done", stash.Name),
                files.Count == 1 ? Loc.Get("Stash.ApplyFiles.DoneOne") : Loc.Format("Stash.ApplyFiles.DoneMany", files.Count));
        }
        await RefreshRefsAsync();
    }

    /// <summary>"Supprimer…" (Suppr) after confirmation: the stash is lost.</summary>
    [RelayCommand]
    private async Task DeleteAsync(GitRef stash)
    {
        if (stash.Kind != GitRefKind.Stash || Actions.IsBusy) return;
        var subject = Subject(stash);
        var answer = await Actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("Stash.Delete.Title", stash.Name),
            Loc.Format("Stash.Delete.Message", subject.Length > 0 ? Loc.Format("Stash.Delete.Subject", subject) : string.Empty),
            Loc.Get("Stash.Delete.Confirm"), IsDestructive: true));
        if (!answer.Confirmed) return;
        if (await Actions.RunAsync(Loc.Format("Stash.Delete.Failed", stash.Name), ct => _git.DropStashAsync(_repositoryPath, stash.Target, ct)))
            _notifier?.Notify(NotificationSeverity.Info, Loc.Get("Stash.Delete.Done"), $"{stash.Name} : {subject}".TrimEnd(' ', ':'));
        await RefreshRefsAsync();
    }

    // ---- Checkout carrying the local changes ----------------------------------------------------------------------

    /// <summary>Called after a successful checkout (J7.26: keeps the submodules up to date).</summary>
    public Func<Task>? HeadMoved { get; set; }

    /// <summary>
    /// Checkout of <paramref name="target"/> (<paramref name="name"/> shown). When git refuses because local changes
    /// would be overwritten, carries them automatically: stash (untracked files included), checkout, pop. A pop that
    /// conflicts leaves the conflicts in the working directory and keeps the stash.
    /// </summary>
    public async Task CheckoutAsync(CheckoutTarget target, string name)
    {
        var failureTitle = Loc.Format("Stash.Checkout.Failed", name);
        var refused = false;
        var checkedOut = false;
        var carried = false;
        await Actions.RunAsync<bool>(failureTitle, async ct => (await _git.CheckoutAsync(_repositoryPath, target, ct)).Value switch
        {
            Done => new Success<bool>(true),
            Failure { Reason: FailureReason.LocalChangesWouldBeOverwritten } => new Success<bool>(false),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
            _ => new Failure(Loc.Get("Common.UnexpectedResult")),
        }, done => { refused = !done; checkedOut = done; }, _ => { }, new UndoStep(Loc.Format("Stash.Checkout.Undo", name)));
        if (checkedOut && HeadMoved is { } moved) await moved();
        if (!refused || _lifetime.IsCancellationRequested) return;

        await Actions.RunAsync<bool>(failureTitle, ct => CarryChangesAsync(target, name, ct),
            _ =>
            {
                carried = true;
                _notifier?.Notify(NotificationSeverity.Success, Loc.Format("Stash.Checkout.Done", name), Loc.Get("Stash.Checkout.DoneDetail"));
            },
            conflict => NotifyConflict(conflict, Loc.Format("Stash.Checkout.ConflictPrefix", name)),
            new UndoStep(Loc.Format("Stash.Checkout.Undo", name)));
        if (carried && HeadMoved is { } afterCarry) await afterCarry();
        await RefreshRefsAsync();
    }

    private async Task<GitResult<bool>> CarryChangesAsync(CheckoutTarget target, string name, CancellationToken ct)
    {
        var stashed = await _git.StashAsync(_repositoryPath, new StashRequest(null, IncludeUntracked: true), ct);
        if (stashed.Value is not Success<GitSha> { Value: var stash })
        {
            return stashed.Value switch
            {
                Conflict c => c,
                AuthenticationFailed a => a,
                Canceled c => c,
                Failure f => f,
                _ => new Failure(Loc.Get("Common.UnexpectedResult")),
            };
        }

        // Not cancelable from here on: the changes must come back whatever happens.
        var checkout = await _git.CheckoutAsync(_repositoryPath, target, CancellationToken.None);
        if (checkout.Value is not Done)
        {
            var restored = (await _git.ApplyStashAsync(_repositoryPath, stash, drop: true, CancellationToken.None)).Value is Done;
            var reason = checkout.Value switch
            {
                Failure f => f.Message,
                Conflict c => string.Join(", ", c.Paths),
                AuthenticationFailed a => a.Message,
                _ => Loc.Get("Stash.Checkout.Canceled"),
            };
            return new Failure(restored
                ? reason
                : Loc.Format("Stash.Checkout.LeftInStash", reason, stash.ToShortString()));
        }

        return (await _git.ApplyStashAsync(_repositoryPath, stash, drop: true, CancellationToken.None)).Value switch
        {
            Done => new Success<bool>(true),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => new Failure(Loc.Format("Stash.Checkout.ReapplyFailed", name, f.Message), Details: f.Details),
            _ => new Failure(Loc.Get("Common.UnexpectedResult")),
        };
    }

    public void Dispose()
    {
        _tree.StatusChanged -= OnStateChanged;
        _graph.RefsChanged -= OnStateChanged;
        _tree.Actions.PropertyChanged -= OnActionsPropertyChanged;
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    /// <summary>Title and description the stash was given; null for git's default "WIP on …" message or when it cannot be read.</summary>
    private async Task<(string Summary, string Description)?> ReadMessageAsync(GitRef stash)
    {
        if ((await _git.GetCommitAsync(_repositoryPath, stash.Target, _lifetime)).Value is not Success<CommitDetails> s) return null;
        var (first, description) = CommitMessage.Split(s.Value.Message);
        if (first.StartsWith("WIP on ", StringComparison.Ordinal)) return null;
        var summary = RefsPanelViewModel.StashSubject(first);
        return summary.Length == 0 ? null : (summary, description);
    }

    /// <summary>"try paging" for "On main: try paging".</summary>
    private static string Subject(GitRef stash) => RefsPanelViewModel.StashSubject(stash.Message);

    private void NotifyConflict(Conflict conflict, string prefix) =>
        _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Common.ConflictDetected"),
            conflict.Paths.Count == 1
                ? Loc.Format("Stash.Conflict.DetailOne", prefix)
                : Loc.Format("Stash.Conflict.DetailMany", prefix, conflict.Paths.Count));

    private void OnStateChanged(object? sender, EventArgs e) => UpdateState();

    private void OnActionsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StagingActions.IsBusy)) UpdateState();
    }

    private void UpdateState()
    {
        OnPropertyChanged(nameof(CanStash));
        OnPropertyChanged(nameof(CanPop));
        OnPropertyChanged(nameof(LatestStash));
        OnPropertyChanged(nameof(PopTooltip));
        StashCommand.NotifyCanExecuteChanged();
        StashWithMessageCommand.NotifyCanExecuteChanged();
        PopLatestCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshRefsAsync()
    {
        if (!_lifetime.IsCancellationRequested) await _graph.RefreshAsync();
    }
}
