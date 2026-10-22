namespace Vigo.Core.Models;

/// <summary>Repository on a hosting service (J6.2): what a remote URL points to.</summary>
/// <param name="Host">Host name of the service ("github.com").</param>
/// <param name="Owner">User or organization; "organization/project" on Azure DevOps.</param>
/// <param name="Name">Repository name, without ".git".</param>
public sealed record HostRepository(HostingProvider Provider, string Host, string Owner, string Name)
{
    /// <summary>"atlas/atlas-api".</summary>
    public string FullName => $"{Owner}/{Name}";

    /// <summary>Page of the repository in a browser.</summary>
    public string WebUrl => Provider == HostingProvider.AzureDevOps
        ? $"https://{Host}/{Owner}/_git/{Name}"
        : $"https://{Host}/{Owner}/{Name}";

    /// <summary>Page listing the open pull requests.</summary>
    public string PullRequestsUrl => Provider == HostingProvider.AzureDevOps ? WebUrl + "/pullrequests" : WebUrl + "/pulls";

    /// <summary>Page listing the open issues (work items on Azure DevOps).</summary>
    public string IssuesUrl => Provider == HostingProvider.AzureDevOps ? $"https://{Host}/{Owner}/_workitems" : WebUrl + "/issues";

    /// <summary>What <c>#n</c>, <c>!n</c> and <c>@name</c> in a comment link to.</summary>
    public ReferenceLinks ReferenceLinks => Provider == HostingProvider.AzureDevOps
        ? new ReferenceLinks($"https://{Host}/{Owner}/_workitems/edit/{{0}}", WebUrl + "/pullrequest/{0}", null)
        : new ReferenceLinks(WebUrl + "/issues/{0}", WebUrl + "/pull/{0}", $"https://{Host}/{{0}}");
}

/// <summary>
/// Addresses the comments link to (<c>{0}</c> is the number or the name): <c>#123</c> an issue or work item, <c>!123</c> a pull request,
/// <c>@name</c> a person. Null when the service has no such page (a mention then stays a mark without link).
/// </summary>
public sealed record ReferenceLinks(string? Issue, string? Pull, string? User);

/// <summary>What a reference typed in a comment points to: <c>#</c> an issue or work item, <c>!</c> a pull request, <c>@</c> a person.</summary>
public enum ReferenceKind
{
    Issue,
    PullRequest,
    Person,
}

/// <summary>A proposal of the completion menu: <paramref name="Label"/> (title or name), <paramref name="Detail"/> (number, type, state) and the text to insert.</summary>
public sealed record ReferenceSuggestion(string Label, string Detail, string Insert);

/// <summary>Pull request of a hosting service, as listed in the left panel.</summary>
/// <param name="HeadBranch">Source branch.</param>
/// <param name="HeadRepository">"owner/name" of the source repository; differs from the target one for a fork.</param>
/// <param name="BaseBranch">Branch the pull request is proposed on.</param>
/// <param name="Url">Page of the pull request in a browser.</param>
/// <param name="HeadSha">Tip of the source branch (GitHub reads the checks of this commit); null when unknown.</param>
/// <param name="ProjectId">Id of the project the pull request belongs to (Azure DevOps policies); null elsewhere.</param>
/// <param name="Review">Review decision already known from the list (Azure DevOps votes); GitHub reads it with the status.</param>
public sealed record PullRequest(
    int Number, string Title, string Author, string HeadBranch, string? HeadRepository, string BaseBranch, bool IsDraft,
    DateTimeOffset UpdatedAt, string Url, string? HeadSha = null, string? ProjectId = null, ReviewState Review = ReviewState.None);

/// <summary>Outcome of the continuous integration checks of a pull request (J6.6).</summary>
public enum CheckState
{
    /// <summary>No check reported (or not read yet).</summary>
    None,

    Pending,
    Success,
    Failure,

    /// <summary>Not run (skipped job, policy that does not apply): says nothing about the pull request.</summary>
    Skipped,

    /// <summary>Canceled or timed out before a verdict: counts as a failure.</summary>
    Canceled,
}

/// <summary>Where a <see cref="CheckRun"/> comes from (J6.12).</summary>
public enum CheckKind
{
    /// <summary>CI job (GitHub check run).</summary>
    Check,

    /// <summary>Status posted on the commit or the pull request.</summary>
    Status,

    /// <summary>Branch policy evaluation (Azure DevOps).</summary>
    Policy,
}

/// <summary>Review decision on a pull request (J6.6).</summary>
public enum ReviewState
{
    None,
    Approved,
    ChangesRequested,
}

/// <summary>One check (CI job, status, branch policy) of a pull request.</summary>
/// <param name="Description">Output title, status description or policy name; null when the service gives none.</param>
/// <param name="Url">Page of the logs or of the build; null when unknown.</param>
/// <param name="IsRequired">The check must pass before the merge; null when the service does not say.</param>
public sealed record CheckRun(
    string Name, CheckState State, string? Description = null, string? Url = null,
    DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null, bool? IsRequired = null, CheckKind Kind = CheckKind.Check);

