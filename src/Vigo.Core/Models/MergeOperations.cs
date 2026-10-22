namespace Vigo.Core.Models;

/// <summary>What <c>IGitService.MergeAsync</c> did.</summary>
public enum MergeOutcome
{
    /// <summary>Nothing to merge: the source is already reachable from HEAD.</summary>
    UpToDate,

    /// <summary>HEAD moved forward to the source, no commit created.</summary>
    FastForward,

    /// <summary>A merge commit was created.</summary>
    MergeCommit,
}

/// <summary>What <c>IGitService.RebaseAsync</c> did.</summary>
public enum RebaseOutcome
{
    /// <summary>The branch already contained the upstream: nothing replayed.</summary>
    UpToDate,

    /// <summary>The commits of the branch were replayed on the upstream.</summary>
    Rebased,

    /// <summary>
    /// Interactive rebase stopped without conflict (an <see cref="RebaseAction.Edit"/> step, or a reword whose message
    /// could not be applied): it stays in progress (<see cref="WorkingTreeStatus.Operation"/>) until continued or aborted.
    /// </summary>
    Stopped,
}

/// <summary>Kind of operation stopped half-way in a repository (conflicts, or a step waiting for the user).</summary>
public enum RepositoryOperationKind
{
    Merge,
    Rebase,
    CherryPick,
    Revert,

    /// <summary>A patch mailbox being applied (<c>git am</c>, J7.12): stopped on conflicts or waiting for the user.</summary>
    ApplyMailbox,
}

/// <summary>
/// Operation in progress in the repository, read from the git directory (<c>MERGE_HEAD</c>, <c>rebase-merge/</c>,
/// <c>rebase-apply/</c>, <c>CHERRY_PICK_HEAD</c>, <c>REVERT_HEAD</c>).
/// </summary>
/// <param name="Source">
/// Merge: what is merged, from <c>MERGE_MSG</c> ("main", "origin/main", a short SHA); rebase: the commit the branch is
/// replayed on (full SHA, see <see cref="Onto"/>); cherry-pick / revert: the commit applied (full SHA); null when unknown.
/// </param>
/// <param name="Branch">Branch being merged into or rebased; null when unknown (detached HEAD).</param>
/// <param name="Onto">Rebase: commit the branch is replayed on.</param>
/// <param name="Step">Rebase: number of the commit being replayed (1-based); 0 otherwise.</param>
/// <param name="StepCount">Rebase: number of commits to replay; 0 otherwise.</param>
/// <param name="Message">Merge, cherry-pick, revert: prepared commit message (<c>MERGE_MSG</c>, comments removed); null otherwise.</param>
/// <param name="IsEditStop">
/// Rebase stopped on an <c>edit</c> step (<c>rebase-merge/amend</c>): HEAD is the commit to change, new commits and
/// amends are allowed before going on.
/// </param>
public sealed record RepositoryOperation(
    RepositoryOperationKind Kind,
    string? Source = null,
    string? Branch = null,
    GitSha? Onto = null,
    int Step = 0,
    int StepCount = 0,
    string? Message = null,
    bool IsEditStop = false);
