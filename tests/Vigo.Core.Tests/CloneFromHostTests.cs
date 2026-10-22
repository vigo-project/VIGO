using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>Clone dialog fed by a connected hosting account (J6.13, ADR-077).</summary>
public sealed class CloneFromHostTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HostAccount Octo = new(HostingProvider.GitHub, "octo", null, null);

    private readonly FakeGit _git = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryPreferencesStore _store = new();
    private readonly Preferences _preferences;
    private readonly List<CloneViewModel> _created = [];

    public CloneFromHostTests() =>
        _preferences = new Preferences(_store, new UserPreferences { LastCloneParent = @"D:\work" });

    public void Dispose()
    {
        foreach (var clone in _created) clone.Dispose();
        _preferences.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10, Ct);
        Assert.True(condition());
    }

    private sealed class MemoryVault : ITokenVault
    {
        public string? Retrieve(HostingProvider provider, string login, string profileId) => "token";

        public void Store(HostingProvider provider, string login, string token, string profileId)
        {
        }

        public void Remove(HostingProvider provider, string login, string profileId)
        {
        }
    }

    private sealed class MemoryStore(params HostAccount[] initial) : IHostAccountStore
    {
        public IReadOnlyList<HostAccount> Load() => initial;

        public Task SaveAsync(IReadOnlyList<HostAccount> accounts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ListingClient(HostingProvider provider, bool listing = true) : IHostingClient
    {
        public HostingProvider Provider => provider;

        public bool SupportsDeviceFlow => false;

        public bool SupportsRepositoryListing => listing;

        public Queue<GitResult<HostedRepositoryList>> Answers { get; } = [];

        public int Calls { get; private set; }

        public Task<GitResult<HostedRepositoryList>> ListRepositoriesAsync(string token, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Answers.Dequeue());
        }

        public Task<GitResult<HostUser>> GetUserAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitResult<DeviceCode>> StartDeviceFlowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitResult<DevicePoll>> PollDeviceFlowAsync(DeviceCode code, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public HostRepository? ParseRepository(RemoteUrl url) => null;

        public Task<GitResult<IReadOnlyList<PullRequest>>> ListPullRequestsAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitResult<IReadOnlyList<Issue>>> ListIssuesAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitResult<string>> GetDefaultBranchAsync(string token, HostRepository repository, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitResult<PullRequest>> CreatePullRequestAsync(string token, HostRepository repository, NewPullRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static HostedRepository Repo(string name, string? description = null, bool isPrivate = false, DateTimeOffset? at = null) =>
        new($"atlas/{name}", description, isPrivate, false, $"https://github.com/atlas/{name}.git", $"git@github.com:atlas/{name}.git", at, "atlas");

    private static GitResult<HostedRepositoryList> Ok(params HostedRepository[] items) => new Success<HostedRepositoryList>(new HostedRepositoryList(items, false));

    private CloneViewModel Create(ListingClient? client, params HostAccount[] accounts)
    {
        var host = new HostAccounts(new MemoryStore(accounts), new MemoryVault(), client is null ? [] : [client]);
        var clone = new CloneViewModel(_git, new NullPicker(), null, _preferences, host, _time);
        _created.Add(clone);
        return clone;
    }

    private sealed class NullPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    [Fact]
    public void Without_a_connected_account_only_the_url_source_exists_and_a_link_offers_to_connect()
    {
        var sut = Create(new ListingClient(HostingProvider.GitHub));

        Assert.Single(sut.Sources);
        Assert.True(sut.IsUrlSource);
        Assert.False(sut.HasHostAccounts);
        Assert.True(sut.ShowConnectHost);
    }

    [Fact]
    public void Accounts_of_the_active_profile_whose_service_lists_repositories_become_sources()
    {
        var other = new HostAccount(HostingProvider.GitHub, "elsewhere", null, null, "other-profile");
        var azure = new HostAccount(HostingProvider.AzureDevOps, "me@example.com", null, null);

        var sut = Create(new ListingClient(HostingProvider.GitHub), Octo, other, azure);

        Assert.Equal(["URL", "GitHub · octo"], sut.Sources.Select(s => s.Label));
        Assert.True(sut.HasHostAccounts);
        Assert.False(sut.ShowConnectHost);
    }

    [Fact]
    public async Task Selecting_a_source_lists_its_repositories_and_the_filter_matches_name_and_description()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(Ok(Repo("api", "Payments API"), Repo("web", "Storefront", isPrivate: true), Repo("docs")));
        var sut = Create(client, Octo);

        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;

        Assert.Equal(["atlas/api", "atlas/web", "atlas/docs"], sut.Repositories.Select(r => r.Name));
        Assert.True(sut.Repositories[1].IsPrivate);

        sut.RepositoryFilter = "STORE";
        _time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => sut.Repositories.Count == 1);
        Assert.Equal(["atlas/web"], sut.Repositories.Select(r => r.Name));

        sut.RepositoryFilter = "ATLAS/D";
        _time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => sut.Repositories.Count == 1 && sut.Repositories[0].Name == "atlas/docs");
    }

    [Fact]
    public async Task The_filter_waits_for_a_pause_in_typing()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(Ok(Repo("api"), Repo("web")));
        var sut = Create(client, Octo);
        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;

        sut.RepositoryFilter = "w";
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, sut.Repositories.Count);

        sut.RepositoryFilter = "we";
        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal(2, sut.Repositories.Count);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => sut.Repositories.Count == 1);
        Assert.Equal(["atlas/web"], sut.Repositories.Select(r => r.Name));
    }

    [Fact]
    public async Task Choosing_a_repository_fills_the_url_and_the_destination_and_ssh_switches_the_address()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(Ok(Repo("api")));
        var sut = Create(client, Octo);
        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;
        Assert.False(sut.CanClone);

        sut.SelectedRepository = sut.Repositories[0];

        Assert.Equal("https://github.com/atlas/api.git", sut.Url);
        Assert.Equal(@"D:\work\api", sut.Destination);
        Assert.True(sut.CanClone);

        sut.UseSsh = true;
        Assert.Equal("git@github.com:atlas/api.git", sut.Url);
        Assert.Equal(@"D:\work\api", sut.Destination);
    }

    [Fact]
    public async Task A_host_source_needs_a_chosen_repository_even_when_an_address_was_typed()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(Ok(Repo("api")));
        var sut = Create(client, Octo);
        sut.Url = "https://example.com/x/y.git";
        Assert.True(sut.CanClone);

        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;

        Assert.False(sut.CanClone);
    }

    [Fact]
    public async Task The_list_is_kept_for_the_dialog_and_refreshed_on_demand()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(Ok(Repo("api")));
        client.Answers.Enqueue(Ok(Repo("api"), Repo("web")));
        var sut = Create(client, Octo);

        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;
        sut.SelectedSource = sut.Sources[0];
        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;
        Assert.Equal(1, client.Calls);
        Assert.Single(sut.Repositories);

        await sut.RefreshRepositoriesCommand.ExecuteAsync(null);

        Assert.Equal(2, client.Calls);
        Assert.Equal(2, sut.Repositories.Count);
    }

    [Fact]
    public async Task A_failed_listing_shows_the_reason_and_an_empty_list_says_so()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(new AuthenticationFailed("github", "Jeton révoqué."));
        client.Answers.Enqueue(Ok());
        var sut = Create(client, Octo);

        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;
        Assert.Equal("Jeton révoqué.", sut.RepositoriesError);
        Assert.True(sut.HasRepositoriesError);
        Assert.False(sut.IsRepositoryListEmpty);
        Assert.False(sut.IsLoadingRepositories);

        await sut.RefreshRepositoriesCommand.ExecuteAsync(null);
        Assert.False(sut.HasRepositoriesError);
        Assert.True(sut.IsRepositoryListEmpty);
    }

    [Fact]
    public async Task A_truncated_list_is_flagged()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(new Success<HostedRepositoryList>(new HostedRepositoryList([Repo("api")], true)));
        var sut = Create(client, Octo);

        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;

        Assert.True(sut.IsRepositoryListTruncated);
    }

    [Fact]
    public async Task Relative_date_of_the_last_push_is_shown()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(Ok(Repo("api", at: _time.GetUtcNow().AddDays(-3)), Repo("web")));
        var sut = Create(client, Octo);

        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;

        Assert.NotEqual(string.Empty, sut.Repositories[0].UpdatedText);
        Assert.Equal(string.Empty, sut.Repositories[1].UpdatedText);
    }

    [Fact]
    public async Task A_clone_from_a_host_remembers_the_ssh_choice()
    {
        var client = new ListingClient(HostingProvider.GitHub);
        client.Answers.Enqueue(Ok(Repo("api")));
        var sut = Create(client, Octo);
        sut.SelectedSource = sut.Sources[1];
        await sut.WhenRepositoriesLoaded;
        sut.UseSsh = true;
        sut.SelectedRepository = sut.Repositories[0];

        var cloned = await sut.RunAsync(new ClickAndClose(), Ct);

        Assert.NotNull(cloned);
        Assert.Equal("git@github.com:atlas/api.git", Assert.Single(_git.Clones).Url);
        Assert.True(_store.Saved?.CloneUseSsh);
        Assert.True(new CloneViewModel(_git, new NullPicker(), null, _preferences).UseSsh);
    }

    [Fact]
    public void Connecting_a_host_closes_the_dialog_and_asks_for_the_integrations()
    {
        var sut = Create(new ListingClient(HostingProvider.GitHub));
        var closed = false;
        sut.CloseRequested += (_, _) => closed = true;

        sut.ConnectHostCommand.Execute(null);

        Assert.True(closed);
        Assert.True(sut.OpenIntegrationsRequested);
    }

    private sealed class ClickAndClose : ICloneDialog
    {
        public async Task<bool> ShowAsync(CloneViewModel clone, CancellationToken cancellationToken = default)
        {
            var closed = false;
            clone.CloseRequested += (_, _) => closed = true;
            await clone.CloneCommand.ExecuteAsync(null);
            return closed;
        }
    }
}
