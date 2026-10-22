using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Whole old and new versions of a file of a pull request (side-by-side diff): read at the merge base and at the head commit.
public sealed partial class GitHubClient
{
    // Beyond this size a version is not fetched: the caller falls back to the hunks of the patch.
    private const int MaxSideBytes = 2 * 1024 * 1024;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _mergeBases = new();

    public bool SupportsPullRequestSides => true;

    public async Task<GitResult<DiffSides>> GetPullRequestFileSidesAsync(
        string token, HostRepository repository, PullRequest pullRequest, PullRequestFile file, CancellationToken cancellationToken = default)
    {
        if (pullRequest.HeadSha is not { Length: > 0 } head) return new Failure(Loc.Get("Hosting.GitHubNoHead"));
        try
        {
            string original = string.Empty, modified = string.Empty;
            if (file.Kind != FileChangeKind.Added)
            {
                var baseSha = await MergeBaseAsync(token, repository, pullRequest, head, cancellationToken);
                if (baseSha.Value is not Success<string> { Value: var sha }) return Forward<DiffSides>(baseSha.Value);
                var old = await ReadContentAsync(token, repository, file.OldPath ?? file.Path, sha, cancellationToken);
                if (old.Value is not Success<string> { Value: var oldText }) return Forward<DiffSides>(old.Value);
                original = oldText;
            }
            if (file.Kind != FileChangeKind.Deleted)
            {
                var current = await ReadContentAsync(token, repository, file.Path, head, cancellationToken);
                if (current.Value is not Success<string> { Value: var newText }) return Forward<DiffSides>(current.Value);
                modified = newText;
            }
            return new Success<DiffSides>(new DiffSides(file.Path, file.OldPath ?? file.Path, original, modified));
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

    // The diff of a pull request is taken against the merge base, not against the tip of the target branch.
    private async Task<GitResult<string>> MergeBaseAsync(
        string token, HostRepository repository, PullRequest pullRequest, string head, CancellationToken cancellationToken)
    {
        var key = $"{RepositoryUrl(repository)}#{pullRequest.BaseBranch}...{head}";
        if (_mergeBases.TryGetValue(key, out var known)) return new Success<string>(known);

        var url = $"{RepositoryUrl(repository)}/compare/{Uri.EscapeDataString(pullRequest.BaseBranch)}...{head}?per_page=1";
        using var request = NewRequest(HttpMethod.Get, url, token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return await RejectAsync<string>(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubCompare, cancellationToken);
        if (body?.MergeBaseCommit?.Sha is not { Length: > 0 } sha) return new Failure(Loc.Get("Hosting.GitHubNoMergeBase"));
        _mergeBases[key] = sha;
        return new Success<string>(sha);
    }

    // Raw text of a file at a commit; empty when the file does not exist there.
    private async Task<GitResult<string>> ReadContentAsync(
        string token, HostRepository repository, string path, string sha, CancellationToken cancellationToken)
    {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var request = NewRequest(HttpMethod.Get, $"{RepositoryUrl(repository)}/contents/{escaped}?ref={sha}", token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new Success<string>(string.Empty);
        if (!response.IsSuccessStatusCode) return await RejectAsync<string>(response, cancellationToken);
        if (response.Content.Headers.ContentLength > MaxSideBytes) return new Failure(Loc.Get("Hosting.FileTooLargeSideBySide"));
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length > MaxSideBytes
            ? new Failure(Loc.Get("Hosting.FileTooLargeSideBySide"))
            : new Success<string>(System.Text.Encoding.UTF8.GetString(bytes));
    }

    private static GitResult<T> Forward<T>(object? other) => other switch
    {
        AuthenticationFailed a => a,
        Failure f => f,
        _ => Canceled.Instance,
    };
}

internal sealed record GitHubCompareCommit(
    [property: JsonPropertyName("sha")] string? Sha);

internal sealed record GitHubCompare(
    [property: JsonPropertyName("merge_base_commit")] GitHubCompareCommit? MergeBaseCommit);
