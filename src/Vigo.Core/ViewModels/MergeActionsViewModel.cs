using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>How "Fusionner X dans Y" is carried out.</summary>
public enum MergeMode
{
    /// <summary>Y is checked out: <c>git merge X</c> (fast-forward or merge commit, as merge.ff says).</summary>
    IntoHead,

    /// <summary>Y is not checked out and can move forward to X: Y is moved without touching the working directory.</summary>
    FastForwardBranch,

    /// <summary>Y is not checked out and diverged from X: checkout of Y, then merge.</summary>
    CheckoutThenMerge,

    /// <summary>Y already contains X: nothing to merge.</summary>
    UpToDate,
}

/// <summary>Merge of <see cref="Source"/> into the local branch <see cref="Target"/>.</summary>
public sealed record MergePlan(GitRef Source, GitRef Target, MergeMode Mode)
{
    /// <summary>Menu entry (mockup: "Fusionner x dans y").</summary>
    public string Label => Mode switch
    {
        MergeMode.FastForwardBranch => Loc.Format("Merge.Plan.FastForward", Target.Name, Source.Name),
        MergeMode.CheckoutThenMerge => Loc.Format("Merge.Plan.CheckoutThenMerge", Target.Name, Source.Name),
        MergeMode.UpToDate => Loc.Format("Merge.Plan.UpToDate", Target.Name, Source.Name),
        _ => Loc.Format("Merge.Plan.Merge", Source.Name, Target.Name),
    };
}

/// <summary>Rebase of the local branch <see cref="Branch"/> on <see cref="Upstream"/>.</summary>
public sealed record RebasePlan(GitRef Branch, GitRef Upstream)
{
    /// <summary>Menu entry (mockup: "Rebaser y sur x").</summary>
    public string Label => Loc.Format("Merge.Plan.Rebase", Branch.Name, Upstream.Name);

    /// <summary>Menu entry of the interactive rebase (J5.5).</summary>
    public string InteractiveLabel => Loc.Format("Merge.Plan.RebaseInteractive", Branch.Name, Upstream.Name);
}

/// <summary>Entries of the menu shown when a branch is dropped on another one (mockup: dropMenu).</summary>
public sealed record DropMenu(string Title, MergePlan? Merge, RebasePlan? Rebase, GitRef? Source = null, GitRef? Target = null);

