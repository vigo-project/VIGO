using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Core.Services;

/// <summary>Secure storage of access tokens (Windows Credential Locker); one entry per provider and login.</summary>
public interface ITokenVault
{
    string? Retrieve(HostingProvider provider, string login, string profileId);

    void Store(HostingProvider provider, string login, string token, string profileId);

    void Remove(HostingProvider provider, string login, string profileId);
}

/// <summary>Outcome of one poll of the OAuth device flow.</summary>
/// <param name="Token">Access token once the user authorized VIGO; null while the authorization is pending.</param>
/// <param name="SlowDown">The service asks for a longer delay between polls.</param>
public sealed record DevicePoll(string? Token, bool SlowDown = false);

/// <summary>Token ready for an API call, and the vault value to save when renewing changed it.</summary>
public sealed record ResolvedToken(string AccessToken, string? UpdatedStored);

/// <summary>Calls of one hosting service that sign-in needs (J6.1). Results reuse <see cref="GitResult{T}"/>.</summary>
public interface IHostingClient
{
    HostingProvider Provider { get; }

    /// <summary>The OAuth device flow can be used (an application is registered for it); a personal token always can.</summary>
    bool SupportsDeviceFlow { get; }

    /// <summary>Account the token belongs to; <see cref="AuthenticationFailed"/> when the service refuses the token.</summary>
    Task<GitResult<HostUser>> GetUserAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same, for a service whose tokens are limited to an organization (Azure DevOps); other services ignore the
    /// organization.
    /// </summary>
    Task<GitResult<HostUser>> GetUserAsync(string token, string? organization, CancellationToken cancellationToken = default) =>
        GetUserAsync(token, cancellationToken);

    Task<GitResult<DeviceCode>> StartDeviceFlowAsync(CancellationToken cancellationToken = default);

    Task<GitResult<DevicePoll>> PollDeviceFlowAsync(DeviceCode code, CancellationToken cancellationToken = default);

    /// <summary>The repository a remote URL points to on this service; null for another host or a URL without owner and name.</summary>
    HostRepository? ParseRepository(RemoteUrl url);

    /// <summary>Open pull requests, most recently updated first (first page only).</summary>
    Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(string token, HostRepository repository, CancellationToken cancellationToken = default);

    /// <summary>Open issues (pull requests excluded), most recently updated first (first page only).</summary>
    Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(string token, HostRepository repository, CancellationToken cancellationToken = default);

    /// <summary>The service can filter the issue list and give it page after page (J6.10a).</summary>
    bool SupportsIssueFilters => false;

    /// <summary>
    /// A page (from 1) of the issues matching <paramref name="query"/>; <paramref name="login"/> is the signed-in account, for
    /// the "me" filters. The default gives only the first page of open issues.
    /// </summary>
    async Task<GitResult<IssuePage>> ListIssuePageAsync(
        string token, HostRepository repository, IssueQuery query, string? login, int page, CancellationToken cancellationToken = default)
    {
        if (!query.IsDefault || page != 1) return new Failure(Loc.Get("Hosting.NoIssueFilter"));
        var result = await ListIssuesAsync(token, repository, cancellationToken);
        switch (result)
        {
            case Success<IReadOnlyList<Issue>> { Value: var items }: return new Success<IssuePage>(new IssuePage(items, false));
            case AuthenticationFailed a: return a;
            case Failure f: return f;
            default: return Canceled.Instance;
        }
    }

    /// <summary>Default branch of the repository (target of a new pull request).</summary>
    Task<GitResult<string>> GetDefaultBranchAsync(string token, HostRepository repository, CancellationToken cancellationToken = default);

