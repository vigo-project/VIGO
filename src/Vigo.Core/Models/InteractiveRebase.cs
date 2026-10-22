namespace Vigo.Core.Models;

/// <summary>Command of a commit in an interactive rebase (line of the todo list of <c>git rebase -i</c>).</summary>
public enum RebaseAction
{
    /// <summary>Kept as is.</summary>
    Pick,

    /// <summary>Kept with a new message.</summary>
    Reword,

    /// <summary>Kept, then the rebase stops so that the commit can be changed (amend, new commits) before going on.</summary>
    Edit,

    /// <summary>Melded into the kept commit before it; both messages are combined.</summary>
    Squash,

    /// <summary>Melded into the kept commit before it; its message is discarded.</summary>
    Fixup,

    /// <summary>Removed from the branch.</summary>
    Drop,
}

/// <summary>A commit an interactive rebase would replay (<c>IGitService.GetRebaseCommitsAsync</c>).</summary>
/// <param name="Message">Full message (subject, blank line, body), trailing newlines removed.</param>
/// <param name="ParentCount">More than one: a merge commit, which the interactive rebase of VIGO refuses (ADR-041).</param>
public sealed record RebaseCommit(
    GitSha Sha,
    string Subject,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    int ParentCount)
{
    public bool IsMerge => ParentCount > 1;
}

/// <summary>A line of the todo list given to <c>IGitService.InteractiveRebaseAsync</c>, in application order.</summary>
/// <param name="Message">New full message of a <see cref="RebaseAction.Reword"/>; ignored for the other actions.</param>
public sealed record RebaseTodoItem(GitSha Commit, RebaseAction Action, string? Message = null);
