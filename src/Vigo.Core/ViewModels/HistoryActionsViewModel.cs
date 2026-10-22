using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Reset of the checked-out branch (or detached HEAD) to <see cref="Target"/>.</summary>
public sealed record ResetPlan(GitSha Target, ResetMode Mode);

/// <summary>
/// Cherry-pick, revert and reset of a repository tab (J5.1), from the menu of a commit row of the graph (mockup rowMenu).
/// </summary>
/// <remarks>
/// Operations run through <see cref="StagingActions"/> (one at a time with the index operations, status reread); the
/// refs are read again after each one. A cherry-pick or revert stopped on conflicts stays in progress: the banner of
/// <see cref="MergeActionsViewModel"/> continues, skips or aborts it, and the commit form can conclude it. Only the hard
/// reset is confirmed (uncommitted changes are lost); the three are recorded for Undo / Redo (<see cref="UndoHistory"/>).
/// </remarks>
public sealed partial class HistoryActionsViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly WorkingTreeViewModel _tree;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;

    public HistoryActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, WorkingTreeViewModel tree, IUserNotifier? notifier, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _tree = tree;
        _notifier = notifier;
        _lifetime = lifetime;
    }

    private StagingActions Actions => _tree.Actions;

    private GitSha? Head => _tree.Status?.Head ?? _graph.Head;

    /// <summary>A working directory with a commit checked out, no operation in progress, nothing running.</summary>
    public bool CanStart => _tree.IsAvailable && Head is not null && _tree.Status?.Operation is null && !Actions.IsBusy;

    /// <summary>Cherry-picking HEAD itself would bring nothing.</summary>
    public bool CanCherryPick(GitSha commit) => CanStart && commit != Head;

    public bool CanRevert(GitSha commit) => CanStart;

    public bool CanReset(GitSha commit) => CanStart;

    /// <summary>Submenu of the row menu (mockup: "Reset feature/oauth-refresh ici").</summary>
    public string ResetLabel => Loc.Format("History.ResetLabel", BranchName);

    private string BranchName => _tree.Status?.Branch ?? _graph.HeadBranch ?? "HEAD";

    // ---- Operations ------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task CherryPickAsync(GitSha commit)
    {
        if (!CanCherryPick(commit)) return;
        var what = Describe(commit);
        var branch = BranchName;
        await Actions.RunAsync(Loc.Format("History.CherryPick.Failed", commit.ToShortString()),
            ct => _git.CherryPickAsync(_repositoryPath, commit, ct),
            _ => _notifier?.Notify(NotificationSeverity.Success, Loc.Get("History.CherryPick.Done"), Loc.Format("History.CherryPick.Detail", what, branch)),
            conflict => NotifyConflict(conflict, Loc.Format("History.CherryPick.ConflictPrefix", commit.ToShortString())),
            new UndoStep(Loc.Format("History.CherryPick.Undo", what)));
        await RefreshRefsAsync();
        ShowStoppedOperation();
    }

    [RelayCommand]
    private async Task RevertAsync(GitSha commit)
    {
        if (!CanRevert(commit)) return;
        var what = Describe(commit);
        await Actions.RunAsync(Loc.Format("History.Revert.Failed", commit.ToShortString()),
            ct => _git.RevertAsync(_repositoryPath, commit, ct),
            head => _notifier?.Notify(NotificationSeverity.Success, Loc.Get("History.Revert.Done"), Loc.Format("History.Revert.Detail", what, head.ToShortString())),
            conflict => NotifyConflict(conflict, Loc.Format("History.Revert.ConflictPrefix", commit.ToShortString())),
            new UndoStep(Loc.Format("History.Revert.Undo", what)));
        await RefreshRefsAsync();
        ShowStoppedOperation();
    }

    /// <summary>Soft and mixed run at once; hard asks first, saying what will be lost.</summary>
    [RelayCommand]
    private async Task ResetAsync(ResetPlan plan)
    {
        if (!CanReset(plan.Target) || Head is not { } head) return;
        var (target, mode) = plan;
        var branch = BranchName;
        var what = Describe(target);
        var leaving = (await _git.CountCommitsAsync(_repositoryPath, target, head, _lifetime)).Value is Success<int> s ? s.Value : 0;

        if (mode == ResetMode.Hard)
        {
            var tracked = _tree.Status?.Entries.Count(e => !e.IsUntracked) ?? 0;
            var lines = new List<string> { Loc.Format("History.Reset.Moved", branch, what) };
            if (leaving == 1) lines.Add(Loc.Get("History.Reset.LeavingOne"));
            else if (leaving > 0) lines.Add(Loc.Format("History.Reset.LeavingMany", leaving));
            lines.Add(tracked switch
            {
                0 => Loc.Get("History.Reset.CleanTree"),
                1 => Loc.Get("History.Reset.LossOne"),
                _ => Loc.Format("History.Reset.LossMany", tracked),
            });
            var confirmed = await Actions.ConfirmAsync(new ConfirmationRequest(
                Loc.Format("History.Reset.Title", branch), string.Join("\n\n", lines), Loc.Get("History.Reset.Confirm"), IsDestructive: true));
            if (!confirmed.Confirmed) return;
        }

        if (await Actions.RunAsync(Loc.Format("History.Reset.Failed", branch), ct => _git.ResetAsync(_repositoryPath, target, mode, ct),
                // Soft and mixed come back the same way; a hard reset comes back keeping the changes made since.
                new UndoStep(Loc.Format("History.Reset.Undo", branch, what), mode == ResetMode.Hard ? ResetMode.Keep : mode)))
        {
            var kept = (mode, leaving) switch
            {
                (ResetMode.Hard, _) or (_, 0) => string.Empty,
                (ResetMode.Soft, _) => Loc.Get("History.Reset.KeptStaged"),
                _ => Loc.Get("History.Reset.KeptInTree"),
            };
            var moved = leaving switch
            {
                0 => string.Empty,
                1 => Loc.Get("History.Reset.RemovedOne"),
                _ => Loc.Format("History.Reset.RemovedMany", leaving),
            };
            _notifier?.Notify(NotificationSeverity.Success, Loc.Get("History.Reset.Done"), Loc.Format("History.Reset.DoneDetail", branch, what, moved, kept));
        }
        await RefreshRefsAsync();
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    /// <summary>Stopped on conflicts: the work-in-progress row is selected, so that the banner and the conflicted files show.</summary>
    private void ShowStoppedOperation()
    {
        if (_tree.Status?.Operation is not null) _graph.SelectWorkInProgress();
    }

    /// <summary>"a1b2c3d « Fix cache »" when the commit is in the graph, else its short SHA.</summary>
    private string Describe(GitSha commit) =>
        _graph.FindCommit(commit) is { } info ? Loc.Format("Common.CommitWithSubject", commit.ToShortString(), info.Subject) : commit.ToShortString();

    private void NotifyConflict(Conflict conflict, string prefix)
    {
        var count = conflict.Paths.Count == 1 ? Loc.Get("Common.ConflictCount.One") : Loc.Format("Common.ConflictCount.Many", conflict.Paths.Count);
        _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Common.ConflictDetected"), Loc.Format("History.Conflict.Detail", prefix, count));
    }

    private async Task RefreshRefsAsync()
    {
        if (!_lifetime.IsCancellationRequested) await _graph.RefreshAsync();
    }
}
