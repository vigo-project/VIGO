namespace Vigo.Core.Models;

/// <summary>Lifecycle of an issue.</summary>
public enum IssueState
{
    Open,
    Closed,
}

/// <summary>Everything the issue screen shows (J6.10a, read only).</summary>
/// <param name="Summary">The issue as listed.</param>
/// <param name="Body">Description (Markdown).</param>
/// <param name="NotPlanned">The issue was closed as "not planned" rather than completed.</param>
/// <param name="Milestone">Title of the milestone; null when there is none.</param>
/// <param name="Timeline">Comments and events (labels, assignments, closing…), oldest first.</param>
/// <param name="IsTruncated">A list was cut at the page limit: the screen says to continue in the browser.</param>
public sealed record IssueDetail(
    Issue Summary, string Body, IssueState State, bool NotPlanned, IReadOnlyList<string> Labels, IReadOnlyList<string> Assignees,
    string? Milestone, IReadOnlyList<TimelineEntry> Timeline, bool IsTruncated = false);
