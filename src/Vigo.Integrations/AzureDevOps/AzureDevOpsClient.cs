using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

/// <summary>
/// Azure DevOps (J6.4, ADR-046): pull requests, work items and pull request creation through the REST API 7.1. Sign-in
/// with a personal access token (Basic) or, once a client id is configured, the Microsoft Entra ID device flow (Bearer,
/// ADR-047). A repository is "organization/project" + name.
/// </summary>
/// <param name="http">Shared client; no base address or headers expected.</param>
/// <param name="clientId">Client id of VIGO's Entra ID application (public client); empty when none is registered.</param>
/// <param name="time">Clock for the device code's expiry and the access token's lifetime.</param>
public sealed partial class AzureDevOpsClient(HttpClient http, string? clientId = null, TimeProvider? time = null) : IHostingClient
{
    private const string Host = "dev.azure.com";
    private const string ApiVersion = "api-version=7.1";
    private const string ProfileUrl = "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?" + ApiVersion;
    private const int PageSize = 50;
    private const string Remote = "azure-devops";

    public HostingProvider Provider => HostingProvider.AzureDevOps;

    public Task<GitResult<HostUser>> GetUserAsync(string token, CancellationToken cancellationToken = default) =>
        GetUserAsync(token, null, cancellationToken);

    /// <summary>
    /// With an organization, the token is checked against that organization's connection data: a token limited to one
    /// organization is refused by the account-wide profile endpoint, whatever its scopes.
    /// </summary>
    public async Task<GitResult<HostUser>> GetUserAsync(string token, string? organization, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(organization)) return await GetOrganizationUserAsync(token, organization.Trim(), cancellationToken);
        try
        {
            using var request = NewRequest(HttpMethod.Get, ProfileUrl, AccessTokenOf(token));
            using var response = await http.SendAsync(request, cancellationToken);
            if (IsSignInPage(response) || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new AuthenticationFailed(Remote, Loc.Get("Hosting.AzureTokenRefused"));
            if (!response.IsSuccessStatusCode) return await RejectAsync<HostUser>(response, cancellationToken);

            var profile = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureProfile, cancellationToken);
            var login = !string.IsNullOrWhiteSpace(profile?.EmailAddress) ? profile.EmailAddress : profile?.DisplayName;
            return string.IsNullOrWhiteSpace(login)
                ? new Failure(Loc.Get("Hosting.AzureUnreadable"))
                : new Success<HostUser>(new HostUser(login, string.IsNullOrWhiteSpace(profile?.DisplayName) ? null : profile.DisplayName, null));
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

    private static string ConnectionDataUrl(string organization) =>
        $"https://{Host}/{Uri.EscapeDataString(organization)}/_apis/connectionData?connectOptions=none&lastChangeId=-1&lastChangeId64=-1&api-version=7.1-preview";

    private async Task<GitResult<HostUser>> GetOrganizationUserAsync(string token, string organization, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, ConnectionDataUrl(organization), AccessTokenOf(token));
            using var response = await http.SendAsync(request, cancellationToken);
            if (IsSignInPage(response) || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new AuthenticationFailed(Remote, Loc.Format("Hosting.AzureTokenRefusedOrg", organization));
            if (!response.IsSuccessStatusCode) return await RejectAsync<HostUser>(response, cancellationToken);

            var data = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureConnectionData, cancellationToken);
            var user = data?.AuthenticatedUser;
            var name = user?.CustomDisplayName is { Length: > 0 } custom ? custom : user?.ProviderDisplayName;
            var login = user?.Properties?.Account?.Value is { Length: > 0 } account ? account : name;
            return string.IsNullOrWhiteSpace(login)
                ? new Failure(Loc.Get("Hosting.AzureUnreadable"))
                : new Success<HostUser>(new HostUser(login, string.IsNullOrWhiteSpace(name) ? null : name, null));
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

    /// <summary>
    /// <c>https://dev.azure.com/org/project/_git/repo</c> (also with a <c>org@</c> user), the legacy
    /// <c>https://org.visualstudio.com/[DefaultCollection/]project/_git/repo</c> and SSH
    /// <c>git@ssh.dev.azure.com:v3/org/project/repo</c>.
    /// </summary>
    public HostRepository? ParseRepository(RemoteUrl url)
    {
        var host = url.Host.ToLowerInvariant();
        var parts = url.DisplayPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

        if (host is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com")
            return parts is ["v3", var org, var project, var repo] ? Make(org, project, repo) : null;

        if (host == Host)
            return parts is [var org, var project, "_git", var repo] ? Make(org, project, repo) : null;

        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var org = host[..^".visualstudio.com".Length];
            if (org.Length == 0 || org.Contains('.', StringComparison.Ordinal)) return null;
            return parts switch
            {
                [var project, "_git", var repo] => Make(org, project, repo),
                ["DefaultCollection", var project, "_git", var repo] => Make(org, project, repo),
                _ => null,
            };
        }
        return null;

        static HostRepository Make(string org, string project, string repo) =>
            new(HostingProvider.AzureDevOps, Host, $"{org}/{project}", repo);
    }

    public async Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{RepositoryApi(repository)}/pullrequests?searchCriteria.status=active&$top={PageSize}&{ApiVersion}";
            using var request = NewRequest(HttpMethod.Get, url, token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<IReadOnlyList<PullRequest>>(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzurePullList, cancellationToken);
            IReadOnlyList<PullRequest> items = [.. (body?.Value ?? []).Select(p => ToPullRequest(repository, p))
                .OrderByDescending(p => p.UpdatedAt)];
            return new Success<IReadOnlyList<PullRequest>>(items);
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

    /// <summary>Open work items of the project (anything not Closed, Done or Removed), most recently changed first.</summary>
    public async Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            var projectApi = ProjectApi(repository);
            using var query = NewRequest(HttpMethod.Post, $"{projectApi}/wit/wiql?$top={PageSize}&{ApiVersion}", token);
            query.Content = JsonContent.Create(
                new AzureWiql("Select [System.Id] From WorkItems Where [System.TeamProject] = @project "
                    + "And [System.State] Not In ('Closed', 'Done', 'Removed') Order By [System.ChangedDate] Desc"),
                AzureDevOpsJsonContext.Default.AzureWiql);
            using var queryResponse = await http.SendAsync(query, cancellationToken);
            if (!Accepted(queryResponse)) return await RejectAsync<IReadOnlyList<Issue>>(queryResponse, cancellationToken);

            var refs = await queryResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureWiqlResult, cancellationToken);
            var ids = (refs?.WorkItems ?? []).Select(w => w.Id).Take(PageSize).ToArray();
            if (ids.Length == 0) return new Success<IReadOnlyList<Issue>>([]);

            var fields = "System.Id,System.Title,System.CreatedBy,System.ChangedDate,System.WorkItemType";
            using var items = NewRequest(HttpMethod.Get, $"{projectApi}/wit/workitems?ids={string.Join(',', ids)}&fields={fields}&{ApiVersion}", token);
            using var itemsResponse = await http.SendAsync(items, cancellationToken);
            if (!Accepted(itemsResponse)) return await RejectAsync<IReadOnlyList<Issue>>(itemsResponse, cancellationToken);

            var list = await itemsResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureWorkItemList, cancellationToken);
            IReadOnlyList<Issue> result = [.. (list?.Value ?? []).Select(w => ToIssue(repository, w))
                .OrderByDescending(i => i.UpdatedAt)];
            return new Success<IReadOnlyList<Issue>>(result);
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

