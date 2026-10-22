using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A commit of the interactive rebase list: its action and, for a reword, its new subject.</summary>
public sealed partial class RebaseRow : ObservableObject
{
    public RebaseRow(RebaseCommit commit, string dateText)
    {
        Commit = commit;
        DateText = dateText;
        Subject = commit.Subject;
    }

    public RebaseCommit Commit { get; }

    public string ShortSha => Commit.Sha.ToShortString();

    public string AuthorName => Commit.AuthorName;

    public string AuthorInitials => new GitIdentity(Commit.AuthorName, null).Initials;

    /// <summary>"hier", "il y a 3 h" (mockup).</summary>
    public string DateText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionIndex), nameof(IsReword), nameof(IsNotReword), nameof(IsMelded), nameof(IsDropped), nameof(Tag), nameof(HasTag))]
    public partial RebaseAction Action { get; set; }

    /// <summary>Index of <see cref="Action"/> in <see cref="RebaseAction"/> (selection of the action list of the row).</summary>
    public int ActionIndex
    {
        get => (int)Action;
        set
        {
            if (value >= 0 && value <= (int)RebaseAction.Drop) Action = (RebaseAction)value;
        }
    }

    /// <summary>Subject shown, edited in place for a reword (the body of the message is kept).</summary>
    [ObservableProperty]
    public partial string Subject { get; set; }

    public bool IsReword => Action == RebaseAction.Reword;

    public bool IsNotReword => !IsReword;

    /// <summary>Squash or fixup: melded into the kept commit above (indented, mockup).</summary>
    public bool IsMelded => Action is RebaseAction.Squash or RebaseAction.Fixup;

    public bool IsDropped => Action == RebaseAction.Drop;

    /// <summary>Label after the message (mockup: "pause", "message combiné", "message ignoré").</summary>
    public string? Tag => Action switch
    {
        RebaseAction.Edit => Loc.Get("Rebase.Tag.Pause"),
        RebaseAction.Squash => Loc.Get("Rebase.Tag.Squash"),
        RebaseAction.Fixup => Loc.Get("Rebase.Tag.Fixup"),
        _ => null,
    };

    public bool HasTag => Tag is not null;

    /// <summary>Full message of a reword: the new subject, then the body of the original message.</summary>
    public string RewordMessage
    {
        get
        {
            var parts = Commit.Message.Split('\n', 2);
            return parts.Length > 1 ? Subject.Trim() + "\n" + parts[1] : Subject.Trim();
        }
    }

    /// <summary>Text read by the narrator for a row (and by UI Automation replays).</summary>
    public override string ToString() => $"{Action.ToString().ToLowerInvariant()} {ShortSha} {Subject}";
}

/// <summary>A commit of the result card (mockup: message, "inchangé", "3 commits combinés").</summary>
/// <param name="IsPaused">Edit step: the rebase stops there.</param>
public sealed record RebaseResultItem(string Message, string Detail, bool IsPaused);

