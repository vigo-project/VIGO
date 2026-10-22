using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Writes on a pull request (J6.9b): discussion comments (add, edit, delete), close / reopen, draft state, merge. The "repo"
// scope asked at sign-in covers all of them. The draft state is only reachable through the GraphQL API.
public sealed partial class GitHubClient
{
    private const string GraphQlUrl = ApiRoot + "/graphql";

    public bool SupportsPullRequestWrite => true;

    // J6.10b: the discussion of an issue is the same "issue comments" resource as a pull request's, so edit, delete and
    // reactions reuse the methods below that only take a comment id.
    public bool SupportsIssueWrite => true;

    private const long MaxImageBytes = 25 * 1024 * 1024;

    // Pictures of issues (github.com/user-attachments, *.githubusercontent.com): private ones answer 404 without the token. The
    // token goes to those hosts only; the redirect to the signed address of the file crosses hosts, so .NET drops it there.
    public async Task<GitResult<byte[]>> DownloadImageAsync(string token, string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            return new Failure(Loc.Get("Hosting.ImageNotGitHub"));
        try
        {
            using var request = NewRequest(HttpMethod.Get, url, token);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("image/*");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return await RejectAsync<byte[]>(response, cancellationToken);
            if (response.Content.Headers.ContentLength > MaxImageBytes) return new Failure(Loc.Get("Hosting.ImageTooLarge"));
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length > MaxImageBytes ? new Failure(Loc.Get("Hosting.ImageTooLarge")) : new Success<byte[]>(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new Failure(Loc.Format("Hosting.Unreachable", "GitHub", e.Message), Exception: e);
        }
    }

    public Task<GitResult<TimelineEntry>> AddIssueCommentAsync(
        string token, HostRepository repository, Issue issue, string body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{RepositoryUrl(repository)}/issues/{issue.Number}/comments", token,
            JsonContent.Create(new GitHubCommentWrite(body), GitHubJsonContext.Default.GitHubCommentWrite), ReadCommentAsync, cancellationToken);

    // J6.10c: POST /issues answers the created issue (201); PATCH /issues/{n} changes its state ("not_planned" is a close reason).
    public Task<GitResult<Issue>> CreateIssueAsync(
        string token, HostRepository repository, NewIssue issue, CancellationToken cancellationToken = default) =>
        SendAsync<Issue>(HttpMethod.Post, $"{RepositoryUrl(repository)}/issues", token,
            JsonContent.Create(new GitHubIssueWrite(issue.Title, issue.Body), GitHubJsonContext.Default.GitHubIssueWrite),
            async (response, ct) =>
            {
                var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubIssue, ct);
                return body is null ? (GitResult<Issue>)new Failure(Loc.Get("Hosting.GitHubUnreadable")) : new Success<Issue>(ToIssue(body));
            }, cancellationToken);

    public Task<GitResult<Done>> SetIssueOpenAsync(
        string token, HostRepository repository, Issue issue, bool open, IssueCloseReason reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, $"{RepositoryUrl(repository)}/issues/{issue.Number}", token,
            JsonContent.Create(
                open ? new GitHubIssueStateWrite("open", "reopened")
                    : new GitHubIssueStateWrite("closed", reason == IssueCloseReason.NotPlanned ? "not_planned" : "completed"),
                GitHubJsonContext.Default.GitHubIssueStateWrite),
            DoneAsync, cancellationToken);

    // J6.10d: PATCH /issues/{n} replaces the title and the description, or the whole sets of labels and assignees and the milestone.
    public Task<GitResult<Done>> EditIssueTextAsync(
        string token, HostRepository repository, Issue issue, string title, string body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, $"{RepositoryUrl(repository)}/issues/{issue.Number}", token,
            JsonContent.Create(new GitHubIssueTextWrite(title, body), GitHubJsonContext.Default.GitHubIssueTextWrite), DoneAsync, cancellationToken);

    public Task<GitResult<Done>> SetIssueMetadataAsync(
        string token, HostRepository repository, Issue issue, IssueMetadata metadata, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, $"{RepositoryUrl(repository)}/issues/{issue.Number}", token,
            JsonContent.Create(
                new GitHubIssueMetadataWrite([.. metadata.Labels], [.. metadata.Assignees], metadata.MilestoneNumber),
                GitHubJsonContext.Default.GitHubIssueMetadataWrite),
            DoneAsync, cancellationToken);

