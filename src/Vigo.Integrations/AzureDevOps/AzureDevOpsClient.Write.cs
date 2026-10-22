using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Writes on the pull request itself (J6.9j): a comment on the whole discussion, abandon / reactivate, draft state. Editing and
// deleting a comment stay unavailable (they address a comment by thread and the interface only gives its id); merging came with J6.9k.
public sealed partial class AzureDevOpsClient
{
    public bool SupportsPullRequestComment => true;

    public bool SupportsPullRequestState => true;

    // A comment on the whole pull request is a thread without context; it starts active, as the web interface creates it.
    public async Task<GitResult<TimelineEntry>> AddPullRequestCommentAsync(
        string token, HostRepository repository, PullRequest pullRequest, string body, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Post, $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}/threads?{ApiVersion}", token);
            request.Content = JsonContent.Create(NewThread(body, null), AzureDevOpsJsonContext.Default.AzureNewThread);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<TimelineEntry>(response, cancellationToken);

            var created = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureThread, cancellationToken);
            return created?.Comments?.FirstOrDefault() is not { } comment
                ? new Failure(Loc.Get("Hosting.AzureUnreadable"))
                : new Success<TimelineEntry>(new TimelineEntry(
                    TimelineKind.Comment, comment.Author?.DisplayName ?? "?", comment.Content ?? body, comment.PublishedDate, Id: comment.Id));
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

    // "abandoned" closes a pull request, "active" brings it back.
    public Task<GitResult<Done>> SetPullRequestOpenAsync(
        string token, HostRepository repository, PullRequest pullRequest, bool open, CancellationToken cancellationToken = default) =>
        PatchPullRequestAsync(token, repository, pullRequest, new AzurePullPatch(Status: open ? "active" : "abandoned"), cancellationToken);

    public Task<GitResult<Done>> SetPullRequestDraftAsync(
        string token, HostRepository repository, PullRequestDetail detail, bool draft, CancellationToken cancellationToken = default) =>
        PatchPullRequestAsync(token, repository, detail.Summary, new AzurePullPatch(IsDraft: draft), cancellationToken);

    public bool SupportsPullRequestMerge => true;

    // Completing a pull request names the source commit the user saw (the service refuses a stale one) and a merge strategy. The
    // completion itself runs asynchronously on the service: the pull request may still be active when this returns.
    public async Task<GitResult<Done>> MergePullRequestAsync(
        string token, HostRepository repository, PullRequest pullRequest, MergeMethod method, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}?{ApiVersion}";
            using var read = NewRequest(HttpMethod.Get, url, token);
            using var current = await http.SendAsync(read, cancellationToken);
            if (!Accepted(current)) return await RejectAsync<Done>(current, cancellationToken);
            var pull = await current.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzurePull, cancellationToken);
            if (pull?.LastMergeSourceCommit?.CommitId is not { Length: > 0 } head)
                return new Failure(Loc.Get("Hosting.AzureNoSourceCommit"));

            var strategy = method switch
            {
                MergeMethod.Squash => "squash",
                MergeMethod.Rebase => "rebase",
                _ => "noFastForward",
            };
            using var complete = NewRequest(HttpMethod.Patch, url, token);
            complete.Content = JsonContent.Create(
                new AzurePullPatch(Status: "completed", LastMergeSourceCommit: new AzureCommitRef(head),
                    CompletionOptions: new AzureCompletionOptions(strategy, false)),
                AzureDevOpsJsonContext.Default.AzurePullPatch);
            using var response = await http.SendAsync(complete, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<Done>(response, cancellationToken);

            var result = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzurePull, cancellationToken);
            return result?.MergeStatus is "conflicts" or "failure" or "rejectedByPolicy"
                ? new Failure(string.IsNullOrWhiteSpace(result.MergeFailureMessage)
                    ? "Azure DevOps n'a pas pu fusionner la pull request."
                    : result.MergeFailureMessage)
                : new Success<Done>(Done.Instance);
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

    private async Task<GitResult<Done>> PatchPullRequestAsync(
        string token, HostRepository repository, PullRequest pullRequest, AzurePullPatch patch, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Patch, $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}?{ApiVersion}", token);
            request.Content = JsonContent.Create(patch, AzureDevOpsJsonContext.Default.AzurePullPatch);
            using var response = await http.SendAsync(request, cancellationToken);
            return Accepted(response) ? new Success<Done>(Done.Instance) : await RejectAsync<Done>(response, cancellationToken);
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
}

internal sealed record AzurePullPatch(
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonPropertyName("isDraft"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsDraft = null,
    [property: JsonPropertyName("lastMergeSourceCommit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureCommitRef? LastMergeSourceCommit = null,
    [property: JsonPropertyName("completionOptions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureCompletionOptions? CompletionOptions = null);

internal sealed record AzureCompletionOptions(
    [property: JsonPropertyName("mergeStrategy")] string MergeStrategy,
    [property: JsonPropertyName("deleteSourceBranch")] bool DeleteSourceBranch);
