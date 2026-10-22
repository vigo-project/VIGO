using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Integrations.AzureDevOps;

// Completion of #123 (work item), !45 (pull request) and @person while a comment is typed (J6.12). Work items go through a
// WIQL query, pull requests through the list of the repository, people through the members of the project's teams. A mention is
// stored as @<identity id> in the Markdown, which is what the web interface writes and shows as the person's name.
public sealed partial class AzureDevOpsClient
{
    private const int MaxSuggestions = 10;
    private static readonly TimeSpan PeopleLifetime = TimeSpan.FromMinutes(5);

    // People of a project, kept a few minutes: the menu asks at every keystroke.
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<PullRequestReviewerOption> People)> _people = new();

    public bool SupportsReferenceSearch => true;

    public async Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchReferencesAsync(
        string token, HostRepository repository, ReferenceKind kind, string query, CancellationToken cancellationToken = default)
    {
        try
        {
            return kind switch
            {
                ReferenceKind.Issue => await SearchWorkItemsAsync(token, repository, query.Trim(), cancellationToken),
                ReferenceKind.PullRequest => await SearchPullRequestsAsync(token, repository, query.Trim(), cancellationToken),
                _ => await SearchPeopleAsync(token, repository, query.Trim(), cancellationToken),
            };
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

    // The title contains the text, or the number is the one typed; the most recently changed first.
    private async Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchWorkItemsAsync(
        string token, HostRepository repository, string query, CancellationToken cancellationToken)
    {
        var projectApi = ProjectApi(repository);
        var where = "[System.TeamProject] = @project";
        if (query.Length > 0)
        {
            var text = query.Replace("'", "''", StringComparison.Ordinal);
            where += int.TryParse(query, out var number)
                ? $" And ([System.Id] = {number} Or [System.Title] Contains '{text}')"
                : $" And [System.Title] Contains '{text}'";
        }
        using var wiql = NewRequest(HttpMethod.Post, $"{projectApi}/wit/wiql?$top={MaxSuggestions}&{ApiVersion}", token);
        wiql.Content = JsonContent.Create(new AzureWiql($"Select [System.Id] From WorkItems Where {where} Order By [System.ChangedDate] Desc"), AzureDevOpsJsonContext.Default.AzureWiql);
        using var wiqlResponse = await http.SendAsync(wiql, cancellationToken);
        if (!Accepted(wiqlResponse)) return await RejectAsync<IReadOnlyList<ReferenceSuggestion>>(wiqlResponse, cancellationToken);

        var refs = await wiqlResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureWiqlResult, cancellationToken);
        var ids = (refs?.WorkItems ?? []).Select(w => w.Id).Take(MaxSuggestions).ToArray();
        if (ids.Length == 0) return new Success<IReadOnlyList<ReferenceSuggestion>>([]);

        var fields = "System.Id,System.Title,System.WorkItemType,System.State,System.ChangedDate";
        using var items = NewRequest(HttpMethod.Get, $"{projectApi}/wit/workitems?ids={string.Join(',', ids)}&fields={fields}&{ApiVersion}", token);
        using var itemsResponse = await http.SendAsync(items, cancellationToken);
        if (!Accepted(itemsResponse)) return await RejectAsync<IReadOnlyList<ReferenceSuggestion>>(itemsResponse, cancellationToken);

        var list = await itemsResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureWorkItemList, cancellationToken);
        IReadOnlyList<ReferenceSuggestion> result = [.. (list?.Value ?? [])
            .OrderByDescending(w => w.Fields?.ChangedDate)
            .Select(w => new ReferenceSuggestion(
                w.Fields?.Title ?? $"#{w.Id}", Detail($"#{w.Id}", w.Fields?.WorkItemType, w.Fields?.State), $"#{w.Id}"))];
        return new Success<IReadOnlyList<ReferenceSuggestion>>(result);
    }

    // The list of the repository has no title search: the latest pull requests, any state, filtered here.
    private async Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchPullRequestsAsync(
        string token, HostRepository repository, string query, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, $"{RepositoryApi(repository)}/pullrequests?searchCriteria.status=all&$top=100&{ApiVersion}", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!Accepted(response)) return await RejectAsync<IReadOnlyList<ReferenceSuggestion>>(response, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzurePullList, cancellationToken);
        IReadOnlyList<ReferenceSuggestion> result = [.. (body?.Value ?? [])
            .Where(p => query.Length == 0
                || p.PullRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal)
                || p.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(p => p.CreationDate)
            .Take(MaxSuggestions)
            .Select(p => new ReferenceSuggestion(p.Title, Detail($"!{p.PullRequestId}", p.CreatedBy?.DisplayName, p.Status), $"!{p.PullRequestId}"))];
        return new Success<IReadOnlyList<ReferenceSuggestion>>(result);
    }

    private async Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchPeopleAsync(
        string token, HostRepository repository, string query, CancellationToken cancellationToken)
    {
        var people = await PeopleAsync(token, repository, cancellationToken);
        if (people.Value is not Success<IReadOnlyList<PullRequestReviewerOption>> { Value: var all }) return CarryFailure<IReadOnlyList<PullRequestReviewerOption>, IReadOnlyList<ReferenceSuggestion>>(people);

        IReadOnlyList<ReferenceSuggestion> result = [.. all
            .Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(MaxSuggestions)
            .Select(p => new ReferenceSuggestion(p.Name, string.Empty, $"@<{p.Id}>"))];
        return new Success<IReadOnlyList<ReferenceSuggestion>>(result);
    }

    private async Task<GitResult<IReadOnlyList<PullRequestReviewerOption>>> PeopleAsync(
        string token, HostRepository repository, CancellationToken cancellationToken)
    {
        if (_people.TryGetValue(repository.FullName, out var cached) && DateTimeOffset.UtcNow - cached.At < PeopleLifetime)
            return new Success<IReadOnlyList<PullRequestReviewerOption>>(cached.People);

        var options = await GetPullRequestCreateOptionsAsync(token, repository, cancellationToken);
        if (options.Value is not Success<PullRequestCreateOptions> { Value: var read }) return CarryFailure<PullRequestCreateOptions, IReadOnlyList<PullRequestReviewerOption>>(options);
        _people[repository.FullName] = (DateTimeOffset.UtcNow, read.Reviewers);
        return new Success<IReadOnlyList<PullRequestReviewerOption>>(read.Reviewers);
    }

    private static GitResult<TOut> CarryFailure<TIn, TOut>(GitResult<TIn> result) => result.Value switch
    {
        Failure f => f,
        AuthenticationFailed a => a,
        _ => Canceled.Instance,
    };

    // Names of the identifiers a comment mentions: the project's people first, the organization's identity service for the others.
    public async Task<GitResult<IReadOnlyDictionary<string, string>>> ResolveMentionsAsync(
        string token, HostRepository repository, IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        try
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var people = await PeopleAsync(token, repository, cancellationToken);
            if (people.Value is Success<IReadOnlyList<PullRequestReviewerOption>> { Value: var known })
            {
                foreach (var person in known)
                {
                    if (ids.Contains(person.Id, StringComparer.OrdinalIgnoreCase)) names[person.Id] = person.Name;
                }
            }

            var missing = ids.Where(id => !names.ContainsKey(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
            if (missing.Length > 0 && repository.Host == Host)
            {
                var organization = Uri.EscapeDataString(repository.Owner.Split('/', 2)[0]);
                var url = $"https://vssps.dev.azure.com/{organization}/_apis/identities?identityIds={string.Join(',', missing)}&{ApiVersion}";
                using var request = NewRequest(HttpMethod.Get, url, token);
                using var response = await http.SendAsync(request, cancellationToken);
                if (Accepted(response))
                {
                    var found = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureIdentityList, cancellationToken);
                    foreach (var identity in found?.Value ?? [])
                    {
                        var name = identity.CustomDisplayName ?? identity.ProviderDisplayName;
                        if (identity.Id is { Length: > 0 } id && !string.IsNullOrEmpty(name)) names[id] = name;
                    }
                }
            }
            return new Success<IReadOnlyDictionary<string, string>>(names);
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

    // "#123 · Bug · Active": what the menu shows next to the title.
    private static string Detail(string number, params string?[] parts) =>
        string.Join(" · ", new[] { number }.Concat(parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!)));
}

internal sealed record AzureIdentityEntry(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("providerDisplayName")] string? ProviderDisplayName,
    [property: JsonPropertyName("customDisplayName")] string? CustomDisplayName);

internal sealed record AzureIdentityList(
    [property: JsonPropertyName("value")] AzureIdentityEntry[]? Value);
