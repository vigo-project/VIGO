using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>An operation that Undo can take back, as announced in the toolbar tooltip and the toasts.</summary>
/// <param name="Label">"Commit « Fix cache »", "Fusion de feature dans main"…</param>
/// <param name="HeadMode">
/// How the checked-out commit moves back and forth: <see cref="ResetMode.Soft"/> for a commit or an amend (its changes
/// come back staged), the mode of a soft or mixed reset, <see cref="ResetMode.Keep"/> for everything else.
/// </param>
/// <param name="IgnoreNewTags">Tags fetched by a pull are not taken back with it.</param>
public sealed record UndoStep(string Label, ResetMode HeadMode = ResetMode.Keep, bool IgnoreNewTags = false);

/// <summary>A recorded operation: HEAD, branches and tags before and after it.</summary>
internal sealed record UndoEntry(UndoStep Step, RefSnapshot Before, RefSnapshot After);

/// <summary>An operation being recorded (<see cref="UndoHistory.BeginAsync"/> … <see cref="UndoHistory.EndAsync"/>).</summary>
internal sealed record UndoRecording(UndoStep Step, RefSnapshot? Before);

/// <summary>
/// Undo / Redo of a repository tab (J5.2, ADR-035): the operations made in VIGO (commit, amend, checkout, merge, rebase,
/// reset, cherry-pick, revert, pull, branches and tags) are recorded as snapshots of HEAD, the local branches and the
/// tags taken before and after them; Undo puts the "before" back, Redo the "after".
/// </summary>
/// <remarks>
/// Only what VIGO did is recorded, and an entry is applied only if the references it touched are still as it left them
/// (otherwise both stacks are cleared: they no longer describe the repository). Local changes are never lost: the
/// checked-out commit moves with <c>git reset --keep</c> (or soft / mixed, see <see cref="UndoStep.HeadMode"/>), which
/// refuses when a change is in the way. An operation stopped on conflicts is recorded once concluded (continue, commit),
/// as a whole; aborted, it leaves nothing to undo. The stacks live with the tab (moved with it to another window) and
/// are lost when it is closed. Staging, discard, stash, fetch and push are not recorded.
/// </remarks>
public sealed partial class UndoHistory : ObservableObject, IDisposable
{
    /// <summary>Entries kept in each stack.</summary>
    public const int Capacity = 50;

    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly WorkingTreeViewModel _tree;
    private readonly GraphViewModel _graph;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private UndoRecording? _stopped;
    private int _recording;

    public UndoHistory(
        IGitService git, string repositoryPath, WorkingTreeViewModel tree, GraphViewModel graph, IUserNotifier? notifier, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _tree = tree;
        _graph = graph;
        _notifier = notifier;
        _lifetime = lifetime;
        _tree.Actions.PropertyChanged += OnActionsPropertyChanged;
        _tree.StatusChanged += OnStatusChanged;
    }

    /// <summary>Nothing running or being recorded, no operation stopped on conflicts.</summary>
    private bool IsReady => _tree.IsAvailable && _recording == 0 && !_tree.Actions.IsBusy && _tree.Status?.Operation is null;

    public bool CanUndo => IsReady && _undo.Count > 0;

    public bool CanRedo => IsReady && _redo.Count > 0;

    /// <summary>Label of the operation Undo takes back; null when there is none.</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Step.Label : null;

    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Step.Label : null;

    public string UndoTooltip => UndoLabel is { } label ? Loc.Format("Toolbar.Undo.Tooltip", label) : Loc.Get("Toolbar.Undo.None");

    public string RedoTooltip => RedoLabel is { } label ? Loc.Format("Toolbar.Redo.Tooltip", label) : Loc.Get("Toolbar.Redo.None");

    // ---- Recording -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Called before an operation: takes the "before" snapshot (that of the operation stopped on conflicts, when it is
    /// the one being concluded). Undo / Redo stay disabled until <see cref="EndAsync"/>.
    /// </summary>
    internal async Task<UndoRecording> BeginAsync(UndoStep step)
    {
        _recording++;
        NotifyState();
        if (_tree.Status?.Operation is not null)
        {
            // Concluding (or aborting) an operation started outside VIGO: its "before" is unknown, nothing is recorded.
            return _stopped ?? new UndoRecording(step, null);
        }
        _stopped = null;
        return new UndoRecording(step, await SnapshotAsync());
    }

