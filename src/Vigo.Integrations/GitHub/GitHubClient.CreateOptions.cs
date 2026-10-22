using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// What a new pull request can carry (J6.9m): GitHub takes the title, body and draft state at creation; labels and reviewers
// follow in separate calls (ApplyPullRequestCreateOptionsAsync) and linked issues travel as closing keywords in the body.
public sealed partial class GitHubClient
{
    public bool SupportsPullRequestCreateOptions => true;

    // The people that can be asked to review are the ones the repository can assign (collaborators and, for an organization,
    // its members with access). The signed-in account is left out: GitHub refuses to ask the author of a pull request.
    public async Task<GitResult<PullRequestCreateOptions>> GetPullRequestCreateOptionsAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default)
    {
        try
        {
            var people = await OptionPages(token, $"{RepositoryUrl(repository)}/assignees?per_page=100", GitHubJsonContext.Default.GitHubAccountArray, cancellationToken);
            if (people.Failure is { } failure) return failure.Value is Failure f ? f : failure.Value is AuthenticationFailed a ? a : Canceled.Instance;

            var own = await GetOwnLoginAsync(token, cancellationToken);
            // Anyone assignable can be assigned, the signed-in account included ("assign to myself"); reviewers exclude it.
            var assignees = people.Items
                .Select(a => a.Login)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(login => new PullRequestReviewerOption(login, login))
                .ToList();
            var reviewers = assignees.Where(a => !string.Equals(a.Id, own, StringComparison.OrdinalIgnoreCase)).ToList();
            return new Success<PullRequestCreateOptions>(new PullRequestCreateOptions(reviewers, people.Truncated, SupportsRequiredReviewers: false, assignees));
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

    // Labels first, then reviewers; both are attempted and the refusals are reported together (the pull request exists anyway).
    public async Task<GitResult<Done>> ApplyPullRequestCreateOptionsAsync(
        string token, HostRepository repository, PullRequest created, NewPullRequest request, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        if (request.Labels is { Count: > 0 } labels)
        {
            // POST /issues/{n}/labels adds to the existing labels and creates the ones the repository does not know yet.
            var result = await SendAsync(HttpMethod.Post, $"{RepositoryUrl(repository)}/issues/{created.Number}/labels", token,
                JsonContent.Create(new GitHubLabelsWrite([.. labels]), GitHubJsonContext.Default.GitHubLabelsWrite), DoneAsync, cancellationToken);
            if (Refusal(result, Loc.Get("Hosting.PartLabels")) is { } why) { if (result is Canceled) return result; problems.Add(why); }
        }
        if (request.Reviewers is { Count: > 0 } reviewers)
        {
            var result = await SendAsync(HttpMethod.Post, $"{RepositoryUrl(repository)}/pulls/{created.Number}/requested_reviewers", token,
                JsonContent.Create(new GitHubReviewersWrite([.. reviewers.Select(r => r.Id)]), GitHubJsonContext.Default.GitHubReviewersWrite),
                DoneAsync, cancellationToken);
            if (Refusal(result, Loc.Get("Hosting.PartReviewers")) is { } why) { if (result is Canceled) return result; problems.Add(why); }
        }
        if (request.Assignees is { Count: > 0 } assignees)
        {
            var result = await SendAsync(HttpMethod.Post, $"{RepositoryUrl(repository)}/issues/{created.Number}/assignees", token,
                JsonContent.Create(new GitHubAssigneesWrite([.. assignees]), GitHubJsonContext.Default.GitHubAssigneesWrite), DoneAsync, cancellationToken);
            if (Refusal(result, Loc.Get("Hosting.PartAssignees")) is { } why) { if (result is Canceled) return result; problems.Add(why); }
        }
        return problems.Count == 0 ? new Success<Done>(Done.Instance) : new Failure(string.Join(' ', problems));
    }

    private static string? Refusal(GitResult<Done> result, string what) => result switch
    {
        Failure f => Loc.Format("Hosting.PartFailed", what, f.Message),
        AuthenticationFailed a => Loc.Format("Hosting.PartFailed", what, a.Message),
        _ => null,
    };

    // "Closes #12" lines make GitHub link the issue to the pull request (and close it when the pull request is merged).
    internal static string WithClosingKeywords(string body, IReadOnlyList<int>? issues)
    {
        if (issues is not { Count: > 0 }) return body;

        var keywords = string.Join(", ", issues.Distinct().Select(n => $"Closes #{n}"));
        return body.Length == 0 ? keywords : $"{body}\n\n{keywords}";
    }

    private async Task<string?> GetOwnLoginAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, UserUrl, token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            return (await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubUser, cancellationToken))?.Login;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }
}

internal sealed record GitHubReviewersWrite(
    [property: JsonPropertyName("reviewers")] string[] Reviewers);

internal sealed record GitHubAssigneesWrite(
    [property: JsonPropertyName("assignees")] string[] Assignees);

internal sealed record GitHubLabelsWrite(
    [property: JsonPropertyName("labels")] string[] Labels);
