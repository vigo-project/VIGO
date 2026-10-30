namespace Vigo.Core.Models;

/// <summary>Lifecycle of a pull request.</summary>
public enum PullRequestState
{
    Open,
    Closed,
    Merged,
}

/// <summary>What an entry of the conversation of a pull request is.</summary>
public enum TimelineKind
{
    /// <summary>Comment of the discussion.</summary>
    Comment,

    /// <summary>Review (with or without a decision and a text).</summary>
    Review,

    /// <summary>Comment on a line of the diff.</summary>
    ReviewComment,

    /// <summary>Something that happened to an issue (J6.10a): the text is in <see cref="TimelineEntry.Body"/>, without Markdown.</summary>
    Event,
}

/// <summary>Decision of a review.</summary>
public enum ReviewVerdict
{
    /// <summary>Not a review, or a review without a decision.</summary>
    None,

    Commented,
    Approved,
    ChangesRequested,
    Dismissed,
}

/// <summary>Entry of the conversation: a comment, a review, or a comment on a line of the diff.</summary>
/// <param name="Path">File a review comment is about; null otherwise.</param>
/// <param name="Line">Line of the file in the new version; null when the comment is outdated or not about a line.</param>
/// <param name="DiffHunk">Lines of the diff around a review comment, as the service reports them.</param>
/// <param name="Id">Identifier of a discussion comment on the service (to edit or delete it); 0 when unknown or not editable.</param>
/// <param name="Reactions">Reactions to the comment, only the kinds somebody used (J6.9d); null when unknown.</param>
public sealed record TimelineEntry(
    TimelineKind Kind, string Author, string Body, DateTimeOffset CreatedAt,
    ReviewVerdict Verdict = ReviewVerdict.None, string? Path = null, int? Line = null, string? DiffHunk = null, long Id = 0,
    IReadOnlyList<ReactionCount>? Reactions = null);

/// <summary>Reaction to a comment (the eight the hosting service offers).</summary>
public enum ReactionKind
{
    ThumbsUp,
    ThumbsDown,
    Laugh,
    Hooray,
    Confused,
    Heart,
    Rocket,
    Eyes,
}

/// <summary>How many people reacted to a comment with <paramref name="Kind"/>.</summary>
public sealed record ReactionCount(ReactionKind Kind, int Count);

/// <summary>Side of a diff a review comment is attached to.</summary>
public enum ReviewSide
{
    /// <summary>The new version of the file (added and unchanged lines).</summary>
    New,

    /// <summary>The old version of the file (deleted lines).</summary>
    Old,
}

/// <summary>Comment of a discussion thread on the diff.</summary>
/// <param name="Id">Identifier on the service (to reply to it).</param>
/// <param name="Reactions">Reactions to the comment, only the kinds somebody used; null when the service gives none.</param>
public sealed record ReviewThreadComment(long Id, string Author, string Body, DateTimeOffset CreatedAt, IReadOnlyList<ReactionCount>? Reactions = null);

/// <summary>Discussion on a line of the diff (J6.9e): the comments in order, and whether it was resolved.</summary>
/// <param name="Id">Identifier of the thread in the service's graph API (to resolve it).</param>
/// <param name="Line">Last line of the file the thread is about; null when the code changed since (outdated).</param>
/// <param name="StartLine">First line of a thread on several lines; null for one line.</param>
/// <param name="StartColumn">Column (1-based) where the commented text starts on its first line; null when the service gives none.</param>
/// <param name="EndColumn">Column (1-based, exclusive) where the commented text ends on its last line; null when the service gives none.</param>
public sealed record ReviewThread(
    string Id, string Path, int? Line, ReviewSide Side, bool IsResolved, bool IsOutdated, IReadOnlyList<ReviewThreadComment> Comments,
    int? StartLine = null, int? StartColumn = null, int? EndColumn = null);

/// <summary>Comment on a line of the diff, waiting to be sent with a review (J6.9e).</summary>
/// <param name="Line">Last line number on the <paramref name="Side"/> version of <paramref name="Path"/>.</param>
/// <param name="StartLine">First line of a comment on several lines; null for one line.</param>
/// <param name="StartColumn">Column (1-based) where the commented text starts on its first line, when the comment is about a selection.</param>
/// <param name="EndColumn">Column (1-based, exclusive) where the commented text ends on its last line.</param>
public sealed record NewReviewComment(
    string Path, int Line, ReviewSide Side, string Body, int? StartLine = null, int? StartColumn = null, int? EndColumn = null);

/// <summary>What a review decides.</summary>
public enum ReviewDecision
{
    Comment,
    Approve,
    RequestChanges,
}

/// <summary>Vote of a reviewer on a pull request, for the services that have votes instead of review decisions (Azure DevOps, J6.9h).</summary>
public enum ReviewVote
{
    /// <summary>No vote: removes the reviewer's vote.</summary>
    None,

    Approved,

    ApprovedWithSuggestions,

    WaitingForAuthor,

    Rejected,
}

/// <summary>Commit of a pull request.</summary>
public sealed record PullRequestCommit(string Sha, string Subject, string Author, DateTimeOffset Date);

/// <summary>File changed by a pull request, with its patch when the service gives one (not for binary or huge files).</summary>
public sealed record PullRequestFile(string Path, string? OldPath, FileChangeKind Kind, int Additions, int Deletions, string? Patch);

/// <summary>Everything the pull request screen shows (J6.9a, read only).</summary>
/// <param name="Summary">The pull request as listed.</param>
/// <param name="Body">Description (Markdown).</param>
/// <param name="Timeline">Comments, reviews and line comments, oldest first.</param>
/// <param name="IsTruncated">A list was cut at the page limit: the screen says to continue in the browser.</param>
/// <param name="NodeId">Identifier of the pull request in the service's graph API (draft state changes need it); null when unknown.</param>
/// <param name="IsBehind">The head branch lacks commits of the base branch (the service can update it, J6.9c).</param>
/// <param name="AutoMerge">Method of the automatic merge that is waiting for the checks and reviews (J6.9d); null when not enabled.</param>
/// <param name="Threads">Discussions on lines of the diff (J6.9e); empty when the service gives none.</param>
public sealed record PullRequestDetail(
    PullRequest Summary, string Body, PullRequestState State, IReadOnlyList<string> Labels,
    IReadOnlyList<TimelineEntry> Timeline, IReadOnlyList<PullRequestCommit> Commits, IReadOnlyList<PullRequestFile> Files,
    bool IsTruncated = false, string? NodeId = null, bool IsBehind = false, MergeMethod? AutoMerge = null,
    IReadOnlyList<ReviewThread>? Threads = null)
{
    /// <summary>Discussions on lines of the diff, never null.</summary>
    public IReadOnlyList<ReviewThread> ReviewThreads => Threads ?? [];

    public int Additions => Files.Sum(f => f.Additions);

    public int Deletions => Files.Sum(f => f.Deletions);
}

/// <summary>How a pull request is merged.</summary>
public enum MergeMethod
{
    /// <summary>Merge commit.</summary>
    Merge,

    Squash,

    Rebase,
}
