using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Vote of the signed-in account on a pull request (J6.9h). Azure DevOps has votes, not review decisions.
public sealed partial class AzureDevOpsClient
{
    public bool SupportsPullRequestVote => true;

    public async Task<GitResult<Done>> SetPullRequestVoteAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewVote vote, CancellationToken cancellationToken = default)
    {
        try
        {
            // The reviewer is addressed by identity id, which only the organization's connection data gives for the token's owner.
            var organization = repository.Owner.Split('/', 2)[0];
            using var who = NewRequest(HttpMethod.Get, ConnectionDataUrl(organization), token);
            using var whoResponse = await http.SendAsync(who, cancellationToken);
            if (!Accepted(whoResponse)) return await RejectAsync<Done>(whoResponse, cancellationToken);
            var data = await whoResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureConnectionData, cancellationToken);
            if (data?.AuthenticatedUser?.Id is not { Length: > 0 } reviewerId) return new Failure(Loc.Get("Hosting.AzureUnreadable"));

            var url = $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}/reviewers/{Uri.EscapeDataString(reviewerId)}?{ApiVersion}";
            using var request = NewRequest(HttpMethod.Put, url, token);
            request.Content = JsonContent.Create(new AzureVote(VoteValue(vote)), AzureDevOpsJsonContext.Default.AzureVote);
            using var response = await http.SendAsync(request, cancellationToken);
            return Accepted(response) ? new Success<Done>(Done.Instance) : await RejectAsync<Done>(response, cancellationToken);
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

    internal static int VoteValue(ReviewVote vote) => vote switch
    {
        ReviewVote.Approved => 10,
        ReviewVote.ApprovedWithSuggestions => 5,
        ReviewVote.WaitingForAuthor => -5,
        ReviewVote.Rejected => -10,
        _ => 0,
    };
}

internal sealed record AzureVote(
    [property: JsonPropertyName("vote")] int Vote);
