using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Detail of an issue (J6.10a): description, labels, assignees, milestone, comments and events. Read only; the comments and the
// events follow the "next" link of the service up to a page limit, and the screen says when a list was cut.
public sealed partial class GitHubClient
{
    public bool SupportsIssueDetail => true;

    public async Task<GitResult<IssueDetail>> GetIssueDetailAsync(
        string token, HostRepository repository, Issue issue, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{RepositoryUrl(repository)}/issues/{issue.Number}";
            var header = SingleIssuePart(token, url, GitHubJsonContext.Default.GitHubIssue, cancellationToken);
            var comments = IssuePages(token, $"{url}/comments?per_page={DetailPageSize}", GitHubJsonContext.Default.GitHubCommentArray, cancellationToken);
            var events = IssuePages(token, $"{url}/events?per_page={DetailPageSize}", GitHubJsonContext.Default.GitHubIssueEventArray, cancellationToken);
            await Task.WhenAll(header, comments, events);

            if (header.Result.Failure is { } failure) return failure;
            if (comments.Result.Failure is { } commentsFailure) return commentsFailure;
            if (events.Result.Failure is { } eventsFailure) return eventsFailure;
            if (header.Result.Body is not { } body) return new Failure(Loc.Get("Hosting.GitHubUnreadable"));

            var timeline = new List<TimelineEntry>();
            timeline.AddRange(comments.Result.Items.Select(ToEntry));
            timeline.AddRange(events.Result.Items.Select(EventEntry).OfType<TimelineEntry>());

            var detail = new IssueDetail(
                new Issue(body.Number, body.Title, body.User?.Login ?? "?", body.UpdatedAt, body.HtmlUrl),
                body.Body ?? string.Empty,
                body.State == "closed" ? IssueState.Closed : IssueState.Open,
                body.StateReason == "not_planned",
                [.. (body.Labels ?? []).Select(l => l.Name).OfType<string>()],
                [.. (body.Assignees ?? []).Select(a => a.Login).OfType<string>()],
                body.Milestone?.Title,
                [.. timeline.OrderBy(t => t.CreatedAt)],
                comments.Result.Truncated || events.Result.Truncated);
            return new Success<IssueDetail>(detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new Failure(Loc.Format("Hosting.Unreachable", "GitHub", e.Message), Exception: e);
        }
    }

    /// <summary>The sentence an event of the issue is shown as ("a ajouté le label « bug »"); null for the events the screen leaves out.</summary>
    internal static TimelineEntry? EventEntry(GitHubIssueEvent e)
    {
        var text = e.Event switch
        {
            "closed" => Loc.Get("Issue.Event.Closed"),
            "reopened" => Loc.Get("Issue.Event.Reopened"),
            "labeled" when e.Label?.Name is { } label => Loc.Format("Issue.Event.Labeled", label),
            "unlabeled" when e.Label?.Name is { } label => Loc.Format("Issue.Event.Unlabeled", label),
            "assigned" when e.Assignee?.Login is { } who => Loc.Format("Issue.Event.Assigned", who),
            "unassigned" when e.Assignee?.Login is { } who => Loc.Format("Issue.Event.Unassigned", who),
            "milestoned" when e.Milestone?.Title is { } title => Loc.Format("Issue.Event.Milestoned", title),
            "demilestoned" when e.Milestone?.Title is { } title => Loc.Format("Issue.Event.Demilestoned", title),
            "renamed" when e.Rename is { } rename => Loc.Format("Issue.Event.Renamed", rename.From, rename.To),
            "locked" => Loc.Get("Issue.Event.Locked"),
            "unlocked" => Loc.Get("Issue.Event.Unlocked"),
            "referenced" when e.CommitId is { Length: > 0 } sha => Loc.Format("Issue.Event.Referenced", sha.Length > 7 ? sha[..7] : sha),
            _ => null,
        };
        return text is null ? null : new TimelineEntry(TimelineKind.Event, e.Actor?.Login ?? "?", text, e.CreatedAt);
    }

    private readonly record struct IssuePart<T>(T? Body, GitResult<IssueDetail>? Failure);

    private readonly record struct IssuePartPages<T>(T[] Items, bool Truncated, GitResult<IssueDetail>? Failure);

    private async Task<IssuePart<T>> SingleIssuePart<T>(string token, string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, url, token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return new IssuePart<T>(default, await RejectAsync<IssueDetail>(response, cancellationToken));
        return new IssuePart<T>(await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken), null);
    }

    private async Task<IssuePartPages<T>> IssuePages<T>(string token, string firstUrl, JsonTypeInfo<T[]> typeInfo, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? url = firstUrl;
        for (var page = 0; url is not null; page++)
        {
            if (page == MaxPages) return new IssuePartPages<T>([.. items], true, null);
            using var request = NewRequest(HttpMethod.Get, url, token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new IssuePartPages<T>([], false, await RejectAsync<IssueDetail>(response, cancellationToken));
            items.AddRange(await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken) ?? []);
            url = NextLink(response);
        }
        return new IssuePartPages<T>([.. items], false, null);
    }
}

internal sealed record GitHubRename(
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] string? To);

internal sealed record GitHubIssueEvent(
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("actor")] GitHubAccount? Actor,
    [property: JsonPropertyName("label")] GitHubLabel? Label = null,
    [property: JsonPropertyName("assignee")] GitHubAccount? Assignee = null,
    [property: JsonPropertyName("milestone")] GitHubMilestone? Milestone = null,
    [property: JsonPropertyName("rename")] GitHubRename? Rename = null,
    [property: JsonPropertyName("commit_id")] string? CommitId = null);