    public async Task<GitResult<string>> GetDefaultBranchAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, $"{RepositoryApi(repository)}?{ApiVersion}", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<string>(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureRepository, cancellationToken);
            return body is { DefaultBranch.Length: > 0 }
                ? new Success<string>(BranchName(body.DefaultBranch))
                : new Failure(Loc.Get("Hosting.AzureNoDefaultBranch"));
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

    public async Task<GitResult<PullRequest>> CreatePullRequestAsync(
        string token, HostRepository repository, NewPullRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = NewRequest(HttpMethod.Post, $"{RepositoryApi(repository)}/pullrequests?{ApiVersion}", token);
            message.Content = JsonContent.Create(
                new AzureNewPull("refs/heads/" + request.Head, "refs/heads/" + request.Base, request.Title, request.Body, request.IsDraft,
                    NewReviewers(request), NewLabels(request), NewWorkItems(request)),
                AzureDevOpsJsonContext.Default.AzureNewPull);
            using var response = await http.SendAsync(message, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<PullRequest>(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzurePull, cancellationToken);
            return body is null ? new Failure(Loc.Get("Hosting.AzureUnreadable")) : new Success<PullRequest>(ToPullRequest(repository, body));
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

    // "organization/project" is escaped segment by segment.
    private static string ProjectApi(HostRepository repository)
    {
        var parts = repository.Owner.Split('/', 2);
        return $"https://{repository.Host}/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts.Length > 1 ? parts[1] : string.Empty)}/_apis";
    }

    private static string RepositoryApi(HostRepository repository) =>
        $"{ProjectApi(repository)}/git/repositories/{Uri.EscapeDataString(repository.Name)}";

    private static string BranchName(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    private static PullRequest ToPullRequest(HostRepository repository, AzurePull p) => new(
        p.PullRequestId, p.Title, p.CreatedBy?.DisplayName ?? "?", BranchName(p.SourceRefName ?? string.Empty), repository.FullName,
        BranchName(p.TargetRefName ?? string.Empty), p.IsDraft, p.CreationDate, $"{repository.WebUrl}/pullrequest/{p.PullRequestId}",
        ProjectId: p.Repository?.Project?.Id, Review: ReviewOf(p.Reviewers));

    private static Issue ToIssue(HostRepository repository, AzureWorkItem w) => new(
        w.Id, w.Fields?.Title ?? $"#{w.Id}", w.Fields?.CreatedBy?.DisplayName ?? "?", w.Fields?.ChangedDate ?? default,
        $"https://{repository.Host}/{repository.Owner}/_workitems/edit/{w.Id}", w.Fields?.WorkItemType);

    // A personal access token goes as the password of a basic authentication with an empty user name; an Entra ID access
    // token (a JWT) as a bearer.
    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = IsJwt(token)
            ? new AuthenticationHeaderValue("Bearer", token)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VIGO", "0.1"));
        return request;
    }

    // J6.16: the same schemes as the REST calls, for git over HTTPS on dev.azure.com and the organizations' visualstudio.com hosts.
    public GitHttpHeader? GitHttpHeaderOf(string accessToken) => new(
        HostingProvider.AzureDevOps, ["https://dev.azure.com/", "https://*.visualstudio.com/"],
        IsJwt(accessToken)
            ? "Authorization: Bearer " + accessToken
            : "Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + accessToken)));

    // Azure DevOps answers an invalid token with 203 and the HTML sign-in page rather than with 401.
    private static bool IsSignInPage(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.NonAuthoritativeInformation
        || response.Content.Headers.ContentType?.MediaType == "text/html";

    private static bool Accepted(HttpResponseMessage response) => response.IsSuccessStatusCode && !IsSignInPage(response);

    private static Failure Unreachable(Exception e) => new(Loc.Format("Hosting.Unreachable", "Azure DevOps", e.Message), Exception: e);

    private static async Task<GitResult<T>> RejectAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (IsSignInPage(response))
            return new AuthenticationFailed(Remote, Loc.Get("Hosting.AzureTokenExpired"));

        var detail = await ReadErrorAsync(response, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new AuthenticationFailed(Remote, Loc.Get("Hosting.AzureTokenExpired")),
            HttpStatusCode.Forbidden => new AuthenticationFailed(Remote,
                Loc.Get("Hosting.AzureForbidden")),
            HttpStatusCode.NotFound => new Failure(Loc.Get("Hosting.AzureNotFound")),
            HttpStatusCode.TooManyRequests => new Failure(Loc.Get("Hosting.AzureRateLimit")),
            _ => new Failure(detail ?? Loc.Format("Hosting.StatusReply", "Azure DevOps", (int)response.StatusCode, response.ReasonPhrase)),
        };
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureError, cancellationToken);
            return string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message.Trim();
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or NotSupportedException or TaskCanceledException)
        {
            return null;
        }
    }
}

internal sealed record AzureConnectionData(
    [property: JsonPropertyName("authenticatedUser")] AzureConnectionUser? AuthenticatedUser);

internal sealed record AzureConnectionUser(
    [property: JsonPropertyName("providerDisplayName")] string? ProviderDisplayName,
    [property: JsonPropertyName("customDisplayName")] string? CustomDisplayName,
    [property: JsonPropertyName("properties")] AzureConnectionProperties? Properties,
    [property: JsonPropertyName("id")] string? Id = null);

internal sealed record AzureConnectionProperties(
    [property: JsonPropertyName("Account")] AzureStringValue? Account);

internal sealed record AzureStringValue(
    [property: JsonPropertyName("$value")] string? Value);

internal sealed record AzureProfile(
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("emailAddress")] string? EmailAddress,
    [property: JsonPropertyName("id")] string? Id = null);

