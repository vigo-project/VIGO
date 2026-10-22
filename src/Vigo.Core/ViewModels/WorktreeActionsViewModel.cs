using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Worktrees of a repository tab (J7.1, ADR-049): the list shown in the left panel, creating a linked working directory
/// (on a new branch, or on an existing branch from its menu), opening one in its own tab, removing one, pruning the
/// ones whose directory is gone.
/// </summary>
/// <remarks>
/// Operations go through <see cref="StagingActions"/> (one at a time, toast on failure); the list is read again after
/// each one and whenever the refs change (a branch checked out elsewhere is shown with its worktree).
/// </remarks>
public sealed partial class WorktreeActionsViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly StagingActions _actions;
    private readonly IInputDialogService? _inputs;
    private readonly IUserNotifier? _notifier;
    private readonly Func<string, Task>? _open;
    private readonly CancellationToken _lifetime;
    private bool _loaded;

    /// <param name="open">Opens a directory as a repository tab; null disables "Ouvrir".</param>
    public WorktreeActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, StagingActions actions,
        IInputDialogService? inputs, IUserNotifier? notifier, Func<string, Task>? open, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _actions = actions;
        _inputs = inputs;
        _notifier = notifier;
        _open = open;
        _lifetime = lifetime;
        _graph.RefsChanged += OnRefsChanged;
    }

    /// <summary>Working directories of the repository, the main one first; empty until the first read.</summary>
    public IReadOnlyList<Worktree> Worktrees { get; private set; } = [];

    /// <summary>The list of worktrees changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The dialogs can be shown (not in tests or headless).</summary>
    public bool CanPrompt => _inputs is not null;

    public bool CanOpen => _open is not null;

    /// <summary>The tab shows this worktree: its path is the one the tab was opened on.</summary>
    public bool IsCurrent(Worktree worktree) => SamePath(worktree.Path, _repositoryPath);

    public async Task LoadAsync()
    {
        var result = await _git.GetWorktreesAsync(_repositoryPath, _lifetime);
        if (_lifetime.IsCancellationRequested || result.Value is not Success<IReadOnlyList<Worktree>> worktrees) return;
        _loaded = true;
        if (worktrees.Value.SequenceEqual(Worktrees)) return;
        Worktrees = worktrees.Value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Folder of the worktree that checks out <paramref name="branch"/>, when it is not this tab's; null otherwise.</summary>
    public Worktree? WorktreeOf(string branch) =>
        Worktrees.FirstOrDefault(w => w.Branch == branch && !IsCurrent(w));

    // ---- Add -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Nouveau worktree…": a new branch started on the selected commit (or HEAD) in a new directory next to the
    /// repository.
    /// </summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (_inputs is null) return;
        if ((_graph.SelectedSha ?? _graph.Head) is not { } start)
        {
            _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("RefActions.NoCommit.Title"), Loc.Get("Worktree.NoCommit.Detail"));
            return;
        }

        var locals = _graph.Refs?.OfKind(GitRefKind.LocalBranch).Select(r => r.Name).ToList() ?? [];
        var dialog = new InputDialogViewModel(Loc.Get("Worktree.Add.Title"), Loc.Get("Worktree.Add.Confirm"), Loc.Get("Worktree.Add.Header"),
            name => RefNames.Check(name) ?? RefNames.CheckConflict(name, locals, RefNameKind.Branch),
            validateSecond: CheckPath)
        {
            SpacesBecomeDashes = true,
            Description = _graph.FindCommit(start) is { } commit
                ? Loc.Format("RefActions.FromCommitSubject", start.ToShortString(), commit.Subject)
                : Loc.Format("RefActions.FromCommit", start.ToShortString()),
            NamePlaceholder = Loc.Get("RefActions.NewBranch.Placeholder"),
            SecondHeader = Loc.Get("Worktree.Folder.Header"),
            SecondText = DefaultPath("worktree"),
            SecondPlaceholder = Loc.Get("Worktree.Folder.Placeholder"),
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        await CreateAsync(new AddWorktreeRequest(dialog.SecondValue, NewBranch: dialog.Value, Commit: start), dialog.Value);
    }

    /// <summary>"Ouvrir dans un nouveau worktree…" of a local branch that is not checked out anywhere.</summary>
    [RelayCommand]
    private async Task AddForBranchAsync(GitRef branch)
    {
        if (_inputs is null || branch.Kind != GitRefKind.LocalBranch) return;
        var dialog = new InputDialogViewModel(Loc.Format("Worktree.ForBranch.Title", branch.Name), Loc.Get("Worktree.Add.Confirm"), Loc.Get("Worktree.Folder.Header"), CheckPath,
            DefaultPath(branch.Name))
        {
            Description = Loc.Get("Worktree.ForBranch.Description"),
            NamePlaceholder = Loc.Get("Worktree.Folder.Placeholder"),
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        await CreateAsync(new AddWorktreeRequest(dialog.Value, Branch: branch.Name), branch.Name);
    }

    private async Task CreateAsync(AddWorktreeRequest request, string branch)
    {
        var created = await _actions.RunAsync(Loc.Get("Worktree.Add.Failed"), ct => _git.AddWorktreeAsync(_repositoryPath, request, ct));
        await LoadAsync();
        if (!created) return;
        _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Worktree.Add.Done"), Loc.Format("Worktree.Add.DoneDetail", branch, request.Path));
        await _graph.RefreshAsync();
    }

    // ---- Open, remove, prune ---------------------------------------------------------------------------------------

    /// <summary>"Ouvrir": the worktree in a tab of its own (the tab of an already open one is shown).</summary>
    [RelayCommand]
    private async Task OpenAsync(Worktree worktree)
    {
        if (_open is null || IsCurrent(worktree) || worktree.IsBare) return;
        if (worktree.IsPrunable || !Directory.Exists(worktree.Path))
        {
            _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Worktree.Missing.Title"), Loc.Format("Worktree.Missing.Detail", worktree.Path));
            return;
        }
        await _open(worktree.Path);
    }

    /// <summary>
    /// "Supprimer le worktree…": removes the directory and forgets it (the branch stays). A worktree with modifications
    /// or locked is only removed after a second, explicit confirmation.
    /// </summary>
    [RelayCommand]
    private async Task RemoveAsync(Worktree worktree)
    {
        if (worktree.IsMain || IsCurrent(worktree) || _actions.IsBusy) return;
        var where = worktree.Branch is { } branch ? Loc.Format("Worktree.Remove.BranchKept", branch) : string.Empty;
        var answer = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("Worktree.Remove.Title", worktree.Name),
            Loc.Format("Worktree.Remove.Message", worktree.Path, where), Loc.Get("Worktree.Remove.Confirm"), IsDestructive: true));
        if (!answer.Confirmed) return;

        Failure? refused = null;
        var removed = await _actions.RunAsync(Loc.Format("Worktree.Remove.Failed", worktree.Name), async ct =>
        {
            var result = await _git.RemoveWorktreeAsync(_repositoryPath, worktree.Path, force: false, ct);
            if (result.Value is not Failure { Reason: FailureReason.WorktreeNeedsForce } needsForce) return result;
            refused = needsForce;
            return Done.Instance;
        });

        // The first attempt "succeeds" when git only asked for --force: the user decides.
        if (refused is not null)
        {
            var again = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("Worktree.Force.Title", worktree.Name),
                Loc.Format("Worktree.Force.Message", refused.Message), Loc.Get("Worktree.Force.Confirm"), IsDestructive: true));
            removed = again.Confirmed && await _actions.RunAsync(Loc.Format("Worktree.Remove.Failed", worktree.Name),
                ct => _git.RemoveWorktreeAsync(_repositoryPath, worktree.Path, force: true, ct));
        }

        await LoadAsync();
        if (removed) _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Worktree.Remove.Done"), worktree.Path);
        if (!_lifetime.IsCancellationRequested) await _graph.RefreshAsync();
    }

    /// <summary>Whether some worktree lost its directory (moved or deleted outside git).</summary>
    public bool HasPrunable => Worktrees.Any(w => w.IsPrunable);

    /// <summary>"Nettoyer les worktrees": forgets the ones whose directory no longer exists.</summary>
    [RelayCommand]
    private async Task PruneAsync()
    {
        await _actions.RunAsync(Loc.Get("Worktree.Prune.Failed"), ct => _git.PruneWorktreesAsync(_repositoryPath, ct));
        await LoadAsync();
    }

    public void Dispose() => _graph.RefsChanged -= OnRefsChanged;

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    // Checking out a branch in a worktree shows in the refs of every other worktree only through the shared list.
    private void OnRefsChanged(object? sender, EventArgs e)
    {
        if (_loaded) _ = LoadAsync();
    }

    private static string? CheckPath(string path) =>
        path.Length == 0 ? Loc.Get("Worktree.Path.Required")
        : path.StartsWith('-') ? Loc.Get("Worktree.Path.StartsWithDash")
        : !Path.IsPathRooted(path) ? Loc.Get("Worktree.Path.NotAbsolute")
        : null;

    /// <summary>Next to the repository: "D:\dev\depot" → "D:\dev\depot-feature-x".</summary>
    private string DefaultPath(string suffix)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(_repositoryPath);
        var parent = Path.GetDirectoryName(trimmed);
        var name = Path.GetFileName(trimmed);
        var clean = string.Concat(suffix.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c));
        return parent is null ? string.Empty : Path.Combine(parent, $"{name}-{clean}");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a.Replace('/', Path.DirectorySeparatorChar)),
            Path.TrimEndingDirectorySeparator(b.Replace('/', Path.DirectorySeparatorChar)),
            StringComparison.OrdinalIgnoreCase);
}