/// <summary>
/// Interactive rebase of a repository tab (J5.5, mockup VIGO Outils.dc.html, "Rebase" screen), shown in place of the
/// graph: the commits HEAD would replay on an upstream, oldest first, each with its action (pick, reword, edit,
/// squash, fixup, drop), reordered with Alt+↑ / Alt+↓ or drag and drop; the result card says what the branch becomes.
/// </summary>
/// <remarks>
/// "fixup!" and "squash!" commits are moved under their target and preset (autosquash). The rebase runs through
/// <see cref="StagingActions"/> and is recorded for Undo / Redo as a whole, conflicts and edit stops included (concluded
/// from the operation banner). A range holding a merge commit is refused (ADR-041).
/// </remarks>
public sealed partial class InteractiveRebaseViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly WorkingTreeViewModel _tree;
    private readonly TimeProvider _time;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;
    private IReadOnlyList<RebaseCommit> _commits = [];
    private CancellationTokenSource? _loading;

    public InteractiveRebaseViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, WorkingTreeViewModel tree, TimeProvider time,
        IUserNotifier? notifier, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _tree = tree;
        _time = time;
        _notifier = notifier;
        _lifetime = lifetime;
        Rows.CollectionChanged += (_, _) => Update();
    }

    private StagingActions Actions => _tree.Actions;

    /// <summary>A working directory with a commit checked out, no operation in progress, nothing running.</summary>
    public bool CanOpen => _tree.IsAvailable && (_tree.Status?.Head ?? _graph.Head) is not null && _tree.Status?.Operation is null && !Actions.IsBusy;

    /// <summary>Shown in place of the graph.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Commits in application order (oldest first); reordered in place.</summary>
    public ObservableCollection<RebaseRow> Rows { get; } = [];

    /// <summary>What the branch becomes (right card).</summary>
    public ObservableCollection<RebaseResultItem> Result { get; } = [];

    /// <summary>Branch rebased (mockup: "feature/oauth-refresh").</summary>
    [ObservableProperty]
    public partial string BranchName { get; private set; } = string.Empty;

    /// <summary>What it is replayed on (mockup: "main"), and its short SHA.</summary>
    [ObservableProperty]
    public partial string UpstreamName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UpstreamSha { get; private set; } = string.Empty;

    /// <summary>"6 commits · autosquash : 1 commit fixup! déplacé" (mockup header).</summary>
    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    /// <summary>"6 commits → 4 commits · 1 supprimé" (result card).</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Why the list cannot be applied (a squash with nothing above it…); null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial RebaseRow? SelectedRow { get; set; }

    private string _upstream = string.Empty;
    private int _autosquashed;

    // ---- Opening -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Lists the commits HEAD would replay on <paramref name="upstream"/> (canonical ref name or full SHA, shown as
    /// <paramref name="upstreamName"/>) and shows the editor. Refused (toast) with local changes, a merge commit in the
    /// range, or nothing to replay.
    /// </summary>
    public async Task OpenAsync(string upstream, string upstreamName, GitSha upstreamSha)
    {
        if (!CanOpen) return;
        if (_tree.Status?.Entries.Any(e => !e.IsUntracked) == true)
        {
            _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Rebase.Impossible"),
                Loc.Get("Rebase.CleanTreeRequired"));
            return;
        }

        _loading?.Cancel();
        var loading = _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        IsLoading = true;
        try
        {
            var result = await _git.GetRebaseCommitsAsync(_repositoryPath, upstream, loading.Token);
            if (loading.IsCancellationRequested) return;
            switch (result.Value)
            {
                case Success<IReadOnlyList<RebaseCommit>> { Value: var commits }:
                    if (commits.Count == 0)
                    {
                        _notifier?.Notify(NotificationSeverity.Info, Loc.Get("Rebase.NothingToRebase"), Loc.Format("Rebase.AlreadyContains", CurrentBranch, upstreamName));
                        return;
                    }
                    if (commits.FirstOrDefault(c => c.IsMerge) is { } merge)
                    {
                        _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Rebase.Impossible"),
                            Loc.Format("Rebase.MergeInRange", merge.Sha.ToShortString(), merge.Subject));
                        return;
                    }
                    _upstream = upstream;
                    _commits = commits;
                    BranchName = CurrentBranch;
                    UpstreamName = upstreamName;
                    UpstreamSha = upstreamSha.ToShortString();
                    Fill();
                    IsOpen = true;
                    break;
                case Conflict:
                case AuthenticationFailed:
                    break;
                case Canceled:
                    break;
                case Failure f:
                    _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Rebase.Impossible"), f.Message);
                    break;
            }
        }
        finally
        {
            if (ReferenceEquals(_loading, loading)) IsLoading = false;
        }
    }

    private string CurrentBranch => _tree.Status?.Branch ?? _graph.HeadBranch ?? Loc.Get("Rebase.DetachedHead");

    /// <summary>Rows of the listed commits, autosquash applied.</summary>
    private void Fill()
    {
        _updating = true;
        Rows.Clear();
        var now = _time.GetUtcNow();
        foreach (var commit in _commits)
            Rows.Add(new RebaseRow(commit, RelativeTime.Format(commit.AuthorDate, now, _time.LocalTimeZone)));
        _autosquashed = Autosquash(Rows);
        foreach (var row in Rows) row.PropertyChanged += OnRowChanged;
        _updating = false;
        SelectedRow = Rows.FirstOrDefault();
        Update();
    }

    /// <summary>
    /// Moves each "fixup! x" / "squash! x" commit under its target (the closest commit above whose subject is x, or whose
    /// SHA starts with x), after the ones already attached to it, and presets its action. Returns how many were moved
    /// or preset.
    /// </summary>
    public static int Autosquash(IList<RebaseRow> rows)
    {
        var count = 0;
        foreach (var row in rows.ToList())
        {
            var (action, target) = row.Commit.Subject switch
            {
                var s when s.StartsWith("fixup! ", StringComparison.Ordinal) => (RebaseAction.Fixup, s[7..]),
                var s when s.StartsWith("amend! ", StringComparison.Ordinal) => (RebaseAction.Fixup, s[7..]),
                var s when s.StartsWith("squash! ", StringComparison.Ordinal) => (RebaseAction.Squash, s[8..]),
                _ => (RebaseAction.Pick, null),
            };
            if (target is null) continue;
            target = target.Trim();
            var index = rows.IndexOf(row);
            var targetIndex = -1;
            for (var i = index - 1; i >= 0; i--)
            {
                var candidate = rows[i].Commit;
                if (candidate.Subject == target
                    || target.Length >= 4 && candidate.Sha.ToString().StartsWith(target, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
            if (targetIndex < 0) continue;

            // After the commits already melded into the target.
            var insertAt = targetIndex + 1;
            while (insertAt < index && rows[insertAt].IsMelded) insertAt++;
            if (insertAt != index)
            {
                rows.RemoveAt(index);
                rows.Insert(insertAt, row);
            }
            row.Action = action;
            count++;
        }
        return count;
    }

    // ---- Editing -----------------------------------------------------------------------------------------------

    private bool _updating;

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RebaseRow.Action) or nameof(RebaseRow.Subject)) Update();
    }

    /// <summary>Sets the action of the selected row (keys P, R, E, S, F, D).</summary>
    public void SetAction(RebaseAction action)
    {
        if (SelectedRow is { } row) row.Action = action;
    }

    /// <summary>Moves the selected row up (-1) or down (+1) (Alt+↑ / Alt+↓).</summary>
    public void MoveSelected(int delta)
    {
        if (SelectedRow is not { } row) return;
        var from = Rows.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Rows.Count) return;
        Rows.Move(from, to);
        SelectedRow = row;
    }

    /// <summary>"Réinitialiser": the listed order and actions again (autosquash included).</summary>
    [RelayCommand]
    private void Reset()
    {
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        Fill();
    }

    [RelayCommand]
    public void Close()
    {
        _loading?.Cancel();
        IsOpen = false;
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        Result.Clear();
    }

    /// <summary>Result card, summary and error, after any change of the list.</summary>
    private void Update()
    {
        if (_updating) return;
        Result.Clear();
        string? error = null;
        var kept = new List<(RebaseRow Row, int Count)>();
        foreach (var row in Rows)
        {
            switch (row.Action)
            {
                case RebaseAction.Drop:
                    continue;
                case RebaseAction.Squash or RebaseAction.Fixup:
                    if (kept.Count == 0)
                    {
                        error ??= Loc.Format("Rebase.Error.FirstCommit", row.Action.ToString().ToLowerInvariant());
                        continue;
                    }
                    kept[^1] = (kept[^1].Row, kept[^1].Count + 1);
                    continue;
                default:
                    if (row.Action == RebaseAction.Reword && string.IsNullOrWhiteSpace(row.Subject))
                        error ??= Loc.Format("Rebase.Error.EmptyMessage", row.ShortSha);
                    kept.Add((row, 1));
                    continue;
            }
        }
        if (kept.Count == 0 && Rows.Count > 0) error ??= Loc.Get("Rebase.Error.AllDropped");

        foreach (var (row, count) in kept)
        {
            var detail = count > 1
                ? Loc.Format("Rebase.Result.Combined", count)
                : row.Action switch
                {
                    RebaseAction.Edit => Loc.Get("Rebase.Result.Paused"),
                    RebaseAction.Reword => Loc.Get("Rebase.Result.Reworded"),
                    _ => Loc.Get("Rebase.Result.Unchanged"),
                };
            Result.Add(new RebaseResultItem(row.IsReword ? row.Subject : row.Commit.Subject, detail, row.Action == RebaseAction.Edit));
        }

        var dropped = Rows.Count(r => r.IsDropped);
        Summary = $"{Commits(Rows.Count)} → {Commits(kept.Count)}"
                  + (dropped > 0 ? Loc.Format(dropped > 1 ? "Rebase.Summary.Dropped.Many" : "Rebase.Summary.Dropped.One", dropped) : string.Empty);
        Details = Commits(Rows.Count) + (_autosquashed > 0
            ? Loc.Format(_autosquashed > 1 ? "Rebase.Details.Autosquash.Many" : "Rebase.Details.Autosquash.One", Commits(_autosquashed))
            : string.Empty);
        Error = error;
    }

    private static string Commits(int count) =>
        count == 1 ? Loc.Get("Common.Commits.One") : Loc.Format("Common.Commits.Many", count);

    // ---- Start -------------------------------------------------------------------------------------------------

    private bool CanStart => IsOpen && Error is null && Rows.Count > 0;

    /// <summary>"Démarrer le rebase".</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart) return;
        var todo = Rows.Select(r => new RebaseTodoItem(r.Commit.Sha, r.Action, r.IsReword ? r.RewordMessage : null)).ToList();
        // Nothing changed: no rebase at all (git would rewrite nothing, but VIGO would record an empty operation).
        if (todo.Select(t => t.Commit).SequenceEqual(_commits.Select(c => c.Sha))
            && todo.All(t => t.Action == RebaseAction.Pick || t.Action == RebaseAction.Reword && t.Message == FindCommit(t.Commit)?.Message))
        {
            Close();
            _notifier?.Notify(NotificationSeverity.Info, Loc.Get("Rebase.NothingToChange"), Loc.Get("Rebase.ListUnchanged"));
            return;
        }

        var branch = BranchName;
        var upstreamName = UpstreamName;
        var done = await Actions.RunAsync(Loc.Format("Rebase.Failed", branch),
            ct => _git.InteractiveRebaseAsync(_repositoryPath, _upstream, todo, ct),
            outcome =>
            {
                if (outcome == RebaseOutcome.Stopped)
                {
                    _notifier?.Notify(NotificationSeverity.Info, Loc.Get("Rebase.Stopped.Title"),
                        Loc.Get("Rebase.Stopped.Message"));
                }
                else
                {
                    _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Rebase.Done.Title"), Loc.Format("Rebase.Done.Message", branch, upstreamName));
                }
            },
            conflict =>
            {
                var count = conflict.Paths.Count == 1 ? Loc.Get("Common.ConflictCount.One") : Loc.Format("Common.ConflictCount.Many", conflict.Paths.Count);
                _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Common.ConflictDetected"),
                    Loc.Format("Rebase.ConflictStopped", branch, count));
            },
            new UndoStep(Loc.Format("Rebase.UndoStep", branch)));

        // Stopped (edit, conflicts): the list no longer applies; the banner goes on.
        if (done || _tree.Status?.Operation is not null) Close();
        if (!_lifetime.IsCancellationRequested) await _graph.RefreshAsync();
        if (_tree.Status?.Operation is not null) _graph.SelectWorkInProgress();
    }

    private RebaseCommit? FindCommit(GitSha sha) => _commits.FirstOrDefault(c => c.Sha == sha);
}