internal sealed record AzureIdentity(
    [property: JsonPropertyName("displayName")] string? DisplayName);

internal sealed record AzurePull(
    [property: JsonPropertyName("pullRequestId")] int PullRequestId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("createdBy")] AzureIdentity? CreatedBy,
    [property: JsonPropertyName("sourceRefName")] string? SourceRefName,
    [property: JsonPropertyName("targetRefName")] string? TargetRefName,
    [property: JsonPropertyName("isDraft")] bool IsDraft,
    [property: JsonPropertyName("creationDate")] DateTimeOffset CreationDate,
    [property: JsonPropertyName("repository")] AzurePullRepository? Repository = null,
    [property: JsonPropertyName("reviewers")] AzureReviewer[]? Reviewers = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("labels")] AzureLabel[]? Labels = null,
    [property: JsonPropertyName("lastMergeSourceCommit")] AzureCommitRef? LastMergeSourceCommit = null,
    [property: JsonPropertyName("mergeStatus")] string? MergeStatus = null,
    [property: JsonPropertyName("mergeFailureMessage")] string? MergeFailureMessage = null);

internal sealed record AzurePullList(
    [property: JsonPropertyName("value")] AzurePull[]? Value);

internal sealed record AzureRepository(
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch);

internal sealed record AzureNewPull(
    [property: JsonPropertyName("sourceRefName")] string SourceRefName,
    [property: JsonPropertyName("targetRefName")] string TargetRefName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("isDraft")] bool IsDraft,
    [property: JsonPropertyName("reviewers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureNewReviewer[]? Reviewers = null,
    [property: JsonPropertyName("labels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureNewLabel[]? Labels = null,
    [property: JsonPropertyName("workItemRefs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureWorkItemRef[]? WorkItemRefs = null);

internal sealed record AzureWiql(
    [property: JsonPropertyName("query")] string Query);

internal sealed record AzureWorkItemRef(
    [property: JsonPropertyName("id")] int Id);

internal sealed record AzureWiqlResult(
    [property: JsonPropertyName("workItems")] AzureWorkItemRef[]? WorkItems);

internal sealed record AzureWorkItemFields(
    [property: JsonPropertyName("System.Title")] string? Title,
    [property: JsonPropertyName("System.CreatedBy")] AzureIdentity? CreatedBy,
    [property: JsonPropertyName("System.ChangedDate")] DateTimeOffset? ChangedDate,
    [property: JsonPropertyName("System.WorkItemType")] string? WorkItemType = null,
    [property: JsonPropertyName("System.State")] string? State = null);

internal sealed record AzureWorkItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("fields")] AzureWorkItemFields? Fields);

internal sealed record AzureWorkItemList(
    [property: JsonPropertyName("value")] AzureWorkItem[]? Value);

internal sealed record AzureError(
    [property: JsonPropertyName("message")] string? Message);

[JsonSerializable(typeof(AzureProfile))]
[JsonSerializable(typeof(AzureConnectionData))]
[JsonSerializable(typeof(AzurePull))]
[JsonSerializable(typeof(AzurePullList))]
[JsonSerializable(typeof(AzureRepository))]
[JsonSerializable(typeof(AzureAccountList))]
[JsonSerializable(typeof(AzureRepositoryList))]
[JsonSerializable(typeof(AzureNewPull))]
[JsonSerializable(typeof(AzureTeamList))]
[JsonSerializable(typeof(AzureTeamMemberList))]
[JsonSerializable(typeof(AzureWiql))]
[JsonSerializable(typeof(AzureWiqlResult))]
[JsonSerializable(typeof(AzureWorkItemList))]
[JsonSerializable(typeof(AzureWorkItemFull))]
[JsonSerializable(typeof(AzureWorkItemCommentList))]
[JsonSerializable(typeof(AzureWorkItemUpdateList))]
[JsonSerializable(typeof(AzureWorkItemType))]
[JsonSerializable(typeof(AzureIdentityList))]
[JsonSerializable(typeof(AzureError))]
[JsonSerializable(typeof(AzureVote))]
[JsonSerializable(typeof(AzureStatusList))]
[JsonSerializable(typeof(AzurePolicyList))]
[JsonSerializable(typeof(AzureThreadList))]
[JsonSerializable(typeof(AzureThreadComment))]
[JsonSerializable(typeof(AzureThread))]
[JsonSerializable(typeof(AzurePullPatch))]
[JsonSerializable(typeof(AzureNewComment))]
[JsonSerializable(typeof(AzureThreadStatus))]
[JsonSerializable(typeof(AzureNewThread))]
[JsonSerializable(typeof(AzureCommitList))]
[JsonSerializable(typeof(AzureIterationList))]
[JsonSerializable(typeof(AzureChangeList))]
[JsonSerializable(typeof(AzureDeviceCode))]
[JsonSerializable(typeof(AzureTokenResponse))]
internal sealed partial class AzureDevOpsJsonContext : JsonSerializerContext;
