using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Whole old and new versions of a file of a pull request (J6.9g). Azure DevOps gives no patch: the app computes the diff
// from these two versions, read at the common ancestor and at the source commit of the last iteration (push).
public sealed partial class AzureDevOpsClient
{
    // Beyond this size a version is not fetched: there is no diff to show.
    private const int MaxSideBytes = 2 * 1024 * 1024;

    public bool SupportsPullRequestSides => true;

    public bool ComputesPullRequestDiffs => true;

    public async Task<GitResult<DiffSides>> GetPullRequestFileSidesAsync(
        string token, HostRepository repository, PullRequest pullRequest, PullRequestFile file, CancellationToken cancellationToken = default)
    {
        try
        {
            var pull = $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}";
            var iterations = await Single(token, $"{pull}/iterations?{ApiVersion}", AzureDevOpsJsonContext.Default.AzureIterationList, cancellationToken);
            if (iterations.Failure is { } failure) return Forward<DiffSides>(failure.Value);

            var last = (iterations.Body?.Value ?? []).OrderBy(i => i.Id).LastOrDefault();
            if (last?.SourceRefCommit?.CommitId is not { Length: > 0 } head || last.CommonRefCommit?.CommitId is not { Length: > 0 } baseSha)
                return new Failure(Loc.Get("Hosting.AzureNoCommits"));

            string original = string.Empty, modified = string.Empty;
            if (file.Kind != FileChangeKind.Added)
            {
                var old = await ReadContentAsync(token, repository, file.OldPath ?? file.Path, baseSha, cancellationToken);
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
            return Unreachable(e);
        }
    }

    // Raw text of a file at a commit; empty when the file does not exist there.
    private async Task<GitResult<string>> ReadContentAsync(
        string token, HostRepository repository, string path, string sha, CancellationToken cancellationToken)
    {
        var url = $"{RepositoryApi(repository)}/items?path={Uri.EscapeDataString("/" + path.TrimStart('/'))}"
            + $"&versionDescriptor.versionType=commit&versionDescriptor.version={sha}&download=true&{ApiVersion}";
        using var request = NewRequest(HttpMethod.Get, url, token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new Success<string>(string.Empty);
        if (!Accepted(response)) return await RejectAsync<string>(response, cancellationToken);
        if (response.Content.Headers.ContentLength > MaxSideBytes) return new Failure(Loc.Get("Hosting.FileTooLarge"));
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaxSideBytes) return new Failure(Loc.Get("Hosting.FileTooLarge"));
        return bytes.AsSpan().Contains((byte)0)
            ? new Failure(Loc.Get("Hosting.FileBinary"))
            : new Success<string>(System.Text.Encoding.UTF8.GetString(bytes));
    }

    private static GitResult<T> Forward<T>(object? other) => other switch
    {
        AuthenticationFailed a => a,
        Failure f => f,
        _ => Canceled.Instance,
    };
}
