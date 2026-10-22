using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Pull requests and issues (J6.2, ADR-045): REST v3, first page of open items, responses revalidated with ETags (a 304
// does not count against the rate limit).
public sealed partial class GitHubClient
{
    private const string ApiRoot = "https://api.github.com";
    private const int PageSize = 50;

    // Keyed by URL only (a renewed token replaces the entry instead of leaving the old one behind); an entry obtained with another
    // token is ignored, so a list is never revalidated with, or served to, an account that did not fetch it.
    private readonly ConcurrentDictionary<string, (string Token, string ETag, object Items)> _etags = new();

    public HostRepository? ParseRepository(RemoteUrl url)
    {
        if (!string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(url.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = url.DisplayPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? new HostRepository(HostingProvider.GitHub, "github.com", parts[0], parts[1]) : null;
    }

    public Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default) =>
        GetListAsync(token, $"{RepositoryUrl(repository)}/pulls?state=open&sort=updated&direction=desc&per_page={PageSize}",
            GitHubJsonContext.Default.GitHubPullArray, ToPullRequest, cancellationToken);

    public Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default) =>
        GetListAsync(token, $"{RepositoryUrl(repository)}/issues?state=open&sort=updated&direction=desc&per_page={PageSize}",
            GitHubJsonContext.Default.GitHubIssueArray,
            // The issues endpoint also lists pull requests, recognizable by their "pull_request" member.
            i => i.PullRequest is null ? new Issue(i.Number, i.Title, i.User?.Login ?? "?", i.UpdatedAt, i.HtmlUrl) : null,
            cancellationToken);

    public bool SupportsIssueFilters => true;

    // Without text, the issues endpoint filters by state, assignee and creator (ETag-revalidated like the other lists). With
    // text, the search API takes the same filters as qualifiers, plus the ones the user types (label:, milestone:…). Both
    // list pull requests too: they are dropped here, so a page may hold fewer than PageSize issues and still have a next one.
    public async Task<GitResult<IssuePage>> ListIssuePageAsync(
        string token, HostRepository repository, IssueQuery query, string? login, int page, CancellationToken cancellationToken = default)
    {
        if (query is { AssignedToMe: true } or { CreatedByMe: true } && string.IsNullOrEmpty(login))
            return new Failure(Loc.Get("Hosting.GitHubUnknownAccount"));

        var result = query.Search.Trim().Length > 0
            ? await SearchIssuesAsync(token, repository, query, login, page, cancellationToken)
            : await GetListAsync(token, IssuesUrl(repository, query, login, page), GitHubJsonContext.Default.GitHubIssueArray, i => i, cancellationToken);
        switch (result)
        {
            case Success<IReadOnlyList<GitHubIssue>> { Value: var raw }:
                IReadOnlyList<Issue> items = [.. raw.Where(i => i.PullRequest is null).Select(ToIssue)];
                return new Success<IssuePage>(new IssuePage(items, raw.Count >= PageSize));
            case AuthenticationFailed a: return a;
            case Failure f: return f;
            default: return Canceled.Instance;
        }
    }

    private static string IssuesUrl(HostRepository repository, IssueQuery query, string? login, int page)
    {
        var state = query.State switch
        {
            IssueStateFilter.Closed => "closed",
            IssueStateFilter.All => "all",
            _ => "open",
        };
        var url = $"{RepositoryUrl(repository)}/issues?state={state}&sort=updated&direction=desc&per_page={PageSize}";
        if (query.AssignedToMe) url += "&assignee=" + Uri.EscapeDataString(login!);
        if (query.CreatedByMe) url += "&creator=" + Uri.EscapeDataString(login!);
        return page > 1 ? url + "&page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture) : url;
    }

    private static string SearchQuery(HostRepository repository, IssueQuery query, string? login)
    {
        var text = new System.Text.StringBuilder($"repo:{repository.Owner}/{repository.Name} is:issue");
        if (query.State != IssueStateFilter.All) text.Append(query.State == IssueStateFilter.Closed ? " is:closed" : " is:open");
        if (query.AssignedToMe) text.Append(" assignee:").Append(login);
        if (query.CreatedByMe) text.Append(" author:").Append(login);
        return text.Append(' ').Append(query.Search.Trim()).ToString();
    }

    private async Task<GitResult<IReadOnlyList<GitHubIssue>>> SearchIssuesAsync(
        string token, HostRepository repository, IssueQuery query, string? login, int page, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{ApiRoot}/search/issues?q={Uri.EscapeDataString(SearchQuery(repository, query, login))}&sort=updated&order=desc&per_page={PageSize}"
                + $"&page={page.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            using var request = NewRequest(HttpMethod.Get, url, token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return await RejectAsync<IReadOnlyList<GitHubIssue>>(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubIssueSearch, cancellationToken);
            return new Success<IReadOnlyList<GitHubIssue>>(body?.Items ?? []);
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

    private static Issue ToIssue(GitHubIssue i) => new(i.Number, i.Title, i.User?.Login ?? "?", i.UpdatedAt, i.HtmlUrl);

    public async Task<GitResult<string>> GetDefaultBranchAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, RepositoryUrl(repository), token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return await RejectAsync<string>(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubRepository, cancellationToken);
            return body is { DefaultBranch.Length: > 0 }
                ? new Success<string>(body.DefaultBranch)
                : new Failure(Loc.Get("Hosting.GitHubUnreadable"));
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

    public async Task<GitResult<PullRequest>> CreatePullRequestAsync(
        string token, HostRepository repository, NewPullRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = NewRequest(HttpMethod.Post, RepositoryUrl(repository) + "/pulls", token);
            message.Content = JsonContent.Create(
                new GitHubNewPull(request.Title, WithClosingKeywords(request.Body, request.LinkedIssues), request.Head, request.Base, request.IsDraft),
                GitHubJsonContext.Default.GitHubNewPull);
            using var response = await http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode) return await RejectAsync<PullRequest>(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubPull, cancellationToken);
            return body is null ? new Failure(Loc.Get("Hosting.GitHubUnreadable")) : new Success<PullRequest>(ToPullRequest(body));
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

    private static string RepositoryUrl(HostRepository repository) =>
        $"{ApiRoot}/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}";

    private static PullRequest ToPullRequest(GitHubPull p) => new(
        p.Number, p.Title, p.User?.Login ?? "?", p.Head?.Ref ?? string.Empty, p.Head?.Repo?.FullName, p.Base?.Ref ?? string.Empty,
        p.Draft, p.UpdatedAt, p.HtmlUrl, HeadSha: p.Head?.Sha);

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VIGO", "0.1"));
        return request;
    }

    // One page of a list, revalidated with the ETag of the last answer for this URL (when it was obtained with the same token).
    private async Task<GitResult<IReadOnlyList<T>>> GetListAsync<TDto, T>(
        string token, string url, JsonTypeInfo<TDto[]> typeInfo, Func<TDto, T?> map, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, url, token);
            var cached = _etags.TryGetValue(url, out var known) && known.Token == token && known.Items is IReadOnlyList<T> ? known : default;
            if (cached.ETag is not null) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag, isWeak: false));

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && cached.Items is IReadOnlyList<T> same)
                return new Success<IReadOnlyList<T>>(same);
            if (!response.IsSuccessStatusCode) return await RejectAsync<IReadOnlyList<T>>(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken) ?? [];
            IReadOnlyList<T> items = [.. body.Select(map).OfType<T>()];
            if (response.Headers.ETag is { } etag) _etags[url] = (token, etag.Tag, items);
            return new Success<IReadOnlyList<T>>(items);
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

    private async Task<GitResult<T>> RejectAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await ReadErrorAsync(response, cancellationToken);
        var rateLimited = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0";
        if (logger is not null && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            LogRefused(logger, (int)response.StatusCode, response.RequestMessage?.RequestUri?.AbsolutePath ?? "?",
                Header(response, "X-OAuth-Scopes"), Header(response, "github-authentication-token-expiration"),
                Header(response, "X-GitHub-SSO"), detail ?? string.Empty);
        }
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new AuthenticationFailed("github",
                Loc.Get("Hosting.GitHubTokenRevoked")),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when rateLimited || response.StatusCode == HttpStatusCode.TooManyRequests =>
                new Failure(Loc.Get("Hosting.GitHubRateLimit")),
            HttpStatusCode.Forbidden when response.Headers.Contains("X-GitHub-SSO") =>
                new AuthenticationFailed("github", Loc.Get("Hosting.GitHubSso")),
            HttpStatusCode.Forbidden => new AuthenticationFailed("github",
                Loc.Get("Hosting.GitHubForbidden")
                + (detail is null ? string.Empty : " " + detail)),
            HttpStatusCode.NotFound => new Failure(Loc.Get("Hosting.GitHubNotFound")),
            HttpStatusCode.UnprocessableEntity => new Failure(detail ?? Loc.Get("Hosting.GitHubRequestRefused")),
            _ => new Failure(Loc.Format("Hosting.StatusReply", "GitHub", (int)response.StatusCode, response.ReasonPhrase) + (detail is null ? string.Empty : " " + detail)),
        };
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "-";

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "GitHub refused a request ({Status}) {Path}: scopes [{Scopes}], token expiry {Expiry}, SSO {Sso}, {Detail}")]
    private static partial void LogRefused(
        Microsoft.Extensions.Logging.ILogger logger, int status, string path, string scopes, string expiry, string sso, string detail);

    // "message" of an error body, followed by the messages of its "errors" (e.g. "A pull request already exists for …").
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubError, cancellationToken);
            if (error is null) return null;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(error.Message)) parts.Add(error.Message.Trim().TrimEnd('.') + ".");
            if (error.Errors is { } details) parts.AddRange(details.Select(d => d.Message).Where(m => !string.IsNullOrWhiteSpace(m))!);
            return parts.Count == 0 ? null : string.Join(' ', parts);
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or NotSupportedException or TaskCanceledException)
        {
            return null;
        }
    }
}

