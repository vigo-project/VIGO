using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Integrations.AzureDevOps;

// Checks of a pull request (J6.6, ADR-048): the statuses posted on it and the evaluations of its branch policies (build
// validation, required reviewers…). The review decision comes from the reviewers' votes already read with the list.
public sealed partial class AzureDevOpsClient
{
    private const string PolicyApiVersion = "api-version=7.1-preview.1";

    // Azure DevOps says which policies block in the same answer, so includeRequired changes nothing here.
    public async Task<GitResult<PullRequestStatus>> GetPullRequestStatusAsync(
        string token, HostRepository repository, PullRequest pullRequest, bool includeRequired = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var runs = new List<CheckRun>();

            using var statusRequest = NewRequest(
                HttpMethod.Get, $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}/statuses?{ApiVersion}", token);
            using var statusResponse = await http.SendAsync(statusRequest, cancellationToken);
            if (!Accepted(statusResponse)) return await RejectAsync<PullRequestStatus>(statusResponse, cancellationToken);
            var statuses = await statusResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureStatusList, cancellationToken);
            runs.AddRange((statuses?.Value ?? []).Select(ToRun).OfType<CheckRun>());

            // Policies are looked up by the artifact id of the pull request, which needs the id of its project.
            if (pullRequest.ProjectId is { Length: > 0 } projectId)
            {
                var artifact = Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{projectId}/{pullRequest.Number}");
                using var policyRequest = NewRequest(
                    HttpMethod.Get, $"{ProjectApi(repository)}/policy/evaluations?artifactId={artifact}&{PolicyApiVersion}", token);
                using var policyResponse = await http.SendAsync(policyRequest, cancellationToken);
                if (!Accepted(policyResponse)) return await RejectAsync<PullRequestStatus>(policyResponse, cancellationToken);
                var policies = await policyResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzurePolicyList, cancellationToken);
                runs.AddRange((policies?.Value ?? []).Select(e => ToRun(e, repository)).OfType<CheckRun>());
            }

            return new Success<PullRequestStatus>(new PullRequestStatus(PullRequestStatus.Summarize(runs), pullRequest.Review, runs));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Unreachable(e);
        }
    }

    // Votes: 10 approved, 5 approved with suggestions, 0 none, -5 waiting for the author, -10 rejected. A refusal wins.
    internal static ReviewState ReviewOf(IEnumerable<AzureReviewer>? reviewers)
    {
        var votes = (reviewers ?? []).Where(r => r.IsContainer != true).Select(r => r.Vote).ToList();
        if (votes.Any(v => v < 0)) return ReviewState.ChangesRequested;
        return votes.Any(v => v > 0) ? ReviewState.Approved : ReviewState.None;
    }

    // "notApplicable" statuses say nothing about the pull request.
    private static CheckRun? ToRun(AzureStatus s)
    {
        CheckState? state = s.State switch
        {
            "succeeded" => CheckState.Success,
            "failed" or "error" => CheckState.Failure,
            "pending" or "notSet" => CheckState.Pending,
            _ => null,
        };
        return state is { } known
            ? new CheckRun(NameOf(s), known, s.Description is { Length: > 0 } description ? description : null, s.TargetUrl,
                s.CreationDate, known == CheckState.Pending ? null : s.UpdatedDate ?? s.CreationDate, Kind: CheckKind.Status)
            : null;
    }

    private static string NameOf(AzureStatus s) =>
        string.Join('/', new[] { s.Context?.Genre, s.Context?.Name }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } name ? name : "?";

    // A policy that does not apply is kept as skipped: the screen lists it, the summary ignores it.
    internal static CheckRun? ToRun(AzurePolicyEvaluation e, HostRepository repository)
    {
        var type = e.Configuration?.Type?.DisplayName;
        var name = e.Configuration?.Settings?.DisplayName is { Length: > 0 } custom ? custom : type ?? "?";
        CheckState? state = e.Status switch
        {
            "approved" => CheckState.Success,
            "rejected" or "broken" => CheckState.Failure,
            "queued" or "running" => CheckState.Pending,
            "notApplicable" => CheckState.Skipped,
            _ => null,
        };
        if (state is not { } known) return null;

        var path = string.Join('/', repository.Owner.Split('/').Select(Uri.EscapeDataString));
        var url = e.Context?.BuildId is { } buildId ? $"https://{repository.Host}/{path}/_build/results?buildId={buildId}" : null;
        return new CheckRun(
            name, known, type is { Length: > 0 } && type != name ? type : null, url, e.StartedDate, e.CompletedDate,
            e.Configuration?.IsBlocking, CheckKind.Policy);
    }
}

internal sealed record AzureReviewer(
    [property: JsonPropertyName("vote")] int Vote,
    [property: JsonPropertyName("isContainer")] bool? IsContainer,
    [property: JsonPropertyName("displayName")] string? DisplayName = null);

internal sealed record AzureProject(
    [property: JsonPropertyName("id")] string? Id);

internal sealed record AzurePullRepository(
    [property: JsonPropertyName("project")] AzureProject? Project);

internal sealed record AzureStatusContext(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("genre")] string? Genre);

internal sealed record AzureStatus(
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("context")] AzureStatusContext? Context,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("targetUrl")] string? TargetUrl = null,
    [property: JsonPropertyName("creationDate")] DateTimeOffset? CreationDate = null,
    [property: JsonPropertyName("updatedDate")] DateTimeOffset? UpdatedDate = null);

internal sealed record AzureStatusList(
    [property: JsonPropertyName("value")] AzureStatus[]? Value);

internal sealed record AzureNamed(
    [property: JsonPropertyName("displayName")] string? DisplayName);

internal sealed record AzurePolicyConfiguration(
    [property: JsonPropertyName("type")] AzureNamed? Type,
    [property: JsonPropertyName("settings")] AzureNamed? Settings,
    [property: JsonPropertyName("isBlocking")] bool? IsBlocking = null);

internal sealed record AzurePolicyContext(
    [property: JsonPropertyName("buildId")] long? BuildId);

internal sealed record AzurePolicyEvaluation(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("configuration")] AzurePolicyConfiguration? Configuration,
    [property: JsonPropertyName("context")] AzurePolicyContext? Context = null,
    [property: JsonPropertyName("startedDate")] DateTimeOffset? StartedDate = null,
    [property: JsonPropertyName("completedDate")] DateTimeOffset? CompletedDate = null);

internal sealed record AzurePolicyList(
    [property: JsonPropertyName("value")] AzurePolicyEvaluation[]? Value);
