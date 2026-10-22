using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Integrations.AzureDevOps;

// What a new pull request can carry (J6.9l): reviewers, labels and linked work items travel in the creation request itself
// (see CreatePullRequestAsync); this file reads the people that can be asked to review.
public sealed partial class AzureDevOpsClient
{
    private const int MaxTeams = 20;
    private const int MaxTeamMembers = 200;

    public bool SupportsPullRequestCreateOptions => true;

    // Azure DevOps has no repository-wide list of people: the candidates are the members of the project's teams (groups are left
    // out, a group cannot be asked as a person here). A team whose members cannot be read is skipped; only the list of teams is
    // required.
    public async Task<GitResult<PullRequestCreateOptions>> GetPullRequestCreateOptionsAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            var parts = repository.Owner.Split('/', 2);
            var projects = $"https://{repository.Host}/{Uri.EscapeDataString(parts[0])}/_apis/projects/{Uri.EscapeDataString(parts.Length > 1 ? parts[1] : string.Empty)}/teams";

            using var request = NewRequest(HttpMethod.Get, $"{projects}?$top={MaxTeams}&{ApiVersion}", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<PullRequestCreateOptions>(response, cancellationToken);

            AzureTeam[] teams = (await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureTeamList, cancellationToken))?.Value ?? [];
            var lists = await Task.WhenAll(teams.Where(t => !string.IsNullOrEmpty(t.Id)).Select(t => ReadTeamMembersAsync(token, projects, t.Id!, cancellationToken)));

            var people = lists
                .SelectMany(l => l.Members)
                .Where(m => m.Identity is { Id.Length: > 0, IsContainer: not true })
                .Select(m => new PullRequestReviewerOption(m.Identity!.Id!, m.Identity.DisplayName ?? m.Identity.UniqueName ?? m.Identity.Id!))
                .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var truncated = teams.Length >= MaxTeams || lists.Any(l => l.Members.Length >= MaxTeamMembers);
            return new Success<PullRequestCreateOptions>(new PullRequestCreateOptions(people, truncated));
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

    private async Task<AzureTeamMemberList> ReadTeamMembersAsync(string token, string teamsUrl, string teamId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, $"{teamsUrl}/{Uri.EscapeDataString(teamId)}/members?$top={MaxTeamMembers}&{ApiVersion}", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!Accepted(response)) return new AzureTeamMemberList([]);

            return await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureTeamMemberList, cancellationToken) ?? new AzureTeamMemberList([]);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new AzureTeamMemberList([]);
        }
    }

    private static AzureNewReviewer[]? NewReviewers(NewPullRequest request) =>
        request.Reviewers is { Count: > 0 } reviewers ? [.. reviewers.Select(r => new AzureNewReviewer(r.Id, r.IsRequired))] : null;

    private static AzureNewLabel[]? NewLabels(NewPullRequest request) =>
        request.Labels is { Count: > 0 } labels ? [.. labels.Select(l => new AzureNewLabel(l))] : null;

    private static AzureWorkItemRef[]? NewWorkItems(NewPullRequest request) =>
        request.LinkedIssues is { Count: > 0 } issues ? [.. issues.Select(i => new AzureWorkItemRef(i))] : null;
}

internal sealed record AzureTeam(
    [property: JsonPropertyName("id")] string? Id);

internal sealed record AzureTeamList(
    [property: JsonPropertyName("value")] AzureTeam[]? Value);

internal sealed record AzureTeamMemberIdentity(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("uniqueName")] string? UniqueName,
    [property: JsonPropertyName("isContainer")] bool? IsContainer);

internal sealed record AzureTeamMember(
    [property: JsonPropertyName("identity")] AzureTeamMemberIdentity? Identity);

internal sealed record AzureTeamMemberList(
    [property: JsonPropertyName("value")] AzureTeamMember[]? Value)
{
    public AzureTeamMember[] Members => Value ?? [];
}

internal sealed record AzureNewReviewer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("isRequired")] bool IsRequired);

internal sealed record AzureNewLabel(
    [property: JsonPropertyName("name")] string Name);
