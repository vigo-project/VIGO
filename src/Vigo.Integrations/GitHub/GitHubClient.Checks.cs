using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Checks and review decision of a pull request (J6.6, ADR-048): check runs and commit statuses of the head commit, and the
// reviews of the pull request.
public sealed partial class GitHubClient
{
    // Which checks are required is only known to GraphQL (isRequired); the screen asks for it, the left panel does not.
    private const string RequiredQuery =
        "query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { "
        + "commits(last: 1) { nodes { commit { statusCheckRollup { contexts(first: 100) { nodes { __typename "
        + "... on CheckRun { name isRequired(pullRequestNumber: $number) } "
        + "... on StatusContext { context isRequired(pullRequestNumber: $number) } } } } } } } } } }";

    public async Task<GitResult<PullRequestStatus>> GetPullRequestStatusAsync(
        string token, HostRepository repository, PullRequest pullRequest, bool includeRequired = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var api = RepositoryUrl(repository);
            var runs = new List<CheckRun>();

            if (pullRequest.HeadSha is { Length: > 0 } sha)
            {
                var sha1 = Uri.EscapeDataString(sha);
                var checkRuns = await GetAsync(token, $"{api}/commits/{sha1}/check-runs?per_page=100",
                    GitHubJsonContext.Default.GitHubCheckRuns, cancellationToken);
                if (checkRuns.Failure is { } failure) return failure;
                runs.AddRange(LatestRuns(checkRuns.Body?.CheckRuns ?? []).Select(ToRun));

                var statuses = await GetAsync(token, $"{api}/commits/{sha1}/status?per_page=100",
                    GitHubJsonContext.Default.GitHubCombinedStatus, cancellationToken);
                if (statuses.Failure is { } statusFailure) return statusFailure;
                runs.AddRange((statuses.Body?.Statuses ?? []).Select(ToRun));

                if (includeRequired && runs.Count > 0)
                {
                    var required = await ReadRequiredAsync(token, repository, pullRequest, cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return Canceled.Instance;
                    if (required is not null)
                        for (var i = 0; i < runs.Count; i++)
                            if (required.TryGetValue(RequiredKey(runs[i].Kind, runs[i].Name), out var isRequired))
                                runs[i] = runs[i] with { IsRequired = isRequired };
                }
            }

            var reviews = await GetAsync(token, $"{api}/pulls/{pullRequest.Number}/reviews?per_page=100",
                GitHubJsonContext.Default.GitHubReviewArray, cancellationToken);
            if (reviews.Failure is { } reviewFailure) return reviewFailure;

            return new Success<PullRequestStatus>(
                new PullRequestStatus(PullRequestStatus.Summarize(runs), ReviewOf(reviews.Body ?? []), runs));
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

    private async Task<(T? Body, GitResult<PullRequestStatus>? Failure)> GetAsync<T>(
        string token, string url, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, url, token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return (default, await RejectAsync<PullRequestStatus>(response, cancellationToken));
        return (await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken), null);
    }

    // A re-run job appears again under the same name: only the most recent run of each (name, application) counts.
    internal static IEnumerable<GitHubCheckRun> LatestRuns(IEnumerable<GitHubCheckRun> runs) => runs
        .GroupBy(r => (r.Name, App: r.App?.Name))
        .Select(g => g.OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue).ThenByDescending(r => r.Id ?? 0).First());

    internal static CheckRun ToRun(GitHubCheckRun r) => new(
        r.Name ?? "?", ToState(r), r.Output?.Title is { Length: > 0 } title ? title : null,
        r.HtmlUrl ?? r.DetailsUrl, r.StartedAt, r.CompletedAt);

    internal static CheckRun ToRun(GitHubCommitStatus s) => new(
        s.Context ?? "?", ToState(s.State), s.Description is { Length: > 0 } description ? description : null,
        s.TargetUrl, s.CreatedAt, s.State == "pending" ? null : s.UpdatedAt ?? s.CreatedAt, Kind: CheckKind.Status);

    // A completed run is judged by its conclusion; a run that has not finished is pending.
    private static CheckState ToState(GitHubCheckRun run) => run.Status == "completed"
        ? run.Conclusion switch
        {
            "success" => CheckState.Success,
            "neutral" or "skipped" => CheckState.Skipped,
            "cancelled" => CheckState.Canceled,
            _ => CheckState.Failure,
        }
        : CheckState.Pending;

    private static CheckState ToState(string? status) => status switch
    {
        "success" => CheckState.Success,
        "pending" => CheckState.Pending,
        _ => CheckState.Failure,
    };

    private static string RequiredKey(CheckKind kind, string name) => (kind == CheckKind.Status ? "s:" : "c:") + name;

