using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Writing in the discussion of a pull request (J6.9i): replies, resolution of threads, comments on lines and the review.
// Azure DevOps has no review object: a "review" is one thread per comment, plus a vote for the decision.
public sealed partial class AzureDevOpsClient
{
    public bool SupportsPullRequestReview => true;

    public async Task<GitResult<ReviewThreadComment>> ReplyToReviewThreadAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewThread thread, string body, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}/threads/{thread.Id}/comments?{ApiVersion}";
            using var request = NewRequest(HttpMethod.Post, url, token);
            // The reply hangs under the first comment, as the web interface does.
            request.Content = JsonContent.Create(
                new AzureNewComment(body, (int)thread.Comments[0].Id), AzureDevOpsJsonContext.Default.AzureNewComment);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<ReviewThreadComment>(response, cancellationToken);
            var created = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureThreadComment, cancellationToken);
            return created is null
                ? new Failure(Loc.Get("Hosting.AzureUnreadable"))
                : new Success<ReviewThreadComment>(new ReviewThreadComment(created.Id, created.Author?.DisplayName ?? "?", created.Content ?? body, created.PublishedDate));
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

    public async Task<GitResult<Done>> SetReviewThreadResolvedAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewThread thread, bool resolved, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}/threads/{thread.Id}?{ApiVersion}";
            using var request = NewRequest(HttpMethod.Patch, url, token);
            request.Content = JsonContent.Create(new AzureThreadStatus(resolved ? "fixed" : "active"), AzureDevOpsJsonContext.Default.AzureThreadStatus);
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

    // The comments become threads one by one (the text of the review first), then the decision becomes a vote. A comment that
    // fails stops the rest: the ones already posted stay on the pull request.
    public async Task<GitResult<Done>> SubmitReviewAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewDecision decision, string body,
        IReadOnlyList<NewReviewComment> comments, CancellationToken cancellationToken = default)
    {
        var threads = $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}/threads?{ApiVersion}";
        var writes = new List<AzureNewThread>();
        if (!string.IsNullOrWhiteSpace(body)) writes.Add(NewThread(body, null));
        writes.AddRange(comments.Select(c => NewThread(c.Body, c)));
        try
        {
            foreach (var write in writes)
            {
                using var request = NewRequest(HttpMethod.Post, threads, token);
                request.Content = JsonContent.Create(write, AzureDevOpsJsonContext.Default.AzureNewThread);
                using var response = await http.SendAsync(request, cancellationToken);
                if (!Accepted(response)) return await RejectAsync<Done>(response, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Unreachable(e);
        }

        var vote = decision switch { ReviewDecision.Approve => ReviewVote.Approved, ReviewDecision.RequestChanges => ReviewVote.WaitingForAuthor, _ => ReviewVote.None };
        return decision == ReviewDecision.Comment
            ? new Success<Done>(Done.Instance)
            : await SetPullRequestVoteAsync(token, repository, pullRequest, vote, cancellationToken);
    }

    // A comment on a line is a thread anchored on that line of the right (new) or left (old) version of the file.
    internal static AzureNewThread NewThread(string content, NewReviewComment? line)
    {
        var comments = new[] { new AzureNewComment(content, 0) };
        if (line is null) return new AzureNewThread(comments, "active", null);

        // The selection is anchored with its columns; a comment on whole lines (or without columns) covers the first column of its
        // first line up to the second of its last, as before.
        var start = new AzureThreadPosition(line.StartLine ?? line.Line, line.StartColumn is > 0 ? line.StartColumn.Value : 1);
        var end = new AzureThreadPosition(line.Line, line.EndColumn is > 0 ? line.EndColumn.Value : 2);
        var context = line.Side == ReviewSide.Old
            ? new AzureNewThreadContext("/" + line.Path.TrimStart('/'), null, null, start, end)
            : new AzureNewThreadContext("/" + line.Path.TrimStart('/'), start, end, null, null);
        return new AzureNewThread(comments, "active", context);
    }
}

internal sealed record AzureNewComment(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("parentCommentId")] int ParentCommentId,
    [property: JsonPropertyName("commentType")] string CommentType = "text");

internal sealed record AzureThreadStatus(
    [property: JsonPropertyName("status")] string Status);

internal sealed record AzureThreadPosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("offset")] int Offset);

internal sealed record AzureNewThreadContext(
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("rightFileStart"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureThreadPosition? RightFileStart,
    [property: JsonPropertyName("rightFileEnd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureThreadPosition? RightFileEnd,
    [property: JsonPropertyName("leftFileStart"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureThreadPosition? LeftFileStart,
    [property: JsonPropertyName("leftFileEnd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureThreadPosition? LeftFileEnd);

internal sealed record AzureNewThread(
    [property: JsonPropertyName("comments")] AzureNewComment[] Comments,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("threadContext"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureNewThreadContext? ThreadContext);
