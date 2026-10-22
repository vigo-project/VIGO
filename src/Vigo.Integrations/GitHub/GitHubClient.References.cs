using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Completion of #123 (issue or pull request), !45 (pull request) and @login while a comment is typed (J6.12). Issues and pull
// requests go through the search API (title), people through the assignable accounts of the repository. A mention is plain text
// on GitHub: nothing to resolve when a comment is read.
public sealed partial class GitHubClient
{
    private const int MaxSuggestions = 10;
    private static readonly TimeSpan PeopleLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string[] Logins)> _logins = new();

    public bool SupportsReferenceSearch => true;

    public async Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchReferencesAsync(
        string token, HostRepository repository, ReferenceKind kind, string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (kind == ReferenceKind.Person) return await SearchPeopleAsync(token, repository, query, cancellationToken);

        // The search API has no number lookup: a typed number is read directly, and the title search follows.
        var found = new List<ReferenceSuggestion>();
        if (int.TryParse(query, out var number))
        {
            var direct = await SendAsync<GitHubSearchItem>(HttpMethod.Get, $"{RepositoryUrl(repository)}/issues/{number}", token, null,
                async (response, ct) => await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubSearchItem, ct) is { } item
                    ? new Success<GitHubSearchItem>(item)
                    : new Failure(Loc.Get("Hosting.GitHubUnreadable")), cancellationToken);
            if (direct.Value is Success<GitHubSearchItem> { Value: var one } && (kind == ReferenceKind.Issue || one.PullRequest is not null)) found.Add(ToSuggestion(one));
            else if (direct.Value is Canceled) return Canceled.Instance;
        }

        var text = query.Length == 0 || number > 0 && query == number.ToString(System.Globalization.CultureInfo.InvariantCulture) ? string.Empty : $" {query} in:title";
        var type = kind == ReferenceKind.PullRequest ? " is:pr" : string.Empty;
        var q = Uri.EscapeDataString($"repo:{repository.Owner}/{repository.Name}{type}{text}");
        var searched = await SendAsync<IReadOnlyList<ReferenceSuggestion>>(HttpMethod.Get,
            $"{ApiRoot}/search/issues?q={q}&sort=updated&order=desc&per_page={MaxSuggestions}", token, null,
            async (response, ct) =>
            {
                var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubSearchResult, ct);
                return body is null
                    ? new Failure(Loc.Get("Hosting.GitHubUnreadable"))
                    : new Success<IReadOnlyList<ReferenceSuggestion>>([.. (body.Items ?? []).Select(ToSuggestion)]);
            }, cancellationToken);
        if (searched.Value is not Success<IReadOnlyList<ReferenceSuggestion>> { Value: var items })
            return found.Count > 0 && searched.Value is Failure ? new Success<IReadOnlyList<ReferenceSuggestion>>(found) : searched;

        IReadOnlyList<ReferenceSuggestion> merged = [.. found.Concat(items.Where(i => found.All(f => f.Insert != i.Insert))).Take(MaxSuggestions)];
        return new Success<IReadOnlyList<ReferenceSuggestion>>(merged);
    }

    private static ReferenceSuggestion ToSuggestion(GitHubSearchItem item) => new(
        item.Title ?? $"#{item.Number}",
        $"#{item.Number} · {(item.PullRequest is not null ? "PR" : "issue")} · {item.State}",
        $"#{item.Number}");

    private async Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchPeopleAsync(
        string token, HostRepository repository, string query, CancellationToken cancellationToken)
    {
        string[] logins;
        var key = repository.FullName;
        if (_logins.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < PeopleLifetime) logins = cached.Logins;
        else
        {
            var people = await OptionPages(token, $"{RepositoryUrl(repository)}/assignees?per_page=100", GitHubJsonContext.Default.GitHubAccountArray, cancellationToken);
            if (people.Failure is { } failure) return failure.Value is Failure f ? f : failure.Value is AuthenticationFailed a ? a : Canceled.Instance;
            logins = [.. people.Items.Select(a => a.Login).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
            _logins[key] = (DateTimeOffset.UtcNow, logins);
        }
        IReadOnlyList<ReferenceSuggestion> result = [.. logins
            .Where(l => query.Length == 0 || l.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(MaxSuggestions)
            .Select(l => new ReferenceSuggestion(l, string.Empty, "@" + l))];
        return new Success<IReadOnlyList<ReferenceSuggestion>>(result);
    }
}

internal sealed record GitHubSearchItem(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("pull_request")] GitHubSearchPullMarker? PullRequest);

internal sealed record GitHubSearchPullMarker(
    [property: JsonPropertyName("url")] string? Url);

internal sealed record GitHubSearchResult(
    [property: JsonPropertyName("items")] GitHubSearchItem[]? Items);