    // Null when GraphQL cannot say (scope, rate limit, no rollup): the checks are then shown without the "required" mark.
    private async Task<Dictionary<string, bool>?> ReadRequiredAsync(
        string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken)
    {
        var request = new GitHubThreadsRequest(RequiredQuery, new GitHubThreadsVariables(repository.Owner, repository.Name, pullRequest.Number));
        var result = await SendAsync<Dictionary<string, bool>>(HttpMethod.Post, GraphQlUrl, token,
            JsonContent.Create(request, GitHubJsonContext.Default.GitHubThreadsRequest),
            async (response, ct) =>
            {
                var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubRequiredResponse, ct);
                var nodes = body?.Data?.Repository?.PullRequest?.Commits?.Nodes?.FirstOrDefault()?.Commit?.StatusCheckRollup?.Contexts?.Nodes;
                if (nodes is null) return new Failure(Loc.Get("Hosting.GitHubUnreadable"));
                var required = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var node in nodes)
                {
                    if (node.IsRequired is not { } isRequired) continue;
                    if (node.Typename == "CheckRun" && node.Name is { } name) required[RequiredKey(CheckKind.Check, name)] = isRequired;
                    else if (node.Typename == "StatusContext" && node.Context is { } context) required[RequiredKey(CheckKind.Status, context)] = isRequired;
                }
                return new Success<Dictionary<string, bool>>(required);
            }, cancellationToken);
        return result.Value is Success<Dictionary<string, bool>> { Value: var map } ? map : null;
    }

    // The last decisive review of each reviewer counts: a dismissed review cancels the earlier one, a comment changes nothing.
    // Any outstanding request for changes wins over approvals.
    internal static ReviewState ReviewOf(IEnumerable<GitHubReview> reviews)
    {
        var decisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in reviews.OrderBy(r => r.SubmittedAt))
        {
            if (review.User?.Login is not { } login) continue;
            switch (review.State)
            {
                case "APPROVED" or "CHANGES_REQUESTED": decisions[login] = review.State; break;
                case "DISMISSED": decisions.Remove(login); break;
            }
        }
        if (decisions.ContainsValue("CHANGES_REQUESTED")) return ReviewState.ChangesRequested;
        return decisions.Count > 0 ? ReviewState.Approved : ReviewState.None;
    }
}

internal sealed record GitHubCheckOutput(
    [property: JsonPropertyName("title")] string? Title);

internal sealed record GitHubCheckApp(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record GitHubCheckRun(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion,
    [property: JsonPropertyName("id")] long? Id = null,
    [property: JsonPropertyName("html_url")] string? HtmlUrl = null,
    [property: JsonPropertyName("details_url")] string? DetailsUrl = null,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt = null,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt = null,
    [property: JsonPropertyName("output")] GitHubCheckOutput? Output = null,
    [property: JsonPropertyName("app")] GitHubCheckApp? App = null);

internal sealed record GitHubCheckRuns(
    [property: JsonPropertyName("check_runs")] GitHubCheckRun[]? CheckRuns);

internal sealed record GitHubCommitStatus(
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("target_url")] string? TargetUrl = null,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null);

internal sealed record GitHubRequiredResponse(
    [property: JsonPropertyName("data")] GitHubRequiredData? Data);

internal sealed record GitHubRequiredData(
    [property: JsonPropertyName("repository")] GitHubRequiredRepository? Repository);

internal sealed record GitHubRequiredRepository(
    [property: JsonPropertyName("pullRequest")] GitHubRequiredPull? PullRequest);

internal sealed record GitHubRequiredPull(
    [property: JsonPropertyName("commits")] GitHubRequiredCommits? Commits);

internal sealed record GitHubRequiredCommits(
    [property: JsonPropertyName("nodes")] GitHubRequiredCommitNode[]? Nodes);

internal sealed record GitHubRequiredCommitNode(
    [property: JsonPropertyName("commit")] GitHubRequiredCommit? Commit);

internal sealed record GitHubRequiredCommit(
    [property: JsonPropertyName("statusCheckRollup")] GitHubRequiredRollup? StatusCheckRollup);

internal sealed record GitHubRequiredRollup(
    [property: JsonPropertyName("contexts")] GitHubRequiredContexts? Contexts);

internal sealed record GitHubRequiredContexts(
    [property: JsonPropertyName("nodes")] GitHubRequiredContext[]? Nodes);

internal sealed record GitHubRequiredContext(
    [property: JsonPropertyName("__typename")] string? Typename,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("isRequired")] bool? IsRequired);

internal sealed record GitHubCombinedStatus(
    [property: JsonPropertyName("statuses")] GitHubCommitStatus[]? Statuses);

internal sealed record GitHubReview(
    [property: JsonPropertyName("user")] GitHubAccount? User,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("submitted_at")] DateTimeOffset? SubmittedAt,
    [property: JsonPropertyName("body")] string? Body = null);
