using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

/// <summary>Hosting accounts (J6.1, ADR-044): token and device-flow sign-in, sign-out, persistence split.</summary>
public sealed class HostAccountsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class MemoryVault : ITokenVault
    {
        public Dictionary<(HostingProvider, string), string> Tokens { get; } = [];

        // Keyed like before profiles for the "default" one, so that these tests read the same.
        private static (HostingProvider, string) KeyOf(HostingProvider provider, string login, string profileId) =>
            (provider, profileId == SessionProfiles.Default ? login : $"{profileId}/{login}");

        public string? Retrieve(HostingProvider provider, string login, string profileId) => Tokens.GetValueOrDefault(KeyOf(provider, login, profileId));

        public bool FailStore { get; set; }

        public void Store(HostingProvider provider, string login, string token, string profileId)
        {
            if (FailStore) throw new InvalidOperationException("vault locked");
            Tokens[KeyOf(provider, login, profileId)] = token;
        }

        public void Remove(HostingProvider provider, string login, string profileId) => Tokens.Remove(KeyOf(provider, login, profileId));
    }

    private sealed class MemoryStore(params HostAccount[] initial) : IHostAccountStore
    {
        public IReadOnlyList<HostAccount> Saved { get; private set; } = initial;

        public IReadOnlyList<HostAccount> Load() => Saved;

        public Task SaveAsync(IReadOnlyList<HostAccount> accounts, CancellationToken cancellationToken = default)
        {
            Saved = accounts;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient : IHostingClient
    {
        public HostingProvider Provider => HostingProvider.GitHub;

        public bool SupportsDeviceFlow => true;

        /// <summary>Token → account; any other token is refused.</summary>
        public Dictionary<string, HostUser> Users { get; } = [];

        /// <summary>Answers of the successive polls.</summary>
        public Queue<GitResult<DevicePoll>> Polls { get; } = [];

        public int PollCount { get; private set; }

        public Task<GitResult<HostUser>> GetUserAsync(string token, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<HostUser>>(Users.TryGetValue(token, out var user)
                ? new Success<HostUser>(user)
                : new AuthenticationFailed("github", "refusé"));

        public Task<GitResult<DeviceCode>> StartDeviceFlowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<DeviceCode>>(new Success<DeviceCode>(
                new DeviceCode("ABCD-1234", "https://github.com/login/device", "secret", DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromMilliseconds(1))));

        public Task<GitResult<DevicePoll>> PollDeviceFlowAsync(DeviceCode code, CancellationToken cancellationToken = default)
        {
            PollCount++;
            return Task.FromResult(Polls.Dequeue());
        }

        /// <summary>When set, stored tokens are renewed: calls use "renewed-access" and the vault gets this value.</summary>
        public string? RenewedStored { get; set; }

        public Task<GitResult<ResolvedToken>> ResolveTokenAsync(string stored, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<ResolvedToken>>(new Success<ResolvedToken>(
                RenewedStored is null ? new ResolvedToken(stored, null) : new ResolvedToken("renewed-access", RenewedStored)));

        public string? LastToken { get; private set; }

        public GitHttpHeader? GitHttpHeaderOf(string accessToken) =>
            new(HostingProvider.GitHub, ["https://github.com/"], "Authorization: Basic " + accessToken);

        public List<PullRequest> PullRequests { get; } = [];

        public HostRepository? ParseRepository(RemoteUrl url) =>
            url.Host == "github.com" && url.DisplayPath.Split('/') is [var owner, var name]
                ? new HostRepository(HostingProvider.GitHub, "github.com", owner, name)
                : null;

        public Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(string token, HostRepository repository, CancellationToken cancellationToken = default)
        {
            LastToken = token;
            return Task.FromResult<GitResult<IReadOnlyList<PullRequest>>>(new Success<IReadOnlyList<PullRequest>>(PullRequests));
        }

        public Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<IReadOnlyList<Issue>>>(new Success<IReadOnlyList<Issue>>([]));

        public Task<GitResult<string>> GetDefaultBranchAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<string>>(new Success<string>("main"));

        public Task<GitResult<PullRequest>> CreatePullRequestAsync(string token, HostRepository repository, NewPullRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static readonly HostUser Octocat = new("octocat", "The Octocat", "https://avatars.example/1");

    private static (HostAccounts Accounts, MemoryStore Store, MemoryVault Vault, FakeClient Client) Create(params HostAccount[] existing)
    {
        var store = new MemoryStore(existing);
        var vault = new MemoryVault();
        var client = new FakeClient();
        return (new HostAccounts(store, vault, [client]), store, vault, client);
    }

    [Fact]
    public async Task Each_profile_has_its_own_account_and_token()
    {
        var store = new MemoryStore();
        var vault = new MemoryVault();
        var client = new FakeClient();
        using var profiles = new GitProfiles(new MemoryProfileStore());
        var work = profiles.Add("Travail", "Camille", "c@atlas.dev");
        var perso = profiles.Add("Perso", "Camille", "c@proton.me");
        var accounts = new HostAccounts(store, vault, [client], profiles: profiles);
        client.Users["work-token"] = new HostUser("camille-atlas", null, null);
        client.Users["perso-token"] = new HostUser("camille-perso", null, null);
        var changed = 0;
        accounts.Changed += (_, _) => changed++;

        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "work-token", Ct);
        profiles.SetActive(perso.Id);
        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "perso-token", Ct);

        Assert.Equal("camille-perso", accounts.AccountOf(HostingProvider.GitHub)?.Login);
        Assert.Equal("camille-atlas", accounts.AccountOf(HostingProvider.GitHub, work.Id)?.Login);
        Assert.Equal(work.Id, accounts.AccountOf(HostingProvider.GitHub, work.Id)?.ProfileId);
        Assert.Equal("work-token", vault.Tokens[(HostingProvider.GitHub, "camille-atlas")]);
        Assert.Equal("perso-token", vault.Tokens[(HostingProvider.GitHub, $"{perso.Id}/camille-perso")]);
        // Switching the active profile is a change of accounts for whoever shows them.
        var before = changed;
        profiles.SetActive(work.Id);
        Assert.Equal(before + 1, changed);
        Assert.Equal("camille-atlas", accounts.AccountOf(HostingProvider.GitHub)?.Login);

        // A call for a repository uses the account of the profile that applies to it, not the active one.
        var repository = new HostRepository(HostingProvider.GitHub, "github.com", "o", "r");
        await accounts.ListPullRequestsAsync(repository, perso.Id, Ct);
        Assert.Equal("perso-token", client.LastToken);

        await accounts.SignOutAsync(accounts.AccountOf(HostingProvider.GitHub, perso.Id)!, Ct);
        Assert.Null(accounts.AccountOf(HostingProvider.GitHub, perso.Id));
        Assert.NotNull(accounts.AccountOf(HostingProvider.GitHub, work.Id));
    }

    [Fact]
    public void Accounts_saved_before_profiles_belong_to_the_default_profile()
    {
        var (accounts, _, _, _) = Create(new HostAccount(HostingProvider.GitHub, "octocat", null, null));

        Assert.Equal(SessionProfiles.Default, accounts.AccountOf(HostingProvider.GitHub)?.ProfileId);
    }

    private sealed class MemoryProfileStore : IGitProfileStore
    {
        public GitProfileSet Load() => GitProfileSet.Empty;

        public Task SaveAsync(GitProfileSet profiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_valid_token_is_kept_in_the_vault_and_the_account_in_the_store()
    {
        var (accounts, store, vault, client) = Create();
        client.Users["ghp_ok"] = Octocat;
        var changed = 0;
        accounts.Changed += (_, _) => changed++;

        var result = await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "  ghp_ok \n", Ct);

        var account = Assert.IsType<Success<HostAccount>>(result.Value).Value;
        Assert.Equal(new HostAccount(HostingProvider.GitHub, "octocat", "The Octocat", "https://avatars.example/1"), account);
        Assert.Equal("ghp_ok", vault.Tokens[(HostingProvider.GitHub, "octocat")]);
        Assert.Equal([account], store.Saved);
        Assert.Equal(account, accounts.AccountOf(HostingProvider.GitHub));
        Assert.Equal("ghp_ok", await accounts.GetTokenAsync(account));
        Assert.Equal(1, changed);
        // Nothing but names in the file: the token never reaches the store.
        Assert.DoesNotContain("ghp_ok", store.Saved.Single().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_or_empty_token_stores_nothing()
    {
        var (accounts, store, vault, _) = Create();

        Assert.IsType<AuthenticationFailed>((await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "bad", Ct)).Value);
        Assert.IsType<Failure>((await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "   ", Ct)).Value);
        Assert.IsType<Failure>((await accounts.ConnectWithTokenAsync(HostingProvider.GitLab, "t", Ct)).Value);

        Assert.Empty(vault.Tokens);
        Assert.Empty(store.Saved);
        Assert.Empty(accounts.Accounts);
    }

    [Fact]
    public async Task Signing_in_again_with_the_same_account_replaces_it()
    {
        var (accounts, store, vault, client) = Create(new HostAccount(HostingProvider.GitHub, "OCTOCAT", null, null));
        client.Users["new"] = Octocat;

        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "new", Ct);

        Assert.Equal("The Octocat", Assert.Single(store.Saved).DisplayName);
        Assert.Equal("new", vault.Tokens[(HostingProvider.GitHub, "octocat")]);
    }

    [Fact]
    public async Task Signing_out_removes_the_token_and_the_account()
    {
        var (accounts, store, vault, client) = Create();
        client.Users["t"] = Octocat;
        var account = Assert.IsType<Success<HostAccount>>((await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "t", Ct)).Value).Value;

        await accounts.SignOutAsync(account, Ct);

        Assert.Empty(vault.Tokens);
        Assert.Empty(store.Saved);
        Assert.Null(accounts.AccountOf(HostingProvider.GitHub));
        Assert.Null(await accounts.GetTokenAsync(account));
    }

    [Fact]
    public async Task The_device_flow_polls_until_the_user_authorizes_and_shows_the_code()
    {
        var (accounts, store, vault, client) = Create();
        client.Users["gho_tok"] = Octocat;
        client.Polls.Enqueue(new Success<DevicePoll>(new DevicePoll(null)));
        client.Polls.Enqueue(new Success<DevicePoll>(new DevicePoll(null, SlowDown: true)));
        client.Polls.Enqueue(new Success<DevicePoll>(new DevicePoll("gho_tok")));
        DeviceCode? shown = null;

        var result = await accounts.SignInWithDeviceFlowAsync(HostingProvider.GitHub, code => shown = code, Ct);

        Assert.Equal("octocat", Assert.IsType<Success<HostAccount>>(result.Value).Value.Login);
        Assert.Equal("ABCD-1234", shown?.UserCode);
        Assert.Equal(3, client.PollCount);
        Assert.Equal("gho_tok", vault.Tokens[(HostingProvider.GitHub, "octocat")]);
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task The_device_flow_stops_on_a_refusal_and_on_cancellation()
    {
        var (accounts, store, _, client) = Create();
        client.Polls.Enqueue(new Failure("Connexion refusée sur GitHub."));

        var refused = await accounts.SignInWithDeviceFlowAsync(HostingProvider.GitHub, _ => { }, Ct);
        Assert.Equal("Connexion refusée sur GitHub.", Assert.IsType<Failure>(refused.Value).Message);

        using var cancel = new CancellationTokenSource();
        var canceled = await accounts.SignInWithDeviceFlowAsync(HostingProvider.GitHub, _ => cancel.Cancel(), cancel.Token);
        Assert.IsType<Canceled>(canceled.Value);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void Accounts_saved_earlier_are_loaded_and_support_is_reported()
    {
        var saved = new HostAccount(HostingProvider.GitHub, "octocat", null, null);
        var (accounts, _, _, _) = Create(saved);

        Assert.Equal([saved], accounts.Accounts);
        Assert.True(accounts.IsSupported(HostingProvider.GitHub));
        Assert.False(accounts.IsSupported(HostingProvider.Bitbucket));
        Assert.True(accounts.SupportsDeviceFlow(HostingProvider.GitHub));
        Assert.Equal("octocat", saved.Title);
    }

    [Theory]
    [InlineData("https://github.com/atlas/atlas-api.git", "atlas/atlas-api")]
    [InlineData("git@github.com:atlas/atlas-api.git", "atlas/atlas-api")]
    [InlineData("https://gitlab.example.com/atlas/atlas-api.git", null)]
    [InlineData("C:\repos\atlas-api", null)]
    public void The_hosted_repository_is_found_from_the_remote_url(string url, string? expected)
    {
        var (accounts, _, _, _) = Create();

        Assert.Equal(expected, accounts.ResolveRepository(RemoteUrl.TryParse(url))?.FullName);
    }

    [Fact]
    public async Task Hosting_calls_use_the_token_of_the_signed_in_account()
    {
        var repository = new HostRepository(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");
        var (accounts, _, _, client) = Create();
        client.Users["ghp_tok"] = Octocat;
        client.PullRequests.Add(new PullRequest(7, "Fix cache", "octocat", "fix/cache", null, "main", false, DateTimeOffset.UnixEpoch, "https://github.com/atlas/atlas-api/pull/7"));

        var signedOut = await accounts.ListPullRequestsAsync(repository, cancellationToken: Ct);
        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "ghp_tok", Ct);
        var signedIn = await accounts.ListPullRequestsAsync(repository, cancellationToken: Ct);

        Assert.IsType<AuthenticationFailed>(signedOut.Value);
        Assert.Equal(7, Assert.Single(Assert.IsType<Success<IReadOnlyList<PullRequest>>>(signedIn.Value).Value).Number);
        Assert.Equal("ghp_tok", client.LastToken);
    }

    [Fact]
    public async Task A_renewed_token_is_used_and_saved_in_the_vault()
    {
        var repository = new HostRepository(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");
        var (accounts, _, vault, client) = Create();
        client.Users["old-stored"] = Octocat;
        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "old-stored", Ct);
        client.RenewedStored = "new-stored";

        await accounts.ListPullRequestsAsync(repository, cancellationToken: Ct);

        Assert.Equal("renewed-access", client.LastToken);
        Assert.Equal("new-stored", vault.Tokens[(HostingProvider.GitHub, "octocat")]);
    }

    [Fact]
    public async Task Git_gets_the_header_of_the_account_of_the_profile_with_its_renewed_token()
    {
        var client = new FakeClient();
        var vault = new MemoryVault();
        using var profiles = new GitProfiles(new MemoryProfileStore());
        var work = profiles.Add("Travail", "Camille", "c@atlas.dev");
        var perso = profiles.Add("Perso", "Camille", "c@proton.me");
        var accounts = new HostAccounts(new MemoryStore(), vault, [client], profiles: profiles);
        client.Users["work-token"] = new HostUser("camille-atlas", null, null);
        client.Users["perso-token"] = new HostUser("camille-perso", null, null);
        profiles.SetActive(work.Id);
        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "work-token", Ct);
        profiles.SetActive(perso.Id);
        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "perso-token", Ct);

        // No folder: the active profile.
        var header = Assert.Single(await accounts.GetHeadersAsync(null, Ct));
        Assert.Equal("Authorization: Basic perso-token", header.Value);
        Assert.Equal(["https://github.com/"], header.Urls);

        client.RenewedStored = "new-stored";
        Assert.Equal("Authorization: Basic renewed-access", Assert.Single(await accounts.GetHeadersAsync(null, Ct)).Value);
        Assert.Equal("new-stored", vault.Tokens[(HostingProvider.GitHub, $"{perso.Id}/camille-perso")]);
    }

    [Fact]
    public async Task Git_gets_no_header_without_an_account_or_with_a_token_that_would_add_one()
    {
        var (accounts, _, vault, client) = Create();
        Assert.Empty(await accounts.GetHeadersAsync(null, Ct));

        client.Users["bad"] = Octocat;
        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "bad", Ct);
        vault.Tokens[(HostingProvider.GitHub, "octocat")] = "tok\r\nX-Evil: 1";

        Assert.Empty(await accounts.GetHeadersAsync(null, Ct));
    }

    [Fact]
    public async Task A_rejection_is_raised_with_the_providers_that_were_sent()
    {
        var (accounts, _, _, client) = Create();
        client.Users["tok"] = Octocat;
        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "tok", Ct);
        GitTokenRejectedEventArgs? raised = null;
        accounts.GitTokenRejected += (_, e) => raised = e;

        accounts.ReportRejected("D:/repo", await accounts.GetHeadersAsync("D:/repo", Ct));

        Assert.Equal("D:/repo", raised?.RepositoryPath);
        Assert.Equal([HostingProvider.GitHub], raised?.Providers);
    }

    [Fact]
    public async Task A_renewed_token_is_saved_even_if_the_caller_cancels_and_a_vault_error_is_a_failure()
    {
        var repository = new HostRepository(HostingProvider.GitHub, "github.com", "atlas", "atlas-api");
        var (accounts, _, vault, client) = Create();
        client.Users["old-stored"] = Octocat;
        await accounts.ConnectWithTokenAsync(HostingProvider.GitHub, "old-stored", Ct);
        client.RenewedStored = "new-stored";
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await accounts.ListPullRequestsAsync(repository, cancellationToken: canceled.Token);
        Assert.Equal("new-stored", vault.Tokens[(HostingProvider.GitHub, "octocat")]);

        vault.FailStore = true;
        var result = await accounts.ListPullRequestsAsync(repository, cancellationToken: Ct);
        Assert.IsType<Failure>(result.Value);
    }
}
