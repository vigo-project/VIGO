using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Review of a pull request on its diff (J6.9e): the discussion threads (GraphQL, the only API that knows which are resolved),
// replies, resolving, and a review sent in one go with its comments on lines (REST). The "repo" scope covers all of it.
public sealed partial class GitHubClient
{
    private const string ThreadsQuery =
        "query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { "
        + "reviewThreads(first: 100) { nodes { id isResolved isOutdated path line originalLine startLine originalStartLine diffSide "
        + "comments(first: 50) { nodes { databaseId body createdAt author { login } } } } } } } }";

    public bool SupportsPullRequestReview => true;

    // The threads are a bonus of the screen: when they cannot be read, the pull request is still shown without them.
    private async Task<IReadOnlyList<ReviewThread>> ReadThreadsAsync(
        string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken)
    {
        var request = new GitHubThreadsRequest(ThreadsQuery, new GitHubThreadsVariables(repository.Owner, repository.Name, pullRequest.Number));
        var result = await SendAsync<IReadOnlyList<ReviewThread>>(HttpMethod.Post, GraphQlUrl, token,
            JsonContent.Create(request, GitHubJsonContext.Default.GitHubThreadsRequest),
            async (response, ct) =>
            {
                var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubThreadsResponse, ct);
                var nodes = body?.Data?.Repository?.PullRequest?.ReviewThreads?.Nodes;
                if (nodes is null) return new Failure(Loc.Get("Hosting.ThreadsUnreadable"));
                return new Success<IReadOnlyList<ReviewThread>>([.. nodes.Select(ToThread)]);
            }, cancellationToken);
        return result.Value is Success<IReadOnlyList<ReviewThread>> { Value: var threads } ? threads : [];
    }

    internal static ReviewThread ToThread(GitHubThread t) => new(
        t.Id ?? string.Empty, t.Path ?? string.Empty, t.Line ?? t.OriginalLine,
        t.DiffSide == "LEFT" ? ReviewSide.Old : ReviewSide.New, t.IsResolved, t.IsOutdated,
        [.. (t.Comments?.Nodes ?? []).Select(c => new ReviewThreadComment(c.DatabaseId, c.Author?.Login ?? "?", c.Body ?? string.Empty, c.CreatedAt))],
        StartLine: (t.StartLine ?? t.OriginalStartLine) is { } first && first != (t.Line ?? t.OriginalLine) ? first : null);

    public Task<GitResult<ReviewThreadComment>> ReplyToReviewThreadAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewThread thread, string body, CancellationToken cancellationToken = default) =>
        SendAsync<ReviewThreadComment>(HttpMethod.Post, $"{RepositoryUrl(repository)}/pulls/{pullRequest.Number}/comments/{thread.Comments[0].Id}/replies", token,
            JsonContent.Create(new GitHubCommentWrite(body), GitHubJsonContext.Default.GitHubCommentWrite),
            async (response, ct) =>
            {
                var created = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubReviewComment, ct);
                return created is null
                    ? new Failure(Loc.Get("Hosting.GitHubUnreadable"))
                    : new Success<ReviewThreadComment>(new ReviewThreadComment(created.Id, created.User?.Login ?? "?", created.Body ?? string.Empty, created.CreatedAt));
            }, cancellationToken);

    public Task<GitResult<Done>> SetReviewThreadResolvedAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewThread thread, bool resolved, CancellationToken cancellationToken = default) =>
        MutateAsync(token, thread.Id,
            $"mutation($id: ID!) {{ {(resolved ? "resolveReviewThread" : "unresolveReviewThread")}(input: {{ threadId: $id }}) {{ clientMutationId }} }}",
            null, cancellationToken);

    // A comment on several lines names the first one too (GitHub ignores the columns of a selection).
    internal static GitHubReviewCommentWrite ToCommentWrite(NewReviewComment c)
    {
        var side = c.Side == ReviewSide.Old ? "LEFT" : "RIGHT";
        return c.StartLine is { } first && first < c.Line
            ? new GitHubReviewCommentWrite(c.Path, c.Line, side, c.Body, first, side)
            : new GitHubReviewCommentWrite(c.Path, c.Line, side, c.Body);
    }

    public Task<GitResult<Done>> SubmitReviewAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewDecision decision, string body,
        IReadOnlyList<NewReviewComment> comments, CancellationToken cancellationToken = default)
    {
        var write = new GitHubReviewWrite(
            decision switch { ReviewDecision.Approve => "APPROVE", ReviewDecision.RequestChanges => "REQUEST_CHANGES", _ => "COMMENT" },
            string.IsNullOrWhiteSpace(body) ? null : body,
            comments.Count == 0 ? null : [.. comments.Select(ToCommentWrite)]);
        return SendAsync(HttpMethod.Post, $"{RepositoryUrl(repository)}/pulls/{pullRequest.Number}/reviews", token,
            JsonContent.Create(write, GitHubJsonContext.Default.GitHubReviewWrite), DoneAsync, cancellationToken);
    }
}

internal sealed record GitHubReviewCommentWrite(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("start_line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StartLine = null,
    [property: JsonPropertyName("start_side"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StartSide = null);

internal sealed record GitHubReviewWrite(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Body,
    [property: JsonPropertyName("comments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GitHubReviewCommentWrite[]? Comments);

internal sealed record GitHubThreadsVariables(
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("number")] int Number);

internal sealed record GitHubThreadsRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] GitHubThreadsVariables Variables);

internal sealed record GitHubThreadsResponse(
    [property: JsonPropertyName("data")] GitHubThreadsData? Data);

internal sealed record GitHubThreadsData(
    [property: JsonPropertyName("repository")] GitHubThreadsRepository? Repository);

internal sealed record GitHubThreadsRepository(
    [property: JsonPropertyName("pullRequest")] GitHubThreadsPull? PullRequest);

internal sealed record GitHubThreadsPull(
    [property: JsonPropertyName("reviewThreads")] GitHubThreadConnection? ReviewThreads);

internal sealed record GitHubThreadConnection(
    [property: JsonPropertyName("nodes")] GitHubThread[]? Nodes);

internal sealed record GitHubThread(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("isResolved")] bool IsResolved,
    [property: JsonPropertyName("isOutdated")] bool IsOutdated,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("originalLine")] int? OriginalLine,
    [property: JsonPropertyName("startLine")] int? StartLine,
    [property: JsonPropertyName("originalStartLine")] int? OriginalStartLine,
    [property: JsonPropertyName("diffSide")] string? DiffSide,
    [property: JsonPropertyName("comments")] GitHubThreadCommentConnection? Comments);

internal sealed record GitHubThreadCommentConnection(
    [property: JsonPropertyName("nodes")] GitHubThreadComment[]? Nodes);

internal sealed record GitHubThreadComment(
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("author")] GitHubThreadAuthor? Author);

internal sealed record GitHubThreadAuthor(
    [property: JsonPropertyName("login")] string? Login);
