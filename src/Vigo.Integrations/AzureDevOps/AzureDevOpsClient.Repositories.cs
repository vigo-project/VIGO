using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Repositories of the account, for the clone dialog (J6.13, ADR-077): the organizations the account belongs to (accounts API,
// keyed by the profile id), then the repositories of every project of each one. A token limited to one organization cannot
// list the organizations: the call then fails with the sign-in message.
public sealed partial class AzureDevOpsClient
{
    private const string AccountsUrl = "https://app.vssps.visualstudio.com/_apis/accounts?memberId=";

    public bool SupportsRepositoryListing => true;

    public Task<GitResult<HostedRepositoryList>> ListRepositoriesAsync(string token, CancellationToken cancellationToken = default) =>
        ListRepositoriesAsync(token, null, cancellationToken);

    // A token created for one organization is refused by the account-wide endpoints below: its organization, kept with the account,
    // is listed directly. Otherwise a refusal says so, rather than blaming an expired token.
    public async Task<GitResult<HostedRepositoryList>> ListRepositoriesAsync(string token, string? organization, CancellationToken cancellationToken = default)
    {
        try
        {
            token = AccessTokenOf(token);
            if (!string.IsNullOrWhiteSpace(organization))
            {
                var only = await ListOrganizationRepositoriesAsync(token, organization.Trim(), cancellationToken);
                GitResult<HostedRepositoryList> single = only switch
                {
                    Success<IReadOnlyList<HostedRepository>> { Value: var found } => new Success<HostedRepositoryList>(new HostedRepositoryList([.. found.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)], false)),
                    AuthenticationFailed a => a,
                    Failure f => f,
                    Conflict c => c,
                    _ => Canceled.Instance,
                };
                return single;
            }
            using var profileRequest = NewRequest(HttpMethod.Get, ProfileUrl, token);
            using var profileResponse = await http.SendAsync(profileRequest, cancellationToken);
            if (!Accepted(profileResponse)) return NeedsOrganization(await RejectAsync<HostedRepositoryList>(profileResponse, cancellationToken));
            var profile = await profileResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureProfile, cancellationToken);
            if (string.IsNullOrEmpty(profile?.Id)) return new Failure(Loc.Get("Hosting.AzureUnreadable"));

            using var accountsRequest = NewRequest(HttpMethod.Get, $"{AccountsUrl}{Uri.EscapeDataString(profile.Id)}&{ApiVersion}", token);
            using var accountsResponse = await http.SendAsync(accountsRequest, cancellationToken);
            if (!Accepted(accountsResponse)) return NeedsOrganization(await RejectAsync<HostedRepositoryList>(accountsResponse, cancellationToken));
            var accounts = await accountsResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureAccountList, cancellationToken);
            var organizations = (accounts?.Value ?? []).Select(a => a.AccountName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var items = new List<HostedRepository>();
            GitResult<HostedRepositoryList>? firstError = null;
            foreach (var name in organizations)
            {
                var listed = await ListOrganizationRepositoriesAsync(token, name!, cancellationToken);
                switch (listed)
                {
                    case Success<IReadOnlyList<HostedRepository>> { Value: var found }:
                        items.AddRange(found);
                        break;
                    case Canceled c: return c;
                    case AuthenticationFailed a: firstError ??= a; break;
                    case Failure f: firstError ??= f; break;
                    case Conflict c: firstError ??= c; break;
                }
            }
            // An organization that refuses the token does not hide the others; all of them failing does.
            if (items.Count == 0 && firstError is not null) return firstError.Value;
            return new Success<HostedRepositoryList>(new HostedRepositoryList([.. items.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)], false));
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

    private static GitResult<HostedRepositoryList> NeedsOrganization(GitResult<HostedRepositoryList> rejected) =>
        rejected.Value is AuthenticationFailed ? new AuthenticationFailed(Remote, Loc.Get("Hosting.AzureListNeedsOrganization")) : rejected;

    private async Task<GitResult<IReadOnlyList<HostedRepository>>> ListOrganizationRepositoriesAsync(
        string token, string organization, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, $"https://{Host}/{Uri.EscapeDataString(organization)}/_apis/git/repositories?{ApiVersion}", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!Accepted(response)) return await RejectAsync<IReadOnlyList<HostedRepository>>(response, cancellationToken);

        var list = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureRepositoryList, cancellationToken);
        IReadOnlyList<HostedRepository> repositories =
        [
            .. (list?.Value ?? [])
                .Where(r => r is { IsDisabled: false, Name.Length: > 0, RemoteUrl.Length: > 0 })
                .Select(r => new HostedRepository(
                    $"{organization}/{r.Project?.Name ?? "?"}/{r.Name}", null, r.Project?.Visibility is not "public", r.IsFork,
                    r.RemoteUrl!, string.IsNullOrEmpty(r.SshUrl) ? null : r.SshUrl, null, $"{organization}/{r.Project?.Name ?? "?"}")),
        ];
        return new Success<IReadOnlyList<HostedRepository>>(repositories);
    }
}

internal sealed record AzureAccount(
    [property: JsonPropertyName("accountName")] string? AccountName);

internal sealed record AzureAccountList(
    [property: JsonPropertyName("value")] AzureAccount[]? Value);

internal sealed record AzureRepositoryProject(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("visibility")] string? Visibility);

internal sealed record AzureListedRepository(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("remoteUrl")] string? RemoteUrl,
    [property: JsonPropertyName("sshUrl")] string? SshUrl,
    [property: JsonPropertyName("isDisabled")] bool IsDisabled,
    [property: JsonPropertyName("isFork")] bool IsFork,
    [property: JsonPropertyName("project")] AzureRepositoryProject? Project);

internal sealed record AzureRepositoryList(
    [property: JsonPropertyName("value")] AzureListedRepository[]? Value);