/// <summary>Checks and review decision of a pull request (J6.6, ADR-048).</summary>
public sealed record PullRequestStatus(CheckState Checks, ReviewState Review, IReadOnlyList<CheckRun> Runs)
{
    /// <summary>Worst state of <paramref name="runs"/>: a failure (or cancellation) wins, then pending, then success; skipped runs count for nothing.</summary>
    public static CheckState Summarize(IEnumerable<CheckRun> runs)
    {
        var state = CheckState.None;
        foreach (var run in runs)
        {
            if (run.State is CheckState.Failure or CheckState.Canceled) return CheckState.Failure;
            if (run.State == CheckState.Pending) state = CheckState.Pending;
            else if (run.State == CheckState.Success && state == CheckState.None) state = CheckState.Success;
        }
        return state;
    }
}

/// <summary>Open issue of a hosting service. <c>Type</c>: work item type on Azure DevOps (Bug, Task…), null on GitHub.</summary>
public sealed record Issue(int Number, string Title, string Author, DateTimeOffset UpdatedAt, string Url, string? Type = null);

/// <summary>State of the issues a list shows (J6.10a).</summary>
public enum IssueStateFilter
{
    Open,
    Closed,
    All,
}

/// <summary>Filters of the issue list of the left panel (J6.10a).</summary>
/// <param name="AssignedToMe">Only the issues assigned to the signed-in account.</param>
/// <param name="CreatedByMe">Only the issues opened by the signed-in account.</param>
/// <param name="Search">Text to search for; the service's own qualifiers (<c>label:bug</c>, <c>milestone:v2</c>…) are passed through.</param>
public sealed record IssueQuery(IssueStateFilter State = IssueStateFilter.Open, bool AssignedToMe = false, bool CreatedByMe = false, string Search = "")
{
    /// <summary>Open issues, no other filter: what the list shows until the user filters it.</summary>
    public static IssueQuery Default { get; } = new();

    public bool IsDefault => this == Default;
}

/// <summary>One page of issues, most recently updated first.</summary>
/// <param name="HasMore">Another page may follow.</param>
public sealed record IssuePage(IReadOnlyList<Issue> Items, bool HasMore);

/// <summary>Pull request to create (J6.2).</summary>
/// <param name="Head">Source branch (the branch on the repository itself; forks are not supported).</param>
/// <param name="Base">Branch to merge into.</param>
/// <param name="Reviewers">Reviewers to request (J6.9l); null or empty: none.</param>
/// <param name="Labels">Labels to put on the new pull request; null or empty: none.</param>
/// <param name="LinkedIssues">Numbers of the issues (work items) to link; null or empty: none.</param>
/// <param name="Assignees">Identifiers of the people to assign (J6.9n); null or empty: none.</param>
public sealed record NewPullRequest(
    string Title, string Body, string Head, string Base, bool IsDraft,
    IReadOnlyList<NewPullRequestReviewer>? Reviewers = null, IReadOnlyList<string>? Labels = null, IReadOnlyList<int>? LinkedIssues = null,
    IReadOnlyList<string>? Assignees = null);

/// <summary>An account that can be asked to review a pull request; <see cref="Id"/> is the service's own identifier.</summary>
public sealed record PullRequestReviewerOption(string Id, string Name);

/// <summary>A reviewer requested on a new pull request; a required one must approve before the merge.</summary>
public sealed record NewPullRequestReviewer(string Id, string Name, bool IsRequired);

/// <summary>What the repository offers when a pull request is created (J6.9l).</summary>
/// <param name="IsTruncated">A list was cut at the limit: the rest is on the hosting service.</param>
/// <param name="SupportsRequiredReviewers">The service can make a reviewer mandatory (Azure DevOps); GitHub has no such per-request flag.</param>
/// <param name="Assignees">The people a pull request can be assigned to (J6.9n); null: the service has no assignees at creation.</param>
public sealed record PullRequestCreateOptions(
    IReadOnlyList<PullRequestReviewerOption> Reviewers, bool IsTruncated = false, bool SupportsRequiredReviewers = true,
    IReadOnlyList<PullRequestReviewerOption>? Assignees = null);

/// <summary>Issue to create (J6.10c): a title and a Markdown description; labels, assignees and milestone are set afterwards on the service.</summary>
public sealed record NewIssue(string Title, string Body);

/// <summary>Milestone a repository offers (J6.10d); <paramref name="Number"/> is what the service wants back when assigning it.</summary>
public sealed record MilestoneOption(int Number, string Title);

/// <summary>What the repository offers to put on an issue (J6.10d): its labels, the accounts that can be assigned, its milestones (closed ones too: the issue may still carry one).</summary>
/// <param name="IsTruncated">A list was cut at the page limit: the rest is on the hosting service.</param>
public sealed record IssueMetadataOptions(
    IReadOnlyList<string> Labels, IReadOnlyList<string> Assignees, IReadOnlyList<MilestoneOption> Milestones, bool IsTruncated = false);

/// <summary>Labels, assignees and milestone an issue ends up with (J6.10d); they replace the current ones. A null milestone removes it.</summary>
public sealed record IssueMetadata(IReadOnlyList<string> Labels, IReadOnlyList<string> Assignees, int? MilestoneNumber);

/// <summary>How an issue is closed (J6.10c): done, or "not planned".</summary>
public enum IssueCloseReason
{
    Completed,
    NotPlanned,
}