internal sealed record GitHubAccount(
    [property: JsonPropertyName("login")] string? Login);

internal sealed record GitHubBranchSide(
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("repo")] GitHubRepositoryName? Repo,
    [property: JsonPropertyName("sha")] string? Sha = null);

internal sealed record GitHubRepositoryName(
    [property: JsonPropertyName("full_name")] string? FullName);

internal sealed record GitHubPull(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("user")] GitHubAccount? User,
    [property: JsonPropertyName("head")] GitHubBranchSide? Head,
    [property: JsonPropertyName("base")] GitHubBranchSide? Base,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("merged")] bool Merged = false,
    [property: JsonPropertyName("labels")] GitHubLabel[]? Labels = null,
    [property: JsonPropertyName("node_id")] string? NodeId = null,
    [property: JsonPropertyName("mergeable_state")] string? MergeableState = null,
    [property: JsonPropertyName("auto_merge")] GitHubAutoMerge? AutoMerge = null);

internal sealed record GitHubAutoMerge(
    [property: JsonPropertyName("merge_method")] string? MergeMethod);

internal sealed record GitHubPullMarker(
    [property: JsonPropertyName("url")] string? Url);

internal sealed record GitHubIssue(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("user")] GitHubAccount? User,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("pull_request")] GitHubPullMarker? PullRequest,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("state_reason")] string? StateReason = null,
    [property: JsonPropertyName("labels")] GitHubLabel[]? Labels = null,
    [property: JsonPropertyName("assignees")] GitHubAccount[]? Assignees = null,
    [property: JsonPropertyName("milestone")] GitHubMilestone? Milestone = null);

