namespace Vigo.Core.Models;

/// <summary>Where a work item stands in its workflow, whatever the process template names its states.</summary>
public enum WorkItemStateKind
{
    /// <summary>New, To Do, Proposed…</summary>
    Proposed,

    /// <summary>Active, Doing, In Progress, Committed…</summary>
    InProgress,

    /// <summary>Resolved, In Review…</summary>
    Resolved,

    /// <summary>Closed, Done…</summary>
    Completed,

    /// <summary>Removed, Cut…</summary>
    Removed,
}

/// <summary>One labelled value of a work item ("Priority" / "2"), already formatted for display.</summary>
public sealed record WorkItemField(string Label, string Value);

/// <summary>A titled group of fields (planning, people, dates…).</summary>
public sealed record WorkItemSection(string Title, IReadOnlyList<WorkItemField> Fields);

/// <summary>A rich text field of a work item (description, acceptance criteria, repro steps…), as Markdown.</summary>
public sealed record WorkItemText(string Title, string Markdown);

/// <summary>What a link of a work item points to.</summary>
public enum WorkItemLinkKind
{
    Parent,
    Child,
    Related,
    Predecessor,
    Successor,
    Duplicate,
    Tests,
    PullRequest,
    Commit,
    Branch,
    Build,
    Hyperlink,
    Other,
}

/// <summary>
/// Link of a work item. <paramref name="WorkItemId"/> is set when it points to another work item (the screen then opens it);
/// <paramref name="Type"/> and <paramref name="State"/> describe that work item.
/// </summary>
public sealed record WorkItemLink(
    WorkItemLinkKind Kind, string Title, string Url, int? WorkItemId = null, string? Type = null, string? State = null, string? Comment = null,
    WorkItemStateKind? StateKind = null);

/// <summary>File attached to a work item; <paramref name="Size"/> is in bytes, 0 when unknown.</summary>
public sealed record WorkItemAttachment(string Name, long Size, string Url);

/// <summary>Everything the work item screen shows (read only).</summary>
/// <param name="Summary">The work item as listed (its work item type is <c>Type</c>).</param>
/// <param name="State">State name as the process template spells it ("Active").</param>
/// <param name="StateKind">Workflow position of the state.</param>
/// <param name="TypeColor">Color of the work item type as "RRGGBB"; null when unknown.</param>
/// <param name="AssignedTo">Display name of the person it is assigned to; null when nobody is.</param>
/// <param name="Tags">Tags, in the order the service gives them.</param>
/// <param name="Sections">Planning, people, dates, details and custom fields.</param>
/// <param name="Texts">Rich text fields, in reading order.</param>
/// <param name="Links">Parent, children, related work items, pull requests, commits, branches, builds and hyperlinks.</param>
/// <param name="Timeline">Comments and changes, oldest first.</param>
/// <param name="CommentCount">Number of comments the service reports.</param>
/// <param name="IsTruncated">A list was cut at the page limit: the screen says to continue in the browser.</param>
public sealed record WorkItemDetail(
    Issue Summary, string State, WorkItemStateKind StateKind, string? TypeColor, string? AssignedTo, IReadOnlyList<string> Tags,
    IReadOnlyList<WorkItemSection> Sections, IReadOnlyList<WorkItemText> Texts, IReadOnlyList<WorkItemLink> Links,
    IReadOnlyList<WorkItemAttachment> Attachments, IReadOnlyList<TimelineEntry> Timeline, int CommentCount, bool IsTruncated = false);