    Task<GitResult<PullRequest>> CreatePullRequestAsync(
        string token, HostRepository repository, NewPullRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The service takes reviewers, labels and linked issues when a pull request is created (J6.9l): the creation dialog then
    /// offers them.
    /// </summary>
    bool SupportsPullRequestCreateOptions => false;

    /// <summary>The accounts that can be asked to review a new pull request (J6.9l).</summary>
    Task<GitResult<PullRequestCreateOptions>> GetPullRequestCreateOptionsAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<PullRequestCreateOptions>>(NotWritable);

    /// <summary>
    /// Applies what the creation request carried but the service takes in separate calls (J6.9m: GitHub labels and reviewers);
    /// the pull request exists already, so a failure is reported without undoing it. The default has nothing to apply.
    /// </summary>
    Task<GitResult<Done>> ApplyPullRequestCreateOptionsAsync(
        string token, HostRepository repository, PullRequest created, NewPullRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(new Success<Done>(Done.Instance));

    /// <summary>
    /// Checks and review decision of a pull request (J6.6); the default knows only the review of the list.
    /// <paramref name="includeRequired"/> asks for the extra call that tells which checks are required (J6.12, the pull request screen).
    /// </summary>
    Task<GitResult<PullRequestStatus>> GetPullRequestStatusAsync(
        string token, HostRepository repository, PullRequest pullRequest, bool includeRequired = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<PullRequestStatus>>(new Success<PullRequestStatus>(new PullRequestStatus(CheckState.None, pullRequest.Review, [])));

    /// <summary>The service can give the description, conversation, commits and files of a pull request (J6.9a).</summary>
    bool SupportsPullRequestDetail => false;

    /// <summary>Description, conversation, commits and files of a pull request (J6.9a); the default is a failure saying it is not available.</summary>
    Task<GitResult<PullRequestDetail>> GetPullRequestDetailAsync(
        string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<PullRequestDetail>>(new Failure(Loc.Get("Hosting.NoPullDetail")));

    /// <summary>The service can give the description, comments and events of an issue (J6.10a).</summary>
    bool SupportsIssueDetail => false;

    /// <summary>Description, comments and events of an issue (J6.10a); the default is a failure saying it is not available.</summary>
    Task<GitResult<IssueDetail>> GetIssueDetailAsync(
        string token, HostRepository repository, Issue issue, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IssueDetail>>(new Failure(Loc.Get("Hosting.NoIssueDetail")));

    /// <summary>The service can give every field, link, comment and change of a work item (J6.14).</summary>
    bool SupportsWorkItemDetail => false;

    /// <summary>Fields, links, attachments, comments and changes of a work item (J6.14); the default is a failure saying it is not available.</summary>
    Task<GitResult<WorkItemDetail>> GetWorkItemDetailAsync(
        string token, HostRepository repository, Issue workItem, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<WorkItemDetail>>(new Failure(Loc.Get("Hosting.NoWorkItemDetail")));

    /// <summary>
    /// The service lets the issue screen comment, and edit, delete and react to comments (J6.10b). Editing, deleting and reacting go
    /// through the comment methods below, which only take a comment id (the discussion of an issue and of a pull request share them).
    /// </summary>
    bool SupportsIssueWrite => false;

    /// <summary>
    /// Downloads a picture shown in a description or comment (J6.10b). Pictures attached to a private repository only load with the
    /// account's token, which the image control cannot send; the client only sends it to the service's own hosts.
    /// </summary>
    Task<GitResult<byte[]>> DownloadImageAsync(string token, string url, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<byte[]>>(NotWritable);

    /// <summary>Adds a comment to an issue; returns the entry as the service stored it.</summary>
    Task<GitResult<TimelineEntry>> AddIssueCommentAsync(
        string token, HostRepository repository, Issue issue, string body, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<TimelineEntry>>(NotWritable);

    /// <summary>Creates an issue (J6.10c); returns it as the service stored it. Available where <see cref="SupportsIssueWrite"/> is.</summary>
    Task<GitResult<Issue>> CreateIssueAsync(
        string token, HostRepository repository, NewIssue issue, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Issue>>(NotWritable);

    /// <summary>Closes an issue with <paramref name="reason"/> (<paramref name="open"/> false), or reopens it (J6.10c).</summary>
    Task<GitResult<Done>> SetIssueOpenAsync(
        string token, HostRepository repository, Issue issue, bool open, IssueCloseReason reason, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>Replaces the title and the description of an issue (J6.10d).</summary>
    Task<GitResult<Done>> EditIssueTextAsync(
        string token, HostRepository repository, Issue issue, string title, string body, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>Labels, assignable accounts and milestones of the repository (J6.10d).</summary>
    Task<GitResult<IssueMetadataOptions>> GetIssueMetadataOptionsAsync(
        string token, HostRepository repository, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IssueMetadataOptions>>(NotWritable);

    /// <summary>Replaces the labels, assignees and milestone of an issue (J6.10d).</summary>
    Task<GitResult<Done>> SetIssueMetadataAsync(
        string token, HostRepository repository, Issue issue, IssueMetadata metadata, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>The service lets the pull request screen comment, close, reopen, change the draft state and merge (J6.9b).</summary>
    bool SupportsPullRequestWrite => false;

    /// <summary>The service lets the screen add a comment to the discussion of a pull request, without editing or deleting it (J6.9j).</summary>
    bool SupportsPullRequestComment => SupportsPullRequestWrite;

    /// <summary>The service lets the screen close, reopen and change the draft state of a pull request (J6.9j).</summary>
    bool SupportsPullRequestState => SupportsPullRequestWrite;

    /// <summary>Adds a comment to the discussion of a pull request; returns the entry as the service stored it.</summary>
    Task<GitResult<TimelineEntry>> AddPullRequestCommentAsync(
        string token, HostRepository repository, PullRequest pullRequest, string body, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<TimelineEntry>>(NotWritable);

    /// <summary>Replaces the text of a comment of the discussion.</summary>
    Task<GitResult<TimelineEntry>> EditPullRequestCommentAsync(
        string token, HostRepository repository, long commentId, string body, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<TimelineEntry>>(NotWritable);

    Task<GitResult<Done>> DeletePullRequestCommentAsync(
        string token, HostRepository repository, long commentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>Closes (<paramref name="open"/> false) or reopens a pull request.</summary>
    Task<GitResult<Done>> SetPullRequestOpenAsync(
        string token, HostRepository repository, PullRequest pullRequest, bool open, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>Turns a pull request into a draft, or marks it ready for review.</summary>
    Task<GitResult<Done>> SetPullRequestDraftAsync(
        string token, HostRepository repository, PullRequestDetail detail, bool draft, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>The service lets the screen merge a pull request (J6.9k).</summary>
    bool SupportsPullRequestMerge => SupportsPullRequestWrite;

    Task<GitResult<Done>> MergePullRequestAsync(
        string token, HostRepository repository, PullRequest pullRequest, MergeMethod method, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>Brings the base branch into the head branch of a pull request, on the service (J6.9c).</summary>
    Task<GitResult<Done>> UpdatePullRequestBranchAsync(
        string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>The header git sends to this service with <paramref name="accessToken"/> (J6.16); null when the service has none.</summary>
    GitHttpHeader? GitHttpHeaderOf(string accessToken) => null;

    /// <summary>The service lets the screen delete the source branch of a closed or merged pull request (J6.15).</summary>
    bool SupportsBranchDelete => false;

    /// <summary>Whether <paramref name="branch"/> still exists on the service.</summary>
    Task<GitResult<bool>> BranchExistsAsync(
        string token, HostRepository repository, string branch, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<bool>>(NotWritable);

    /// <summary>Deletes <paramref name="branch"/> on the service; a branch that is already gone counts as deleted.</summary>
    Task<GitResult<Done>> DeleteBranchAsync(
        string token, HostRepository repository, string branch, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>Enables the automatic merge of a pull request with <paramref name="method"/>, or disables it when null (J6.9d).</summary>
    Task<GitResult<Done>> SetPullRequestAutoMergeAsync(
        string token, HostRepository repository, PullRequestDetail detail, MergeMethod? method, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>
    /// Adds the reaction of the account <paramref name="login"/> to a comment of the discussion, or removes it when already there;
    /// returns the comment as the service now has it (J6.9d).
    /// </summary>
    Task<GitResult<TimelineEntry>> ToggleCommentReactionAsync(
        string token, HostRepository repository, long commentId, ReactionKind reaction, string login, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<TimelineEntry>>(NotWritable);

    /// <summary>The service can propose issues, pull requests and people to complete a <c>#</c>, <c>!</c> or <c>@</c> typed in a comment (J6.12).</summary>
    bool SupportsReferenceSearch => false;

    /// <summary>
    /// Proposals for what follows a <c>#</c>, <c>!</c> or <c>@</c> (searched on the service as the user types); an empty
    /// <paramref name="query"/> gives the most recent ones.
    /// </summary>
    Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchReferencesAsync(
        string token, HostRepository repository, ReferenceKind kind, string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IReadOnlyList<ReferenceSuggestion>>>(NotWritable);

    /// <summary>
    /// Names of the people a comment mentions by identifier (Azure DevOps stores <c>@&lt;id&gt;</c>), by identifier; the ones
    /// that cannot be resolved are left out. Services that mention by name have nothing to resolve.
    /// </summary>
    Task<GitResult<IReadOnlyDictionary<string, string>>> ResolveMentionsAsync(
        string token, HostRepository repository, IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IReadOnlyDictionary<string, string>>>(new Success<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

    /// <summary>The service lets the screen reply to threads on the diff, resolve them and send a review (J6.9e).</summary>
    bool SupportsPullRequestReview => false;

    /// <summary>Reactions can be given to the comments of review threads (<see cref="ToggleReviewCommentReactionAsync"/>).</summary>
    bool SupportsReviewCommentReactions => false;

    /// <summary>
    /// Adds the reaction of the account <paramref name="login"/> to a comment of a review thread, or removes it when already there;
    /// returns the comment as the service now has it.
    /// </summary>
    Task<GitResult<ReviewThreadComment>> ToggleReviewCommentReactionAsync(
        string token, HostRepository repository, long commentId, ReactionKind reaction, string login, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<ReviewThreadComment>>(NotWritable);

    /// <summary>Adds a reply to <paramref name="thread"/>.</summary>
    Task<GitResult<ReviewThreadComment>> ReplyToReviewThreadAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewThread thread, string body, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<ReviewThreadComment>>(NotWritable);

    /// <summary>Marks a thread resolved, or not resolved again.</summary>
    Task<GitResult<Done>> SetReviewThreadResolvedAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewThread thread, bool resolved, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>Sends a review in one go: its decision, its text and the comments on lines collected meanwhile.</summary>
    Task<GitResult<Done>> SubmitReviewAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewDecision decision, string body,
        IReadOnlyList<NewReviewComment> comments, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>The service lets the signed-in account vote on a pull request (J6.9h).</summary>
    bool SupportsPullRequestVote => false;

    /// <summary>Sets the vote of the signed-in account on a pull request; <see cref="ReviewVote.None"/> removes it.</summary>
    Task<GitResult<Done>> SetPullRequestVoteAsync(
        string token, HostRepository repository, PullRequest pullRequest, ReviewVote vote, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<Done>>(NotWritable);

    /// <summary>The whole old and new versions of a file of a pull request can be read (side-by-side diff).</summary>
    bool SupportsPullRequestSides => false;

    Task<GitResult<DiffSides>> GetPullRequestFileSidesAsync(
        string token, HostRepository repository, PullRequest pullRequest, PullRequestFile file, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<DiffSides>>(NotWritable);

    /// <summary>The service gives no patch for the files of a pull request: the diff is computed from their two versions (J6.9g).</summary>
    bool ComputesPullRequestDiffs => false;

    /// <summary>The service can list the repositories of the signed-in account, for the clone dialog (J6.13).</summary>
    bool SupportsRepositoryListing => false;

    /// <summary>
    /// The repositories the account can clone (owned, collaborated on, organizations), most recently pushed first when the
    /// service says so; at most a few pages (<see cref="HostedRepositoryList.IsTruncated"/>).
    /// </summary>
    Task<GitResult<HostedRepositoryList>> ListRepositoriesAsync(string token, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<HostedRepositoryList>>(new Failure(Loc.Get("Hosting.NoRepositoryListing")));

    /// <summary>
    /// Same, for an account whose token is limited to one organization (Azure DevOps): only that organization's repositories are listed,
    /// since such a token cannot list the organizations of the account.
    /// </summary>
    Task<GitResult<HostedRepositoryList>> ListRepositoriesAsync(string token, string? organization, CancellationToken cancellationToken = default) =>
        ListRepositoriesAsync(token, cancellationToken);

    private static Failure NotWritable => new(Loc.Get("Hosting.NotWritable"));

    /// <summary>
    /// Turns the stored token into one the API accepts: a token that expires (OAuth access token) is renewed with its
    /// refresh token. <see cref="ResolvedToken.UpdatedStored"/> is the new value to keep in the vault, null when unchanged.
    /// </summary>
    Task<GitResult<ResolvedToken>> ResolveTokenAsync(string stored, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<ResolvedToken>>(new Success<ResolvedToken>(new ResolvedToken(stored, null)));
}

/// <summary>Persistence of the account list (names and pictures only: tokens go to the <see cref="ITokenVault"/>).</summary>
public interface IHostAccountStore
{
    /// <summary>Reads the accounts synchronously (once, at launch). Empty when unreadable.</summary>
    IReadOnlyList<HostAccount> Load();

    Task SaveAsync(IReadOnlyList<HostAccount> accounts, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hosting accounts VIGO is signed in to (singleton): sign in with the OAuth device flow or a personal access token,
/// sign out, read a token back. Names and pictures are saved in a JSON file, tokens in the credential vault.
/// </summary>
public sealed class HostAccounts(
    IHostAccountStore store, ITokenVault vault, IEnumerable<IHostingClient> clients, TimeProvider? time = null, GitProfiles? profiles = null)
    : IGitHttpCredentials, IDisposable
{
    private readonly Dictionary<HostingProvider, IHostingClient> _clients = clients.ToDictionary(c => c.Provider);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _saving = new(1, 1);
    private IReadOnlyList<HostAccount> _accounts = store.Load();

    // The semaphore is not disposed: a save still running (fire and forget) would release a disposed object, and a
    // SemaphoreSlim that never exposed its wait handle holds nothing to free.
    public void Dispose()
    {
    }

    public IReadOnlyList<HostAccount> Accounts => _accounts;

    private EventHandler? _changed;

    /// <summary>
    /// Raised on the thread that changed the accounts, and when the profiles change (the account that applies to a
    /// repository, or to the Preferences screen, may then be another one).
    /// </summary>
    public event EventHandler? Changed
    {
        add
        {
            _changed += value;
            if (profiles is not null) profiles.Changed += value;
        }
        remove
        {
            _changed -= value;
            if (profiles is not null) profiles.Changed -= value;
        }
    }

    /// <summary>Sign-in is implemented for this provider.</summary>
    public bool IsSupported(HostingProvider provider) => _clients.ContainsKey(provider);

    public bool SupportsDeviceFlow(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsDeviceFlow;

    /// <summary>Profile that signing in and the Preferences screen act on (J6.7).</summary>
    public string ActiveProfileId => profiles?.ActiveId ?? SessionProfiles.Default;

    /// <summary>The account of a profile on a service; <paramref name="profileId"/> null: the active profile.</summary>
    public HostAccount? AccountOf(HostingProvider provider, string? profileId = null)
    {
        profileId ??= ActiveProfileId;
        return _accounts.FirstOrDefault(a => a.Provider == provider && a.ProfileId == profileId);
    }

    /// <summary>Token of an account, null when the vault has none (removed from outside VIGO).</summary>
    public Task<string?> GetTokenAsync(HostAccount account) => Task.Run(() => vault.Retrieve(account.Provider, account.Login, account.ProfileId));

    /// <summary>Signs in with a personal access token: checked with the service, then stored.</summary>
    public Task<GitResult<HostAccount>> ConnectWithTokenAsync(HostingProvider provider, string token, CancellationToken cancellationToken = default) =>
        ConnectWithTokenAsync(provider, token, null, cancellationToken);

    /// <summary>Same, checking the token against an organization (Azure DevOps tokens limited to one organization).</summary>
    public async Task<GitResult<HostAccount>> ConnectWithTokenAsync(HostingProvider provider, string token, string? organization, CancellationToken cancellationToken = default)
    {
        token = token.Trim();
        if (token.Length == 0) return new Failure(Loc.Get("Hosting.EnterToken"));
        if (!_clients.TryGetValue(provider, out var client)) return new Failure(Loc.Get("Hosting.Unsupported"));

        var user = await client.GetUserAsync(token, organization, cancellationToken);
        return await StoreAsync(provider, user, token, cancellationToken, string.IsNullOrWhiteSpace(organization) ? null : organization.Trim());
    }

    /// <summary>
    /// Signs in with the OAuth device flow: <paramref name="codeReady"/> receives the code to type on the provider's
    /// page, then the service is polled until the user authorized VIGO, refused, or the code expired.
    /// </summary>
    public async Task<GitResult<HostAccount>> SignInWithDeviceFlowAsync(
        HostingProvider provider, Action<DeviceCode> codeReady, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(provider, out var client)) return new Failure(Loc.Get("Hosting.Unsupported"));

        var started = await client.StartDeviceFlowAsync(cancellationToken);
        if (started is not Success<DeviceCode> { Value: var code }) return Forward<DeviceCode, HostAccount>(started);
        codeReady(code);

        var interval = code.Interval;
        string? token = null;
        while (token is null)
        {
            try
            {
                await Task.Delay(interval, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Canceled.Instance;
            }
            if (_time.GetUtcNow() >= code.ExpiresAt) return new Failure(Loc.Get("Hosting.CodeExpired"));

            var poll = await client.PollDeviceFlowAsync(code, cancellationToken);
            if (poll is not Success<DevicePoll> { Value: var result }) return Forward<DevicePoll, HostAccount>(poll);
            token = result.Token;
            if (result.SlowDown) interval += TimeSpan.FromSeconds(5);
        }

        var user = await client.GetUserAsync(token, cancellationToken);
        return await StoreAsync(provider, user, token, cancellationToken);
    }

    /// <summary>
    /// The hosted repository a remote points to (J6.2); null when no supported service hosts it. Needs no account: the
    /// panel then says to sign in.
    /// </summary>
    public HostRepository? ResolveRepository(RemoteUrl? url)
    {
        if (url is not { IsHttp: true } and not { Protocol: RemoteProtocol.Ssh }) return null;
        foreach (var client in _clients.Values)
        {
            if (client.ParseRepository(url) is { } repository) return repository;
        }
        return null;
    }

    public Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(HostRepository repository, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.ListPullRequestsAsync(token, repository, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(HostRepository repository, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.ListIssuesAsync(token, repository, cancellationToken), profileId, cancellationToken);

    /// <summary>The issue list can be filtered and paged for this provider (J6.10a).</summary>
    public bool SupportsIssueFilters(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsIssueFilters;

    public Task<GitResult<IssuePage>> ListIssuePageAsync(
        HostRepository repository, IssueQuery query, int page, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository,
            (client, token) => client.ListIssuePageAsync(token, repository, query, AccountOf(repository.Provider, profileId)?.Login, page, cancellationToken),
            profileId, cancellationToken);

    public Task<GitResult<PullRequestStatus>> GetPullRequestStatusAsync(
        HostRepository repository, PullRequest pullRequest, string? profileId = null, bool includeRequired = false, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.GetPullRequestStatusAsync(token, repository, pullRequest, includeRequired, cancellationToken), profileId, cancellationToken);

    /// <summary>The pull request screen exists for this provider (J6.9a).</summary>
    public bool SupportsPullRequestDetail(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsPullRequestDetail;

    public Task<GitResult<PullRequestDetail>> GetPullRequestDetailAsync(
        HostRepository repository, PullRequest pullRequest, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.GetPullRequestDetailAsync(token, repository, pullRequest, cancellationToken), profileId, cancellationToken);

    /// <summary>The issue screen exists for this provider (J6.10a).</summary>
    public bool SupportsIssueDetail(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsIssueDetail;

    public Task<GitResult<IssueDetail>> GetIssueDetailAsync(
        HostRepository repository, Issue issue, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.GetIssueDetailAsync(token, repository, issue, cancellationToken), profileId, cancellationToken);

    /// <summary>The work item screen exists for this provider (J6.14).</summary>
    public bool SupportsWorkItemDetail(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsWorkItemDetail;

    public Task<GitResult<WorkItemDetail>> GetWorkItemDetailAsync(
        HostRepository repository, Issue workItem, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.GetWorkItemDetailAsync(token, repository, workItem, cancellationToken), profileId, cancellationToken);

    /// <summary>
    /// Downloads a picture with the token of the active account of <paramref name="provider"/>; null when there is no account, the
    /// address is not one the service owns, or the download fails (the caller shows a link then).
    /// </summary>
    public async Task<byte[]?> DownloadImageAsync(HostingProvider provider, string url, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(new HostRepository(provider, string.Empty, string.Empty, string.Empty),
            (client, token) => client.DownloadImageAsync(token, url, cancellationToken), null, cancellationToken);
        return result is Success<byte[]> { Value: var bytes } ? bytes : null;
    }

    /// <summary>The issue screen can comment, and edit, delete and react to comments, for this provider (J6.10b).</summary>
    public bool SupportsIssueWrite(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsIssueWrite;

    public Task<GitResult<TimelineEntry>> AddIssueCommentAsync(
        HostRepository repository, Issue issue, string body, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.AddIssueCommentAsync(token, repository, issue, body, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Issue>> CreateIssueAsync(
        HostRepository repository, NewIssue issue, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.CreateIssueAsync(token, repository, issue, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> SetIssueOpenAsync(
        HostRepository repository, Issue issue, bool open, IssueCloseReason reason, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SetIssueOpenAsync(token, repository, issue, open, reason, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> EditIssueTextAsync(
        HostRepository repository, Issue issue, string title, string body, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.EditIssueTextAsync(token, repository, issue, title, body, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<IssueMetadataOptions>> GetIssueMetadataOptionsAsync(
        HostRepository repository, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.GetIssueMetadataOptionsAsync(token, repository, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> SetIssueMetadataAsync(
        HostRepository repository, Issue issue, IssueMetadata metadata, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SetIssueMetadataAsync(token, repository, issue, metadata, cancellationToken), profileId, cancellationToken);

    /// <summary>The pull request screen can write to this provider (J6.9b).</summary>
    public bool SupportsPullRequestWrite(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsPullRequestWrite;

    /// <summary>The pull request screen can add comments to this provider's pull requests (J6.9j).</summary>
    public bool SupportsPullRequestComment(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsPullRequestComment;

    /// <summary>The pull request screen can close, reopen and change the draft state of this provider's pull requests (J6.9j).</summary>
    public bool SupportsPullRequestState(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsPullRequestState;

    /// <summary>The pull request screen can merge this provider's pull requests (J6.9k).</summary>
    public bool SupportsPullRequestMerge(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsPullRequestMerge;

    /// <summary>The diff of a pull request file is computed here from its two versions: the service gives no patch (J6.9g).</summary>
    public bool ComputesPullRequestDiffs(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.ComputesPullRequestDiffs;

    public Task<GitResult<DiffSides>> GetPullRequestFileSidesAsync(
        HostRepository repository, PullRequest pullRequest, PullRequestFile file, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.GetPullRequestFileSidesAsync(token, repository, pullRequest, file, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<TimelineEntry>> AddPullRequestCommentAsync(
        HostRepository repository, PullRequest pullRequest, string body, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.AddPullRequestCommentAsync(token, repository, pullRequest, body, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<TimelineEntry>> EditPullRequestCommentAsync(
        HostRepository repository, long commentId, string body, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.EditPullRequestCommentAsync(token, repository, commentId, body, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> DeletePullRequestCommentAsync(
        HostRepository repository, long commentId, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.DeletePullRequestCommentAsync(token, repository, commentId, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> SetPullRequestOpenAsync(
        HostRepository repository, PullRequest pullRequest, bool open, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SetPullRequestOpenAsync(token, repository, pullRequest, open, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> SetPullRequestDraftAsync(
        HostRepository repository, PullRequestDetail detail, bool draft, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SetPullRequestDraftAsync(token, repository, detail, draft, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> MergePullRequestAsync(
        HostRepository repository, PullRequest pullRequest, MergeMethod method, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.MergePullRequestAsync(token, repository, pullRequest, method, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> UpdatePullRequestBranchAsync(
        HostRepository repository, PullRequest pullRequest, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.UpdatePullRequestBranchAsync(token, repository, pullRequest, cancellationToken), profileId, cancellationToken);

    /// <summary>The pull request screen can delete the source branch of this provider's pull requests (J6.15).</summary>
    public bool SupportsBranchDelete(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsBranchDelete;

    public Task<GitResult<bool>> BranchExistsAsync(
        HostRepository repository, string branch, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.BranchExistsAsync(token, repository, branch, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> DeleteBranchAsync(
        HostRepository repository, string branch, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.DeleteBranchAsync(token, repository, branch, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> SetPullRequestAutoMergeAsync(
        HostRepository repository, PullRequestDetail detail, MergeMethod? method, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SetPullRequestAutoMergeAsync(token, repository, detail, method, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<TimelineEntry>> ToggleCommentReactionAsync(
        HostRepository repository, long commentId, ReactionKind reaction, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.ToggleCommentReactionAsync(
            token, repository, commentId, reaction, AccountOf(repository.Provider, profileId)?.Login ?? string.Empty, cancellationToken), profileId, cancellationToken);

    /// <summary>Comments can offer completion after <c>#</c>, <c>!</c> and <c>@</c> for this provider (J6.12).</summary>
    public bool SupportsReferenceSearch(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsReferenceSearch;

    public Task<GitResult<IReadOnlyList<ReferenceSuggestion>>> SearchReferencesAsync(
        HostRepository repository, ReferenceKind kind, string query, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SearchReferencesAsync(token, repository, kind, query, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<IReadOnlyDictionary<string, string>>> ResolveMentionsAsync(
        HostRepository repository, IReadOnlyList<string> ids, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.ResolveMentionsAsync(token, repository, ids, cancellationToken), profileId, cancellationToken);

    /// <summary>The pull request screen can reply to threads, resolve them and send reviews for this provider (J6.9e).</summary>
    public bool SupportsPullRequestReview(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsPullRequestReview;

    public bool SupportsReviewCommentReactions(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsReviewCommentReactions;

    public Task<GitResult<ReviewThreadComment>> ToggleReviewCommentReactionAsync(
        HostRepository repository, long commentId, ReactionKind reaction, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.ToggleReviewCommentReactionAsync(
            token, repository, commentId, reaction, AccountOf(repository.Provider, profileId)?.Login ?? string.Empty, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<ReviewThreadComment>> ReplyToReviewThreadAsync(
        HostRepository repository, PullRequest pullRequest, ReviewThread thread, string body, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.ReplyToReviewThreadAsync(token, repository, pullRequest, thread, body, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> SetReviewThreadResolvedAsync(
        HostRepository repository, PullRequest pullRequest, ReviewThread thread, bool resolved, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SetReviewThreadResolvedAsync(token, repository, pullRequest, thread, resolved, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> SubmitReviewAsync(
        HostRepository repository, PullRequest pullRequest, ReviewDecision decision, string body, IReadOnlyList<NewReviewComment> comments,
        string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SubmitReviewAsync(token, repository, pullRequest, decision, body, comments, cancellationToken), profileId, cancellationToken);

    /// <summary>The pull request screen can record the account's vote for this provider (J6.9h).</summary>
    public bool SupportsPullRequestVote(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsPullRequestVote;

    public Task<GitResult<Done>> SetPullRequestVoteAsync(
        HostRepository repository, PullRequest pullRequest, ReviewVote vote, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.SetPullRequestVoteAsync(token, repository, pullRequest, vote, cancellationToken), profileId, cancellationToken);

    /// <summary>The clone dialog can list this provider's repositories (J6.13).</summary>
    public bool SupportsRepositoryListing(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsRepositoryListing;

    /// <summary>Repositories the <paramref name="account"/> can clone.</summary>
    public Task<GitResult<HostedRepositoryList>> ListRepositoriesAsync(HostAccount account, CancellationToken cancellationToken = default) =>
        CallAsync(new HostRepository(account.Provider, string.Empty, string.Empty, string.Empty),
            (client, token) => client.ListRepositoriesAsync(token, account.Organization, cancellationToken), account.ProfileId, cancellationToken);

    public Task<GitResult<string>> GetDefaultBranchAsync(HostRepository repository, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.GetDefaultBranchAsync(token, repository, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<PullRequest>> CreatePullRequestAsync(
        HostRepository repository, NewPullRequest request, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.CreatePullRequestAsync(token, repository, request, cancellationToken), profileId, cancellationToken);

    /// <summary>The creation dialog can offer reviewers, labels and linked issues for this provider (J6.9l).</summary>
    public bool SupportsPullRequestCreateOptions(HostingProvider provider) => _clients.TryGetValue(provider, out var c) && c.SupportsPullRequestCreateOptions;

    public Task<GitResult<PullRequestCreateOptions>> GetPullRequestCreateOptionsAsync(
        HostRepository repository, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.GetPullRequestCreateOptionsAsync(token, repository, cancellationToken), profileId, cancellationToken);

    public Task<GitResult<Done>> ApplyPullRequestCreateOptionsAsync(
        HostRepository repository, PullRequest created, NewPullRequest request, string? profileId = null, CancellationToken cancellationToken = default) =>
        CallAsync(repository, (client, token) => client.ApplyPullRequestCreateOptionsAsync(token, repository, created, request, cancellationToken), profileId, cancellationToken);

    // Runs a call of the service that hosts the repository with the token of the signed-in account.
    private async Task<GitResult<T>> CallAsync<T>(
        HostRepository repository, Func<IHostingClient, string, Task<GitResult<T>>> call, string? profileId, CancellationToken cancellationToken)
    {
        var token = await ReadyTokenAsync(repository.Provider, profileId, cancellationToken);
        return token is Success<ReadyToken> { Value: var ready } ? await call(ready.Client, ready.AccessToken) : Forward<ReadyToken, T>(token);
    }

    private sealed record ReadyToken(IHostingClient Client, string AccessToken);

    // The token of the profile's account, renewed (and saved again) when it is about to expire.
    private async Task<GitResult<ReadyToken>> ReadyTokenAsync(HostingProvider provider, string? profileId, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(provider, out var client)) return new Failure(Loc.Get("Hosting.Unsupported"));
        if (AccountOf(provider, profileId) is not { } account)
            return new AuthenticationFailed(null, Loc.Get("Hosting.NotSignedIn"));
        if (await GetTokenAsync(account) is not { Length: > 0 } token)
            return new AuthenticationFailed(null, Loc.Get("Hosting.TokenMissing"));
        var resolved = await client.ResolveTokenAsync(token, cancellationToken);
        if (resolved is not Success<ResolvedToken> { Value: var ready }) return Forward<ResolvedToken, ReadyToken>(resolved);
        if (ready.UpdatedStored is { } renewed)
        {
            // Never canceled: the service already invalidated the old token, losing the new one means "session revoked".
            try
            {
                await Task.Run(() => vault.Store(account.Provider, account.Login, renewed, account.ProfileId), CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new Failure(Loc.Format("Hosting.TokenNotSaved", ex.Message), Exception: ex);
            }
        }
        return new Success<ReadyToken>(new ReadyToken(client, ready.AccessToken));
    }

    /// <summary>Raised when a service refused the token that git commands of <c>RepositoryPath</c> sent (J6.16).</summary>
    public event EventHandler<GitTokenRejectedEventArgs>? GitTokenRejected;

    public async Task<IReadOnlyList<GitHttpHeader>> GetHeadersAsync(string? repositoryPath, CancellationToken cancellationToken)
    {
        var profileId = repositoryPath is null || profiles is null ? null : profiles.IdFor(repositoryPath);
        var headers = new List<GitHttpHeader>();
        foreach (var provider in _clients.Keys)
        {
            if (AccountOf(provider, profileId) is null) continue;
            if (await ReadyTokenAsync(provider, profileId, cancellationToken) is not Success<ReadyToken> { Value: var ready }) continue;
            // A line break or NUL in a token would add a header (or truncate it): such a token is left to the credential helpers.
            if (ready.Client.GitHttpHeaderOf(ready.AccessToken) is { } header && header.Value.IndexOfAny(['\r', '\n', '\0']) < 0) headers.Add(header);
        }
        return headers;
    }

    public void ReportRejected(string? repositoryPath, IReadOnlyList<GitHttpHeader> headers) =>
        GitTokenRejected?.Invoke(this, new GitTokenRejectedEventArgs(repositoryPath, [.. headers.Select(h => h.Provider)]));

    /// <summary>Forgets the account and removes its token from the vault (the token stays valid on the service).</summary>
    public async Task SignOutAsync(HostAccount account, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => vault.Remove(account.Provider, account.Login, account.ProfileId), cancellationToken);
        await UpdateAsync(list => [.. list.Where(a => !Same(a, account.Provider, account.Login, account.ProfileId))], cancellationToken);
    }

    private async Task<GitResult<HostAccount>> StoreAsync(
        HostingProvider provider, GitResult<HostUser> user, string token, CancellationToken cancellationToken, string? organization = null)
    {
        if (user is not Success<HostUser> { Value: var info }) return Forward<HostUser, HostAccount>(user);

        var account = new HostAccount(provider, info.Login, info.DisplayName, info.AvatarUrl, ActiveProfileId, organization);
        await Task.Run(() => vault.Store(provider, account.Login, token, account.ProfileId), cancellationToken);
        await UpdateAsync(list => [.. list.Where(a => !Same(a, provider, account.Login, account.ProfileId)), account], cancellationToken);
        return new Success<HostAccount>(account);
    }

    private async Task UpdateAsync(Func<IReadOnlyList<HostAccount>, IReadOnlyList<HostAccount>> change, CancellationToken cancellationToken)
    {
        _accounts = change(_accounts);
        _changed?.Invoke(this, EventArgs.Empty);
        // Not canceled: the list is already changed in memory, a short write must not leave the file behind.
        await _saving.WaitAsync(CancellationToken.None);
        try
        {
            await Task.Run(() => store.SaveAsync(_accounts, CancellationToken.None), CancellationToken.None);
        }
        finally
        {
            _saving.Release();
        }
    }

    private static bool Same(HostAccount account, HostingProvider provider, string login, string profileId) =>
        account.Provider == provider && account.ProfileId == profileId && string.Equals(account.Login, login, StringComparison.OrdinalIgnoreCase);

    // Failure-like cases of one result carried over to a result of another type.
    private static GitResult<TOut> Forward<TIn, TOut>(GitResult<TIn> result) => result switch
    {
        Success<TIn> => new Failure(Loc.Get("Hosting.UnexpectedService")),
        Conflict c => c,
        AuthenticationFailed a => a,
        Canceled c => c,
        Failure f => f,
    };
}

/// <summary>Stores the accounts in <c>%LOCALAPPDATA%\VIGO\accounts.json</c> (source-generated serializer: AOT).</summary>
public sealed partial class JsonHostAccountStore(string filePath, ILogger<JsonHostAccountStore> logger) : IHostAccountStore
{
    private const int CurrentVersion = 1;

    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "accounts.json");

    public IReadOnlyList<HostAccount> Load()
    {
        if (!File.Exists(filePath)) return [];
        var file = JsonFiles.Read(filePath, HostAccountsJsonContext.Default.HostAccountsFile,
            f => f.Accounts is not null && f.Version <= CurrentVersion,
            f => LogUnsupportedFile(logger, filePath, f?.Version ?? 0), e => LogLoadFailed(logger, e, filePath));
        return file is null ? [] : [.. file.Accounts!.Where(a => !string.IsNullOrWhiteSpace(a.Login))];
    }

    public async Task SaveAsync(IReadOnlyList<HostAccount> accounts, CancellationToken cancellationToken = default)
    {
        try
        {
            await JsonFiles.WriteAtomicAsync(filePath, new HostAccountsFile(CurrentVersion, [.. accounts]),
                HostAccountsJsonContext.Default.HostAccountsFile, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(logger, e, filePath);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Accounts file {Path} has an unsupported version {Version}; ignored")]
    private static partial void LogUnsupportedFile(ILogger logger, string path, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the accounts from {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the accounts to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}

internal sealed record HostAccountsFile(int Version, List<HostAccount>? Accounts);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HostAccountsFile))]
internal sealed partial class HostAccountsJsonContext : JsonSerializerContext;