    // The three lists follow the "next" link up to the page limit; the dialog says when one was cut.
    public async Task<GitResult<IssueMetadataOptions>> GetIssueMetadataOptionsAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            var api = RepositoryUrl(repository);
            var labels = OptionPages(token, $"{api}/labels?per_page=100", GitHubJsonContext.Default.GitHubLabelArray, cancellationToken);
            var assignees = OptionPages(token, $"{api}/assignees?per_page=100", GitHubJsonContext.Default.GitHubAccountArray, cancellationToken);
            var milestones = OptionPages(token, $"{api}/milestones?state=all&per_page=100", GitHubJsonContext.Default.GitHubMilestoneArray, cancellationToken);
            await Task.WhenAll(labels, assignees, milestones);

            if (labels.Result.Failure is { } labelsFailure) return labelsFailure;
            if (assignees.Result.Failure is { } assigneesFailure) return assigneesFailure;
            if (milestones.Result.Failure is { } milestonesFailure) return milestonesFailure;

            return new Success<IssueMetadataOptions>(new IssueMetadataOptions(
                [.. labels.Result.Items.Select(l => l.Name).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)],
                [.. assignees.Result.Items.Select(a => a.Login).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)],
                [.. milestones.Result.Items.Where(m => m.Title is not null).Select(m => new MilestoneOption(m.Number, m.Title!))],
                labels.Result.Truncated || assignees.Result.Truncated || milestones.Result.Truncated));
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

    private readonly record struct OptionList<T>(T[] Items, bool Truncated, GitResult<IssueMetadataOptions>? Failure);

    private async Task<OptionList<T>> OptionPages<T>(
        string token, string firstUrl, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T[]> typeInfo, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? url = firstUrl;
        for (var page = 0; url is not null; page++)
        {
            if (page == MaxPages) return new OptionList<T>([.. items], true, null);
            using var request = NewRequest(HttpMethod.Get, url, token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new OptionList<T>([], false, await RejectAsync<IssueMetadataOptions>(response, cancellationToken));
            items.AddRange(await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken) ?? []);
            url = NextLink(response);
        }
        return new OptionList<T>([.. items], false, null);
    }

    public Task<GitResult<TimelineEntry>> AddPullRequestCommentAsync(
        string token, HostRepository repository, PullRequest pullRequest, string body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{RepositoryUrl(repository)}/issues/{pullRequest.Number}/comments", token,
            JsonContent.Create(new GitHubCommentWrite(body), GitHubJsonContext.Default.GitHubCommentWrite), ReadCommentAsync, cancellationToken);

    public Task<GitResult<TimelineEntry>> EditPullRequestCommentAsync(
        string token, HostRepository repository, long commentId, string body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, $"{RepositoryUrl(repository)}/issues/comments/{commentId}", token,
            JsonContent.Create(new GitHubCommentWrite(body), GitHubJsonContext.Default.GitHubCommentWrite), ReadCommentAsync, cancellationToken);

    public Task<GitResult<Done>> DeletePullRequestCommentAsync(
        string token, HostRepository repository, long commentId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"{RepositoryUrl(repository)}/issues/comments/{commentId}", token, null, DoneAsync, cancellationToken);

    public Task<GitResult<Done>> SetPullRequestOpenAsync(
        string token, HostRepository repository, PullRequest pullRequest, bool open, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, $"{RepositoryUrl(repository)}/pulls/{pullRequest.Number}", token,
            JsonContent.Create(new GitHubStateWrite(open ? "open" : "closed"), GitHubJsonContext.Default.GitHubStateWrite), DoneAsync, cancellationToken);

    public Task<GitResult<Done>> MergePullRequestAsync(
        string token, HostRepository repository, PullRequest pullRequest, MergeMethod method, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{RepositoryUrl(repository)}/pulls/{pullRequest.Number}/merge", token,
            JsonContent.Create(new GitHubMergeWrite(MethodName(method)), GitHubJsonContext.Default.GitHubMergeWrite), DoneAsync, cancellationToken);

    // Merges the base branch into the head branch on the service (202 Accepted: the update runs asynchronously).
    public Task<GitResult<Done>> UpdatePullRequestBranchAsync(
        string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{RepositoryUrl(repository)}/pulls/{pullRequest.Number}/update-branch", token, null, DoneAsync, cancellationToken);

    public bool SupportsBranchDelete => true;

    // 404 is the answer "absent", not a failure.
    public Task<GitResult<bool>> BranchExistsAsync(
        string token, HostRepository repository, string branch, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"{RepositoryUrl(repository)}/git/ref/heads/{EscapePath(branch)}", token, null,
            (response, _) => Task.FromResult<GitResult<bool>>(new Success<bool>(response.IsSuccessStatusCode)),
            cancellationToken, status => status == HttpStatusCode.NotFound);

    // 422 "Reference does not exist": already deleted.
    public Task<GitResult<Done>> DeleteBranchAsync(
        string token, HostRepository repository, string branch, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"{RepositoryUrl(repository)}/git/refs/heads/{EscapePath(branch)}", token, null,
            DoneAsync, cancellationToken, status => status == HttpStatusCode.UnprocessableEntity);

    // J6.16: git over HTTPS takes the token as the password of the user "x-access-token", sent to github.com only.
    public GitHttpHeader? GitHttpHeaderOf(string accessToken) => new(
        HostingProvider.GitHub, ["https://github.com/"],
        "Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:" + accessToken)));

    private static string EscapePath(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    public Task<GitResult<Done>> SetPullRequestDraftAsync(
        string token, HostRepository repository, PullRequestDetail detail, bool draft, CancellationToken cancellationToken = default)
    {
        var mutation = draft ? "convertPullRequestToDraft" : "markPullRequestReadyForReview";
        return MutatePullRequestAsync(token, detail,
            $"mutation($id: ID!) {{ {mutation}(input: {{ pullRequestId: $id }}) {{ clientMutationId }} }}", null, cancellationToken);
    }

    // The automatic merge waits for the required checks and reviews, then merges with the chosen method (GraphQL only).
    public Task<GitResult<Done>> SetPullRequestAutoMergeAsync(
        string token, HostRepository repository, PullRequestDetail detail, MergeMethod? method, CancellationToken cancellationToken = default) =>
        method is { } chosen
            ? MutatePullRequestAsync(token, detail,
                "mutation($id: ID!, $method: PullRequestMergeMethod!) { enablePullRequestAutoMerge(input: { pullRequestId: $id, mergeMethod: $method }) { clientMutationId } }",
                chosen.ToString().ToUpperInvariant(), cancellationToken)
            : MutatePullRequestAsync(token, detail,
                "mutation($id: ID!) { disablePullRequestAutoMerge(input: { pullRequestId: $id }) { clientMutationId } }", null, cancellationToken);

    private Task<GitResult<Done>> MutatePullRequestAsync(
        string token, PullRequestDetail detail, string query, string? mergeMethod, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(detail.NodeId)
            ? Task.FromResult<GitResult<Done>>(new Failure(Loc.Get("Hosting.GitHubNoPullId")))
            : MutateAsync(token, detail.NodeId, query, mergeMethod, cancellationToken);

    // One GraphQL mutation on the object <paramref name="id"/> (a pull request, a review thread).
    private async Task<GitResult<Done>> MutateAsync(string token, string id, string query, string? mergeMethod, CancellationToken cancellationToken)
    {
        return await SendAsync<Done>(HttpMethod.Post, GraphQlUrl, token,
            JsonContent.Create(new GitHubGraphQlRequest(query, new GitHubGraphQlVariables(id, mergeMethod)), GitHubJsonContext.Default.GitHubGraphQlRequest),
            async (response, ct) =>
            {
                // GraphQL answers 200 even when the mutation is refused: the reason is in "errors".
                var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubGraphQlResponse, ct);
                return body?.Errors is { Length: > 0 } errors
                    ? (GitResult<Done>)new Failure(string.Join(' ', errors.Select(e => e.Message).Where(m => !string.IsNullOrWhiteSpace(m))))
                    : new Success<Done>(Done.Instance);
            }, cancellationToken);
    }

    // Looks for the account's own reaction of that kind on the comment: removes it when there, adds it otherwise.
    public async Task<GitResult<TimelineEntry>> ToggleCommentReactionAsync(
        string token, HostRepository repository, long commentId, ReactionKind reaction, string login, CancellationToken cancellationToken = default)
    {
        var comment = $"{RepositoryUrl(repository)}/issues/comments/{commentId}";
        var content = ReactionName(reaction);
        try
        {
            async Task<HttpResponseMessage> CallAsync(HttpMethod method, string url, HttpContent? body)
            {
                using var message = NewRequest(method, url, token);
                message.Content = body;
                return await http.SendAsync(message, cancellationToken);
            }

            using (var list = await CallAsync(HttpMethod.Get, $"{comment}/reactions?content={Uri.EscapeDataString(content)}&per_page=100", null))
            {
                if (!list.IsSuccessStatusCode) return await RejectAsync<TimelineEntry>(list, cancellationToken);
                var existing = await list.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubReactionArray, cancellationToken) ?? [];
                var own = existing.FirstOrDefault(r => string.Equals(r.User?.Login, login, StringComparison.OrdinalIgnoreCase));
                using var change = own is null
                    ? await CallAsync(HttpMethod.Post, $"{comment}/reactions",
                        JsonContent.Create(new GitHubReactionWrite(content), GitHubJsonContext.Default.GitHubReactionWrite))
                    : await CallAsync(HttpMethod.Delete, $"{comment}/reactions/{own.Id}", null);
                if (!change.IsSuccessStatusCode) return await RejectAsync<TimelineEntry>(change, cancellationToken);
            }

            using var fresh = await CallAsync(HttpMethod.Get, comment, null);
            return fresh.IsSuccessStatusCode ? await ReadCommentAsync(fresh, cancellationToken) : await RejectAsync<TimelineEntry>(fresh, cancellationToken);
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

    private static string ReactionName(ReactionKind reaction) => reaction switch
    {
        ReactionKind.ThumbsUp => "+1",
        ReactionKind.ThumbsDown => "-1",
        ReactionKind.Laugh => "laugh",
        ReactionKind.Hooray => "hooray",
        ReactionKind.Confused => "confused",
        ReactionKind.Heart => "heart",
        ReactionKind.Rocket => "rocket",
        _ => "eyes",
    };

    private static string MethodName(MergeMethod method) => method switch
    {
        MergeMethod.Squash => "squash",
        MergeMethod.Rebase => "rebase",
        _ => "merge",
    };

    private static async Task<GitResult<TimelineEntry>> ReadCommentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubComment, cancellationToken);
        return body is null ? new Failure(Loc.Get("Hosting.GitHubUnreadable")) : new Success<TimelineEntry>(ToEntry(body));
    }

    private static Task<GitResult<Done>> DoneAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        Task.FromResult<GitResult<Done>>(new Success<Done>(Done.Instance));

    // One write request: a refusal goes through the same messages as the reads, an unreadable answer is a failure.
    private async Task<GitResult<T>> SendAsync<T>(
        HttpMethod method, string url, string token, HttpContent? content,
        Func<HttpResponseMessage, CancellationToken, Task<GitResult<T>>> read, CancellationToken cancellationToken,
        Func<HttpStatusCode, bool>? alsoAccepted = null)
    {
        try
        {
            using var message = NewRequest(method, url, token);
            message.Content = content;
            using var response = await http.SendAsync(message, cancellationToken);
            return response.IsSuccessStatusCode || alsoAccepted?.Invoke(response.StatusCode) == true
                ? await read(response, cancellationToken)
                : await RejectAsync<T>(response, cancellationToken);
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
}

internal sealed record GitHubCommentWrite(
    [property: JsonPropertyName("body")] string Body);

internal sealed record GitHubIssueWrite(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body);

internal sealed record GitHubIssueStateWrite(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("state_reason")] string StateReason);

internal sealed record GitHubIssueTextWrite(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body);

// "milestone": null removes the milestone, so the default of the serializer (nulls written) is what the service needs.
internal sealed record GitHubIssueMetadataWrite(
    [property: JsonPropertyName("labels")] string[] Labels,
    [property: JsonPropertyName("assignees")] string[] Assignees,
    [property: JsonPropertyName("milestone")] int? Milestone);

internal sealed record GitHubStateWrite(
    [property: JsonPropertyName("state")] string State);

internal sealed record GitHubMergeWrite(
    [property: JsonPropertyName("merge_method")] string MergeMethod);

internal sealed record GitHubGraphQlVariables(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Method = null);

internal sealed record GitHubReactionWrite(
    [property: JsonPropertyName("content")] string Content);

internal sealed record GitHubReaction(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("user")] GitHubAccount? User);

internal sealed record GitHubGraphQlRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] GitHubGraphQlVariables Variables);

internal sealed record GitHubGraphQlResponse(
    [property: JsonPropertyName("errors")] GitHubErrorDetail[]? Errors);