/// <summary>
/// Merge and rebase of a repository tab (J4.3): menus of the left panel and of the graph, drag and drop of a branch on
/// another one, and the banner of an operation stopped on conflicts (abort, continue, skip).
/// </summary>
/// <remarks>
/// Operations run through <see cref="StagingActions"/> (one at a time with the index operations); the refs are read
/// again after each one. Conflicts are resolved in the working directory panel ("Marquer comme résolu") until the
/// conflict editor of J5.
/// </remarks>
public sealed partial class MergeActionsViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly WorkingTreeViewModel _tree;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;

    public MergeActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, WorkingTreeViewModel tree, IUserNotifier? notifier, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _tree = tree;
        _notifier = notifier;
        _lifetime = lifetime;
        _tree.StatusChanged += OnStatusChanged;
    }

    private StagingActions Actions => _tree.Actions;

    /// <summary>Merge and rebase need a working directory (not a bare repository) and no operation in progress.</summary>
    public bool CanStart => _tree.IsAvailable && Operation is null;

    /// <summary>Merge, rebase, cherry-pick or revert stopped half-way (banner of the working directory panel).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperation), nameof(IsRebase), nameof(CanContinue), nameof(OperationTitle), nameof(OperationDetail),
        nameof(AbortLabel), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand), nameof(ContinueCommand), nameof(SkipCommand))]
    public partial RepositoryOperation? Operation { get; private set; }

    public bool HasOperation => Operation is not null;

    public bool IsRebase => Operation?.Kind == RepositoryOperationKind.Rebase;

    /// <summary>
    /// "Continuer" and "Passer ce commit" apply: rebase, cherry-pick or revert. A merge is concluded by the commit form
    /// (a cherry-pick or revert can be too).
    /// </summary>
    public bool CanContinue => Operation?.Kind is RepositoryOperationKind.Rebase or RepositoryOperationKind.CherryPick or RepositoryOperationKind.Revert
        or RepositoryOperationKind.ApplyMailbox;

    private int ConflictCount => _tree.Conflicted.Count;

    /// <summary>
    /// "Fusion de main dans feature/x." (mockup), "Rebase de feature/x sur main · commit 2 sur 5.",
    /// "Cherry-pick de a1b2c3d « Fix cache »."
    /// </summary>
    public string OperationTitle => Operation switch
    {
        null => string.Empty,
        { Kind: RepositoryOperationKind.Merge } merge =>
            Loc.Format("Merge.Title.Merge", merge.Source ?? "?", merge.Branch ?? _tree.Status?.Branch ?? "HEAD"),
        { Kind: RepositoryOperationKind.Rebase } rebase => rebase.StepCount > 0
            ? Loc.Format("Merge.Title.RebaseStep", rebase.Branch ?? "HEAD", NameOf(rebase.Onto), rebase.Step, rebase.StepCount)
            : Loc.Format("Merge.Title.Rebase", rebase.Branch ?? "HEAD", NameOf(rebase.Onto)),
        { Kind: RepositoryOperationKind.CherryPick } pick => Loc.Format("Merge.Title.CherryPick", DescribeCommit(pick.Source)),
        { Kind: RepositoryOperationKind.ApplyMailbox } mailbox => MailboxTitle(mailbox),
        var revert => Loc.Format("Merge.Title.Revert", DescribeCommit(revert.Source)),
    };

    /// <summary>"Application d'un patch · 2 sur 5 : Fix cache" (Subject of the patch git am stopped on).</summary>
    private static string MailboxTitle(RepositoryOperation mailbox) => (mailbox.StepCount > 1, mailbox.Message is { Length: > 0 }) switch
    {
        (true, true) => Loc.Format("Merge.Title.MailboxStepSubject", mailbox.Step, mailbox.StepCount, mailbox.Message!),
        (true, false) => Loc.Format("Merge.Title.MailboxStep", mailbox.Step, mailbox.StepCount),
        (false, true) => Loc.Format("Merge.Title.MailboxSubject", mailbox.Message!),
        _ => Loc.Get("Merge.Title.Mailbox"),
    };

    /// <summary>What is left to do (mockup: "3 fichiers en conflit. Résolvez-les puis validez le commit de fusion.").</summary>
    public string OperationDetail
    {
        get
        {
            if (Operation is null) return string.Empty;
            var conflicts = ConflictCount switch
            {
                0 => null,
                1 => Loc.Get("Common.FileConflicts.One"),
                var n => Loc.Format("Common.FileConflicts.Many", n),
            };
            return (Operation.Kind, conflicts) switch
            {
                (RepositoryOperationKind.Rebase, null) when Operation.IsEditStop =>
                    Loc.Get("Merge.Detail.RebaseEdit"),
                (RepositoryOperationKind.Rebase, null) => Loc.Get("Merge.Detail.RebaseResolved"),
                (RepositoryOperationKind.Rebase, { } c) => Loc.Format("Merge.Detail.ConflictsThenContinue", c),
                (RepositoryOperationKind.Merge, null) => Loc.Get("Merge.Detail.MergeResolved"),
                (RepositoryOperationKind.Merge, { } c) => Loc.Format("Merge.Detail.MergeConflicts", c),
                (_, null) => Loc.Get("Merge.Detail.OtherResolved"),
                (_, { } c) => Loc.Format("Merge.Detail.ConflictsThenContinue", c),
            };
        }
    }

    /// <summary>Text of the work-in-progress row of the graph during an operation (mockup: "Fusion de main en cours · 3 conflits").</summary>
    public string? WorkInProgressText
    {
        get
        {
            var operation = _tree.Status?.Operation;
            if (operation is null) return null;
            var what = operation.Kind switch
            {
                RepositoryOperationKind.Merge => Loc.Format("Merge.Wip.Merge", operation.Source ?? "?"),
                RepositoryOperationKind.Rebase => Loc.Format("Merge.Wip.Rebase", NameOf(operation.Onto)),
                RepositoryOperationKind.CherryPick => Loc.Format("Merge.Wip.CherryPick", ShortSha(operation.Source)),
                RepositoryOperationKind.ApplyMailbox => Loc.Get("Merge.Wip.ApplyMailbox"),
                _ => Loc.Format("Merge.Wip.Revert", ShortSha(operation.Source)),
            };
            return ConflictCount switch
            {
                0 => what,
                1 => Loc.Format("Merge.Wip.ConflictOne", what),
                var n => Loc.Format("Merge.Wip.ConflictMany", what, n),
            };
        }
    }

    /// <summary>
    /// What "ours" and "theirs" stand for in the conflict editor (mockup: "HEAD · feature/oauth-refresh", "main ·
    /// e41b7a2"). During a rebase, ours is the upstream being built on and theirs the commit being replayed.
    /// </summary>
    public (string Ours, string Theirs) ConflictSides
    {
        get
        {
            var head = Loc.Format("Merge.Sides.Head", _tree.Status?.Branch ?? _tree.Status?.Head?.ToShortString() ?? "?");
            return Operation switch
            {
                null => (head, Loc.Get("Merge.Sides.Applied")),
                { Kind: RepositoryOperationKind.Merge } merge => (head, merge.Source ?? "?"),
                { Kind: RepositoryOperationKind.Rebase } rebase => (
                    Loc.Format("Merge.Sides.RebaseBase", NameOf(rebase.Onto)),
                    rebase.StepCount > 0
                        ? Loc.Format("Merge.Sides.RebaseStep", rebase.Branch ?? "HEAD", rebase.Step, rebase.StepCount)
                        : rebase.Branch ?? "HEAD"),
                { Kind: RepositoryOperationKind.CherryPick } pick => (head, DescribeCommit(pick.Source)),
                { Kind: RepositoryOperationKind.ApplyMailbox } => (head, Loc.Get("Merge.Sides.Patch")),
                var revert => (head, Loc.Format("Merge.Sides.Revert", DescribeCommit(revert.Source))),
            };
        }
    }

    public string AbortLabel => Operation?.Kind switch
    {
        RepositoryOperationKind.Merge => Loc.Get("Merge.Abort.Merge"),
        RepositoryOperationKind.Rebase => Loc.Get("Merge.Abort.Rebase"),
        RepositoryOperationKind.CherryPick => Loc.Get("Merge.Abort.CherryPick"),
        RepositoryOperationKind.Revert => Loc.Get("Merge.Abort.Revert"),
        RepositoryOperationKind.ApplyMailbox => Loc.Get("Merge.Abort.ApplyMailbox"),
        _ => Loc.Get("Merge.Abort.Other"),
    };

    /// <summary>Name of an operation in toast titles: "Fusion", "Rebase", "Cherry-pick", "Revert".</summary>
    private static string KindName(RepositoryOperationKind kind) => kind switch
    {
        RepositoryOperationKind.Merge => Loc.Get("Merge.Kind.Merge"),
        RepositoryOperationKind.Rebase => Loc.Get("Merge.Kind.Rebase"),
        RepositoryOperationKind.CherryPick => Loc.Get("Merge.Kind.CherryPick"),
        RepositoryOperationKind.ApplyMailbox => Loc.Get("Merge.Kind.ApplyMailbox"),
        _ => Loc.Get("Merge.Kind.Revert"),
    };

    // ---- Menus -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Entries of a branch row menu (mockup): "Fusionner x dans &lt;branche extraite&gt;" and "Rebaser &lt;branche extraite&gt;
    /// sur x". Null when they do not apply (no branch checked out, x is the checked-out branch, operation in progress).
    /// </summary>
    public (MergePlan Merge, RebasePlan Rebase)? HeadMenuFor(GitRef reference)
    {
        if (!CanStart || reference.Kind is not (GitRefKind.LocalBranch or GitRefKind.RemoteBranch) || reference.IsCurrent) return null;
        if (CurrentBranch is not { } current) return null;
        return (new MergePlan(reference, current, MergeMode.IntoHead), new RebasePlan(current, reference));
    }

    /// <summary>
    /// Menu of a branch dropped on a local branch (mockup: dropMenu). The merge says what it will do: merge into the
    /// checked-out branch, move the target forward, check it out first, or nothing (already merged). The rebase is
    /// offered for a local source only. Null when the drop does nothing.
    /// </summary>
    public async Task<DropMenu?> DropMenuAsync(GitRef source, GitRef target)
    {
        if (!CanStart || target.Kind != GitRefKind.LocalBranch || source.CanonicalName == target.CanonicalName
            || source.Kind is not (GitRefKind.LocalBranch or GitRefKind.RemoteBranch))
        {
            return null;
        }

        var merge = new MergePlan(source, target, await MergeModeAsync(source, target));
        var rebase = source.Kind == GitRefKind.LocalBranch ? new RebasePlan(source, target) : null;
        return new DropMenu(Loc.Format("Merge.DropTitle", source.Name, target.Name), merge, rebase, source, target);
    }

    private async Task<MergeMode> MergeModeAsync(GitRef source, GitRef target)
    {
        if (await IsAncestorAsync(source.Target, target.Target) == true) return MergeMode.UpToDate;
        if (target.IsCurrent) return MergeMode.IntoHead;
        // merge.ff=false asks for a merge commit every time: moving the branch forward would not be what the user expects.
        var alwaysMergeCommit = (await _git.GetConfigValueAsync(_repositoryPath, "merge.ff", _lifetime)).Value is Success<string?> { Value: { } value }
            && value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        return !alwaysMergeCommit && await IsAncestorAsync(target.Target, source.Target) == true ? MergeMode.FastForwardBranch : MergeMode.CheckoutThenMerge;
    }

    // ---- Operations ------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task MergeAsync(MergePlan plan)
    {
        if (!CanStart || plan.Mode == MergeMode.UpToDate)
        {
            if (plan.Mode == MergeMode.UpToDate)
                _notifier?.Notify(NotificationSeverity.Info, Loc.Get("Merge.UpToDate.Title"), Loc.Format("Merge.UpToDate.Detail", plan.Target.Name, plan.Source.Name));
            return;
        }

        var (source, target) = (plan.Source, plan.Target);
        var failureTitle = Loc.Format("Merge.FailTitle", source.Name, target.Name);
        if (plan.Mode == MergeMode.FastForwardBranch)
        {
            if (await Actions.RunAsync(failureTitle, ct => _git.FastForwardBranchAsync(_repositoryPath, target.Name, source.Target, ct),
                    new UndoStep(Loc.Format("Merge.Undo.FastForward", target.Name, source.Name))))
            {
                _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Merge.FastForwarded.Title"),
                    Loc.Format("Merge.FastForwarded.DetailSha", target.Name, source.Name, source.Target.ToShortString()));
            }
            await RefreshRefsAsync();
            return;
        }

        await Actions.RunAsync(failureTitle, async Task<GitResult<MergeOutcome>> (CancellationToken ct) =>
        {
            if (plan.Mode == MergeMode.CheckoutThenMerge)
            {
                var checkout = await _git.CheckoutAsync(_repositoryPath, new CheckoutBranch(target.Name), ct);
                switch (checkout)
                {
                    case Done: break;
                    case Conflict c: return c;
                    case AuthenticationFailed a: return a;
                    case Canceled c: return c;
                    case Failure f: return f;
                }
            }
            return await _git.MergeAsync(_repositoryPath, source.CanonicalName, ct);
        }, outcome => _notifier?.Notify(outcome == MergeOutcome.UpToDate ? NotificationSeverity.Info : NotificationSeverity.Success, outcome switch
        {
            MergeOutcome.UpToDate => Loc.Get("Merge.UpToDate.Title"),
            MergeOutcome.FastForward => Loc.Get("Merge.FastForwarded.Title"),
            _ => Loc.Get("Merge.Merged.Title"),
        }, outcome switch
        {
            MergeOutcome.UpToDate => Loc.Format("Merge.UpToDate.Detail", target.Name, source.Name),
            MergeOutcome.FastForward => Loc.Format("Merge.FastForwarded.Detail", target.Name, source.Name),
            _ => Loc.Format("Merge.Merged.Detail", source.Name, target.Name),
        }), conflict => NotifyConflict(conflict, Loc.Format("Merge.Conflict.PrefixMerge", source.Name)),
            new UndoStep(Loc.Format("Merge.Undo.Merge", source.Name, target.Name)));
        await RefreshRefsAsync();
        ShowStoppedOperation();
    }

    [RelayCommand]
    private async Task RebaseAsync(RebasePlan plan)
    {
        if (!CanStart) return;
        var (branch, upstream) = (plan.Branch, plan.Upstream);
        await Actions.RunAsync(Loc.Format("Merge.Rebase.FailTitle", branch.Name, upstream.Name),
            ct => _git.RebaseAsync(_repositoryPath, upstream.CanonicalName, branch.IsCurrent ? null : branch.Name, ct),
            outcome => _notifier?.Notify(outcome == RebaseOutcome.UpToDate ? NotificationSeverity.Info : NotificationSeverity.Success,
                outcome == RebaseOutcome.UpToDate ? Loc.Get("Merge.Rebase.UpToDateTitle") : Loc.Get("Merge.Rebase.DoneTitle"),
                outcome == RebaseOutcome.UpToDate
                    ? Loc.Format("Merge.UpToDate.Detail", branch.Name, upstream.Name)
                    : Loc.Format("Merge.Rebase.DoneDetail", branch.Name, upstream.Name)),
            conflict => NotifyConflict(conflict, Loc.Format("Merge.Conflict.PrefixRebase", branch.Name)),
            new UndoStep(Loc.Format("Merge.Undo.Rebase", branch.Name, upstream.Name)));
        await RefreshRefsAsync();
        ShowStoppedOperation();
    }

    /// <summary>"Abandonner la fusion" / "Abandonner le rebase": back to the state before the operation.</summary>
    [RelayCommand(CanExecute = nameof(HasOperation))]
    private async Task AbortAsync()
    {
        if (Operation is not { } operation) return;
        if (await Actions.RunAsync(Loc.Format("Merge.Abort.Failed", AbortLabel), ct => _git.AbortOperationAsync(_repositoryPath, operation.Kind, ct),
                new UndoStep(AbortLabel)))
        {
            _notifier?.Notify(NotificationSeverity.Info,
                operation.Kind == RepositoryOperationKind.Merge ? Loc.Get("Merge.Aborted.Merge") : Loc.Format("Merge.Aborted.Other", KindName(operation.Kind)),
                Loc.Get("Merge.Aborted.Detail"));
        }
        await RefreshRefsAsync();
    }

    /// <summary>"Continuer" a stopped rebase, cherry-pick or revert once its conflicts are marked as resolved.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        if (!CanContinue || Operation is not { } operation) return;
        // Rebase, cherry-pick, revert: all masculine in French.
        await RunStepAsync(operation, Loc.Format("Merge.Continue.Failed", KindName(operation.Kind).ToLowerInvariant()),
            ct => _git.ContinueOperationAsync(_repositoryPath, operation.Kind, ct));
    }

    /// <summary>"Passer ce commit": drops the commit the rebase, cherry-pick or revert stopped on.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task SkipAsync()
    {
        if (!CanContinue || Operation is not { } operation) return;
        await RunStepAsync(operation, Loc.Get("Merge.Skip.Failed"), ct => _git.SkipOperationAsync(_repositoryPath, operation.Kind, ct));
    }

    private async Task RunStepAsync(RepositoryOperation operation, string failureTitle, Func<CancellationToken, Task<GitResult>> step)
    {
        var branch = operation.Branch;
        var isRebase = operation.Kind == RepositoryOperationKind.Rebase;
        var done = await Actions.RunAsync<bool>(failureTitle, async ct => (await step(ct)).Value switch
        {
            Done => new Success<bool>(true),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
            _ => new Failure(Loc.Get("Common.UnexpectedResult")),
        }, _ => { }, conflict => NotifyConflict(conflict, Loc.Format("Merge.Conflict.PrefixAgain", KindName(operation.Kind).ToLowerInvariant())),
            // Concluded: recorded as the operation started in VIGO (see UndoHistory.BeginAsync).
            new UndoStep(KindName(operation.Kind)));
        if (done && Operation is null)
        {
            _notifier?.Notify(NotificationSeverity.Success, Loc.Format("Merge.Step.Done", KindName(operation.Kind)), isRebase
                ? branch is null ? null : Loc.Format("Merge.Step.RebaseDetail", branch)
                : operation.Kind == RepositoryOperationKind.ApplyMailbox ? null : Loc.Format("Merge.Step.CommitCreated", DescribeCommit(operation.Source)));
        }
        await RefreshRefsAsync();
    }

    public void Dispose() => _tree.StatusChanged -= OnStatusChanged;

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private GitRef? CurrentBranch => _graph.Refs?.OfKind(GitRefKind.LocalBranch).FirstOrDefault(r => r.IsCurrent);

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        Operation = _tree.Status?.Operation;
        // The conflict count changes without the operation changing.
        OnPropertyChanged(nameof(OperationDetail));
    }

    private void NotifyConflict(Conflict conflict, string prefix)
    {
        var count = conflict.Paths.Count == 1 ? Loc.Get("Common.ConflictCount.One") : Loc.Format("Common.ConflictCount.Many", conflict.Paths.Count);
        _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Common.ConflictDetected"), Loc.Format("Merge.Conflict.Detail", prefix, count));
    }

    private async Task<bool?> IsAncestorAsync(GitSha ancestor, GitSha descendant) =>
        (await _git.IsAncestorAsync(_repositoryPath, ancestor, descendant, _lifetime)).Value is Success<bool> s ? s.Value : null;

    /// <summary>"main" for the commit main points at (local branch first, then remote branch, tag), else its short SHA.</summary>
    private string NameOf(GitSha? sha)
    {
        if (sha is not { } commit) return "?";
        var refs = _graph.Refs?.Refs ?? [];
        return (refs.FirstOrDefault(r => r.Kind == GitRefKind.LocalBranch && r.Target == commit)
                ?? refs.FirstOrDefault(r => r.Kind == GitRefKind.RemoteBranch && r.Target == commit)
                ?? refs.FirstOrDefault(r => r.Kind == GitRefKind.Tag && r.Target == commit))?.Name
               ?? commit.ToShortString();
    }

    /// <summary>Stopped on conflicts: the work-in-progress row is selected, so that the banner and the conflicted files show.</summary>
    private void ShowStoppedOperation()
    {
        if (_tree.Status?.Operation is not null) _graph.SelectWorkInProgress();
    }

    /// <summary>"a1b2c3d" for a full SHA read from the git directory; "?" when unknown.</summary>
    private static string ShortSha(string? sha) => sha is not null && GitSha.TryParse(sha, out var commit) ? commit.ToShortString() : "?";

    /// <summary>"a1b2c3d « Fix cache »" when the commit is in the graph, else its short SHA.</summary>
    private string DescribeCommit(string? sha) =>
        sha is not null && GitSha.TryParse(sha, out var commit) && _graph.FindCommit(commit) is { } info
            ? Loc.Format("Common.CommitWithSubject", commit.ToShortString(), info.Subject)
            : ShortSha(sha);

    private async Task RefreshRefsAsync()
    {
        if (!_lifetime.IsCancellationRequested) await _graph.RefreshAsync();
    }
}
