using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Repositories of the signed-in account, for the clone dialog (J6.13, ADR-077): /user/repos, most recently pushed first,
// followed page after page through the Link header.
public sealed partial class GitHubClient
{
    private const int RepositoryPageSize = 100;
    private const int MaxRepositoryPages = 10;

    public bool SupportsRepositoryListing => true;

    public async Task<GitResult<HostedRepositoryList>> ListRepositoriesAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var items = new List<HostedRepository>();
            string? url = $"{ApiRoot}/user/repos?affiliation=owner,collaborator,organization_member&sort=pushed&per_page={RepositoryPageSize}";
            var truncated = false;
            for (var page = 1; url is not null; page++)
            {
                if (page > MaxRepositoryPages)
                {
                    truncated = true;
                    break;
                }

                using var request = NewRequest(HttpMethod.Get, url, token);
                using var response = await http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return await RejectAsync<HostedRepositoryList>(response, cancellationToken);

                var body = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubUserRepositoryArray, cancellationToken) ?? [];
                items.AddRange(body.Where(r => r.FullName is { Length: > 0 } && r.CloneUrl is { Length: > 0 }).Select(ToHostedRepository));
                url = NextPage(response);
            }
            return new Success<HostedRepositoryList>(new HostedRepositoryList(items, truncated));
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

    private static HostedRepository ToHostedRepository(GitHubUserRepository r) => new(
        r.FullName!, string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(), r.Private, r.Fork, r.CloneUrl!,
        string.IsNullOrEmpty(r.SshUrl) ? null : r.SshUrl, r.PushedAt, r.Owner?.Login);

    // <https://api.github.com/user/repos?page=2>; rel="next", <…>; rel="last". Only an address of the API itself is followed:
    // the token goes with it.
    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in string.Join(',', values).Split(','))
        {
            var pieces = part.Split(';');
            if (pieces.Length < 2 || !pieces.Skip(1).Any(p => p.Trim().Equals("rel=\"next\"", StringComparison.Ordinal))) continue;
            var address = pieces[0].Trim().Trim('<', '>');
            return address.StartsWith(ApiRoot + "/", StringComparison.Ordinal) ? address : null;
        }
        return null;
    }
}

internal sealed record GitHubUserRepository(
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("private")] bool Private,
    [property: JsonPropertyName("fork")] bool Fork,
    [property: JsonPropertyName("clone_url")] string? CloneUrl,
    [property: JsonPropertyName("ssh_url")] string? SshUrl,
    [property: JsonPropertyName("pushed_at")] DateTimeOffset? PushedAt,
    [property: JsonPropertyName("owner")] GitHubAccount? Owner);