    /// <summary>
    /// Called once the operation is over and the status reread: records it if HEAD, a branch or a tag changed. Stopped
    /// on conflicts, it is kept aside until concluded.
    /// </summary>
    internal async Task EndAsync(UndoRecording recording)
    {
        try
        {
            if (recording.Before is not { } before || _lifetime.IsCancellationRequested) return;
            if (_tree.Status?.Operation is not null)
            {
                _stopped = recording;
                return;
            }
            _stopped = null;

            if (await SnapshotAsync() is not { } after) return;
            if (recording.Step.IgnoreNewTags)
            {
                var kept = after.Refs.Where(r => !r.Key.StartsWith(RefSnapshot.TagPrefix, StringComparison.Ordinal) || before.Refs.ContainsKey(r.Key));
                after = after with { Refs = kept.ToDictionary(StringComparer.Ordinal) };
            }
            if (after.SameHead(before) && !RefSnapshot.ChangedRefs(before, after).Any()) return;

            _undo.Add(new UndoEntry(recording.Step, before, after));
            if (_undo.Count > Capacity) _undo.RemoveAt(0);
            _redo.Clear();
        }
        finally
        {
            _recording--;
            NotifyState();
        }
    }

    private async Task<RefSnapshot?> SnapshotAsync() =>
        (await _git.GetRefSnapshotAsync(_repositoryPath, _lifetime)).Value is Success<RefSnapshot> s ? s.Value : null;

    // ---- Undo / Redo -----------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync() => MoveAsync(undo: true);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task RedoAsync() => MoveAsync(undo: false);

    private async Task MoveAsync(bool undo)
    {
        if (!(undo ? CanUndo : CanRedo)) return;
        var (from, to) = undo ? (_undo, _redo) : (_redo, _undo);
        var entry = from[^1];
        var label = entry.Step.Label;
        var stale = false;

        var done = await _tree.Actions.RunAsync(Loc.Format(undo ? "Undo.Failed.Undo" : "Undo.Failed.Redo", label), async Task<GitResult> (CancellationToken ct) =>
        {
            var current = (await _git.GetRefSnapshotAsync(_repositoryPath, ct)).Value;
            if (current is not Success<RefSnapshot> { Value: var now })
            {
                return current switch
                {
                    Conflict c => c,
                    AuthenticationFailed a => a,
                    Canceled c => c,
                    Failure f => f,
                    _ => new Failure(Loc.Get("Undo.Unexpected")),
                };
            }
            if (Plan(entry, undo, now) is not { } target)
            {
                stale = true;
                return new Failure(Loc.Get("Undo.Stale"));
            }
            return await _git.RestoreRefsAsync(_repositoryPath, now, target, entry.Step.HeadMode, ct);
        });

        if (done)
        {
            from.RemoveAt(from.Count - 1);
            to.Add(entry);
            _notifier?.Notify(NotificationSeverity.Success, Loc.Get(undo ? "Undo.Done.Undo" : "Undo.Done.Redo"), label + ".");
        }
        else if (stale)
        {
            _undo.Clear();
            _redo.Clear();
        }
        NotifyState();
        if (!_lifetime.IsCancellationRequested) await _graph.RefreshAsync();
    }

    /// <summary>
    /// State to restore so that <paramref name="entry"/> is undone (or redone): the references it touched as they were
    /// before (after) it, everything else as it is now. Null when those references moved since.
    /// </summary>
    internal static RefSnapshot? Plan(UndoEntry entry, bool undo, RefSnapshot now)
    {
        var (expected, wanted) = undo ? (entry.After, entry.Before) : (entry.Before, entry.After);
        var touched = RefSnapshot.ChangedRefs(entry.Before, entry.After).ToList();
        var headMoved = !entry.Before.SameHead(entry.After);

        if (headMoved && !now.SameHead(expected)) return null;
        if (touched.Any(name => now.Refs.GetValueOrDefault(name)?.Target != expected.Refs.GetValueOrDefault(name)?.Target)) return null;

        var refs = new Dictionary<string, RefState>(now.Refs, StringComparer.Ordinal);
        foreach (var name in touched)
        {
            if (wanted.Refs.TryGetValue(name, out var state)) refs[name] = state;
            else refs.Remove(name);
        }

        if (!headMoved) return new RefSnapshot(now.HeadBranch, now.Head, refs);
        // HEAD back on a branch: on the commit that branch has in the restored state.
        var head = wanted.HeadBranch is { } branch && refs.TryGetValue(RefSnapshot.BranchPrefix + branch, out var tip) ? tip.Target : wanted.Head;
        return new RefSnapshot(wanted.HeadBranch, head, refs);
    }

    // ---- State -----------------------------------------------------------------------------------------------------

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
        OnPropertyChanged(nameof(UndoTooltip));
        OnPropertyChanged(nameof(RedoTooltip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnActionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StagingActions.IsBusy)) NotifyState();
    }

    private void OnStatusChanged(object? sender, EventArgs e) => NotifyState();

    public void Dispose()
    {
        _tree.Actions.PropertyChanged -= OnActionsPropertyChanged;
        _tree.StatusChanged -= OnStatusChanged;
    }
}