internal sealed record GitHubIssueSearch(
    [property: JsonPropertyName("items")] GitHubIssue[]? Items);

internal sealed record GitHubMilestone(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("number")] int Number = 0);

internal sealed record GitHubRepository(
    [property: JsonPropertyName("default_branch")] string? DefaultBranch);

internal sealed record GitHubNewPull(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("draft")] bool Draft);

internal sealed record GitHubErrorDetail(
    [property: JsonPropertyName("message")] string? Message);

internal sealed record GitHubError(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("errors")] GitHubErrorDetail[]? Errors);

[JsonSerializable(typeof(GitHubPull))]
[JsonSerializable(typeof(GitHubPull[]))]
[JsonSerializable(typeof(GitHubIssue[]))]
[JsonSerializable(typeof(GitHubIssue))]
[JsonSerializable(typeof(GitHubIssueSearch))]
[JsonSerializable(typeof(GitHubIssueEvent[]))]
[JsonSerializable(typeof(GitHubRename))]
[JsonSerializable(typeof(GitHubRepository))]
[JsonSerializable(typeof(GitHubUserRepository[]))]
[JsonSerializable(typeof(GitHubNewPull))]
[JsonSerializable(typeof(GitHubReviewersWrite))]
[JsonSerializable(typeof(GitHubLabelsWrite))]
[JsonSerializable(typeof(GitHubAssigneesWrite))]
[JsonSerializable(typeof(GitHubError))]
[JsonSerializable(typeof(GitHubCheckRuns))]
[JsonSerializable(typeof(GitHubCombinedStatus))]
[JsonSerializable(typeof(GitHubReview[]))]
[JsonSerializable(typeof(GitHubComment[]))]
[JsonSerializable(typeof(GitHubReviewComment[]))]
[JsonSerializable(typeof(GitHubPullCommit[]))]
[JsonSerializable(typeof(GitHubPullFile[]))]
[JsonSerializable(typeof(GitHubCompare))]
[JsonSerializable(typeof(GitHubComment))]
[JsonSerializable(typeof(GitHubCommentWrite))]
[JsonSerializable(typeof(GitHubStateWrite))]
[JsonSerializable(typeof(GitHubIssueWrite))]
[JsonSerializable(typeof(GitHubIssueStateWrite))]
[JsonSerializable(typeof(GitHubIssueTextWrite))]
[JsonSerializable(typeof(GitHubIssueMetadataWrite))]
[JsonSerializable(typeof(GitHubLabel[]))]
[JsonSerializable(typeof(GitHubAccount[]))]
[JsonSerializable(typeof(GitHubMilestone[]))]
[JsonSerializable(typeof(GitHubMergeWrite))]
[JsonSerializable(typeof(GitHubGraphQlRequest))]
[JsonSerializable(typeof(GitHubGraphQlResponse))]
[JsonSerializable(typeof(GitHubReactionWrite))]
[JsonSerializable(typeof(GitHubReaction[]))]
[JsonSerializable(typeof(GitHubReviewComment))]
[JsonSerializable(typeof(GitHubReviewWrite))]
[JsonSerializable(typeof(GitHubThreadsRequest))]
[JsonSerializable(typeof(GitHubThreadsResponse))]
[JsonSerializable(typeof(GitHubRequiredResponse))]
[JsonSerializable(typeof(GitHubSearchResult))]
[JsonSerializable(typeof(GitHubSearchItem))]
internal sealed partial class GitHubJsonContext;
