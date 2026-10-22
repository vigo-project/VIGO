using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>What the pull request and issue sections of the left panel show.</summary>
public enum HostingState
{
    /// <summary>No remote points to a supported hosting service: the sections are hidden.</summary>
    Unavailable,

    /// <summary>The remote is hosted but VIGO has no account for the service.</summary>
    SignedOut,

    Loading,
    Ready,
    Failed,
}

/// <summary>State of the hosting sections for <see cref="RefRowBuilder.BuildRows"/> (immutable, tested without a view model).</summary>
/// <param name="RepositoryName">"owner/name" of the hosted repository.</param>
/// <param name="RemoteName">Remote the repository was found with.</param>
/// <param name="Error">Why the lists could not be read (state <see cref="HostingState.Failed"/>).</param>
/// <param name="Statuses">Checks and review of the pull requests read so far, by number (J6.6).</param>
/// <param name="IssueFilter">Filters the issue list was read with; null or default: open issues, nothing else (J6.10a).</param>
/// <param name="IssuesHaveMore">The service has more issues than the ones in <paramref name="Issues"/> (J6.10a).</param>
public sealed record HostingSnapshot(
    HostingState State, HostingProvider Provider, string? RepositoryName, string? RemoteName,
    IReadOnlyList<PullRequest> PullRequests, IReadOnlyList<Issue> Issues, string Error,
    IReadOnlyDictionary<int, PullRequestStatus>? Statuses = null,
    IssueQuery? IssueFilter = null, bool IssuesHaveMore = false, bool IsLoadingMoreIssues = false);

/// <summary>
/// Pull requests and issues of the hosting service behind the repository's remote (J6.2, ADR-045), and the creation of a
/// pull request from a branch. The remote is <c>origin</c> (or the only one, or the first hosted one); the account is the
/// one signed in for that service. Lists are refreshed when the remotes or the accounts change, on demand, and when the
/// refs change after a while (fetch, pull, push).
/// </summary>
/// <remarks>User-facing texts: <c>Hosting.*</c> resources (ADR-075).</remarks>
public sealed partial class HostingPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>Lists older than this are read again when the refs change.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>Only the most recently updated pull requests get their checks read (about three calls each).</summary>
    internal const int StatusLimit = 20;

    private const int StatusParallelism = 4;

    private readonly HostAccounts? _accounts;
    private readonly RefActionsViewModel _refActions;
    private readonly GraphViewModel _graph;
    private readonly IInputDialogService? _inputs;
    private readonly IUserNotifier? _notifier;
    private readonly IUrlLauncher? _launcher;
    private readonly TimeProvider _time;
    private readonly CancellationToken _lifetime;
    private readonly Func<string?>? _profileId;
    private CancellationTokenSource? _refresh;
    private DateTimeOffset _lastRefresh;
    private Dictionary<int, PullRequestStatus> _statuses = [];
    private IssueQuery _issueQuery = IssueQuery.Default;
    private bool _issuesHaveMore;
    private bool _loadingMoreIssues;
    private bool _filteringIssues;
    private int _issuePages = 1;
    private CancellationTokenSource? _issueLoad;

    public HostingPanelViewModel(
        HostAccounts? accounts, RefActionsViewModel refActions, GraphViewModel graph, IInputDialogService? inputs,
        IUserNotifier? notifier, IUrlLauncher? launcher, TimeProvider time, CancellationToken lifetime, Func<string?>? profileId = null)
    {
        _accounts = accounts;
        _refActions = refActions;
        _graph = graph;
        _inputs = inputs;
        _notifier = notifier;
        _launcher = launcher;
        _time = time;
        _lifetime = lifetime;
        _profileId = profileId;
        _refActions.RemotesChanged += OnRemotesChanged;
        _graph.RefsChanged += OnRefsChanged;
        if (_accounts is not null) _accounts.Changed += OnAccountsChanged;
    }

    /// <summary>Raised on the thread that changed the lists or the state (rows of the left panel to rebuild).</summary>
    public event EventHandler? Changed;

    internal RefActionsViewModel RefActions => _refActions;

    /// <summary>Profile whose account the calls use: the one that applies to the repository (J6.7).</summary>
    private string? ProfileId => _profileId?.Invoke();

    public HostingState State { get; private set; } = HostingState.Unavailable;

    /// <summary>Hosted repository behind the remote; null when <see cref="State"/> is <see cref="HostingState.Unavailable"/>.</summary>
    public HostRepository? Repository { get; private set; }

    /// <summary>Remote the repository was found with ("origin").</summary>
    public string? RemoteName { get; private set; }

    public IReadOnlyList<PullRequest> PullRequests { get; private set; } = [];

    public IReadOnlyList<Issue> Issues { get; private set; } = [];

    /// <summary>Checks and review decision of the pull requests whose status was read (J6.6), by number.</summary>
    public IReadOnlyDictionary<int, PullRequestStatus> Statuses => _statuses;

    /// <summary>Why the lists could not be read (state <see cref="HostingState.Failed"/>).</summary>
    public string ErrorMessage { get; private set; } = string.Empty;

    /// <summary>Pull requests can be listed and created: a hosted remote and a signed-in account.</summary>
    public bool IsConnected => Repository is not null && State is not (HostingState.Unavailable or HostingState.SignedOut);

    /// <summary>The pull request screen can show this repository's pull requests: connected, and the service gives their detail (J6.9a).</summary>
    public bool CanShowPullRequest => IsConnected && _accounts is not null && Repository is { } hosted && _accounts.SupportsPullRequestDetail(hosted.Provider);

    /// <summary>The issue screen is available: an account is connected and the service gives the detail of an issue (J6.10a).</summary>
    public bool CanShowIssue => IsConnected && _accounts is not null && Repository is { } hosted
        && (_accounts.SupportsIssueDetail(hosted.Provider) || _accounts.SupportsWorkItemDetail(hosted.Provider));

    /// <summary>What the left panel draws; null when no remote is hosted.</summary>
    public HostingSnapshot? Snapshot => State == HostingState.Unavailable || Repository is not { } repository
        ? null
        : new HostingSnapshot(State, repository.Provider, repository.FullName, RemoteName, PullRequests, Issues, ErrorMessage, _statuses,
            _issueQuery, _issuesHaveMore, _loadingMoreIssues);

    /// <summary>Filters of the issue list (J6.10a).</summary>
    public IssueQuery IssueFilter => _issueQuery;

    /// <summary>The issue list can be filtered and paged: connected, and the service supports it.</summary>
    public bool CanFilterIssues => IsConnected && _accounts is not null && Repository is { } hosted && _accounts.SupportsIssueFilters(hosted.Provider);

    public void Dispose()
    {
        _refActions.RemotesChanged -= OnRemotesChanged;
        _graph.RefsChanged -= OnRefsChanged;
        if (_accounts is not null) _accounts.Changed -= OnAccountsChanged;
        _issueLoad?.Cancel();
        _issueLoad?.Dispose();
        _refresh?.Cancel();
        _refresh?.Dispose();
    }

    private void OnRemotesChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    private void OnAccountsChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    private void OnRefsChanged(object? sender, EventArgs e)
    {
        if (IsConnected && _time.GetUtcNow() - _lastRefresh >= StaleAfter) _ = RefreshAsync();
    }

    /// <summary>
    /// The remote hosted on a supported service, with the hosted repository: <c>origin</c> first, then the other
    /// remotes in their order (pure, tested).
    /// </summary>
    internal static (GitRemote Remote, HostRepository Repository)? FindRepository(IReadOnlyList<GitRemote> remotes, HostAccounts accounts)
    {
        foreach (var remote in remotes.OrderBy(r => r.Name == "origin" ? 0 : 1))
        {
            if (accounts.ResolveRepository(RemoteUrl.TryParse(remote.FetchUrl)) is { } repository) return (remote, repository);
        }
        return null;
    }

    /// <summary>Reads the pull requests and the issues again (toolbar of the section, palette, account or remote change).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_accounts is null || _lifetime.IsCancellationRequested) return;

        _refresh?.Cancel();
        _refresh?.Dispose();
        var source = _refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var cancellationToken = source.Token;
        _issueLoad?.Cancel();
        _loadingMoreIssues = false;
        _filteringIssues = false;

        if (FindRepository(_refActions.Remotes, _accounts) is not var (remote, repository))
        {
            Apply(HostingState.Unavailable, null, null, [], [], string.Empty);
            return;
        }

        if (_accounts.AccountOf(repository.Provider, ProfileId) is null)
        {
            Apply(HostingState.SignedOut, repository, remote.Name, [], [], string.Empty);
            return;
        }

        // Lists already read stay visible while they are read again.
        var sameRepository = Repository == repository;
        if (!sameRepository) _issueQuery = IssueQuery.Default;
        Apply(HostingState.Loading, repository, remote.Name, sameRepository ? PullRequests : [], sameRepository ? Issues : [], string.Empty,
            sameRepository && _issuesHaveMore);

        var pullRequests = _accounts.ListPullRequestsAsync(repository, ProfileId, cancellationToken);
        var issues = ListIssuePageAsync(repository, _issueQuery, 1, cancellationToken);
        await Task.WhenAll(pullRequests, issues);
        if (cancellationToken.IsCancellationRequested) return;
        _lastRefresh = _time.GetUtcNow();

        IReadOnlyList<PullRequest> pulls = [];
        IReadOnlyList<Issue> openIssues = [];
        var issuesHaveMore = false;
        var error = string.Empty;
        switch (pullRequests.Result)
        {
            case Success<IReadOnlyList<PullRequest>> { Value: var list }: pulls = list; break;
            case AuthenticationFailed a: error = a.Message; break;
            case Failure f: error = f.Message; break;
            case Canceled: return;
            default: error = Loc.Get("Hosting.UnexpectedResponse"); break;
        }
        switch (issues.Result)
        {
            case Success<IssuePage> { Value: var page }: openIssues = page.Items; issuesHaveMore = page.HasMore; break;
            case AuthenticationFailed a when error.Length == 0: error = a.Message; break;
            case Failure f when error.Length == 0: error = f.Message; break;
            case Canceled: return;
        }

        _issuePages = 1;
        Apply(error.Length == 0 ? HostingState.Ready : HostingState.Failed, repository, remote.Name, pulls, openIssues, error, issuesHaveMore);
        if (error.Length == 0) await LoadStatusesAsync(pulls, repository, cancellationToken);
    }

    // A page of issues for the query: the filtered and paged call when the service has one, the plain list otherwise.
    private Task<GitResult<IssuePage>> ListIssuePageAsync(HostRepository repository, IssueQuery query, int page, CancellationToken cancellationToken)
    {
        if (_accounts is { } accounts && accounts.SupportsIssueFilters(repository.Provider))
            return accounts.ListIssuePageAsync(repository, query, page, ProfileId, cancellationToken);
        return ReadFirstPageAsync();

        async Task<GitResult<IssuePage>> ReadFirstPageAsync()
        {
            var result = await _accounts!.ListIssuesAsync(repository, ProfileId, cancellationToken);
            switch (result)
            {
                case Success<IReadOnlyList<Issue>> { Value: var list }: return new Success<IssuePage>(new IssuePage(list, false));
                case AuthenticationFailed a: return a;
                case Failure f: return f;
                default: return Canceled.Instance;
            }
        }
    }

    /// <summary>Reads the issues again with other filters (the pull requests stay as they are); on failure the previous filters and list stay.</summary>
    public async Task SetIssueFilterAsync(IssueQuery query)
    {
        if (_accounts is null || Repository is not { } repository || !CanFilterIssues || query == _issueQuery) return;

        _issueLoad?.Cancel();
        _issueLoad?.Dispose();
        var source = _issueLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _loadingMoreIssues = false;
        _filteringIssues = true;
        var result = await ListIssuePageAsync(repository, query, 1, source.Token);
        if (source.IsCancellationRequested) return;

        _filteringIssues = false;
        if (result is not Success<IssuePage> { Value: var page })
        {
            ReportFailure(Loc.Get("Hosting.IssueFilter.Failed"), result);
            return;
        }
        _issueQuery = query;
        _issuePages = 1;
        Issues = page.Items;
        _issuesHaveMore = page.HasMore;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the next page of the issue list and adds it at the end (J6.10a).</summary>
    [RelayCommand]
    public async Task LoadMoreIssuesAsync()
    {
        if (_accounts is null || Repository is not { } repository || !CanFilterIssues || !_issuesHaveMore || _loadingMoreIssues || _filteringIssues) return;

        _issueLoad?.Dispose();
        var source = _issueLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var query = _issueQuery;
        _loadingMoreIssues = true;
        Changed?.Invoke(this, EventArgs.Empty);

        var result = await ListIssuePageAsync(repository, query, _issuePages + 1, source.Token);
        if (source.IsCancellationRequested) return;

        _loadingMoreIssues = false;
        if (result is Success<IssuePage> { Value: var page })
        {
            _issuePages++;
            // The list is sorted by update date: an issue updated meanwhile may come again on the next page.
            var known = Issues.Select(i => i.Number).ToHashSet();
            Issues = [.. Issues, .. page.Items.Where(i => !known.Contains(i.Number))];
            _issuesHaveMore = page.HasMore;
        }
        else
        {
            ReportFailure(Loc.Get("Hosting.Issues"), result);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back to the open issues, without other filter.</summary>
    [RelayCommand]
    public Task ClearIssueFilterAsync() => SetIssueFilterAsync(IssueQuery.Default);

    /// <summary>Asks for the text to search the issues for; an empty text removes the search.</summary>
    [RelayCommand]
    public async Task SearchIssuesAsync()
    {
        if (_inputs is null || !CanFilterIssues) return;

        var dialog = new InputDialogViewModel(Loc.Get("Hosting.SearchIssues.Title"), Loc.Get("Hosting.SearchIssues.Confirm"), Loc.Get("Hosting.SearchIssues.Header"), _ => null, _issueQuery.Search)
        {
            Description = Loc.Get(Repository is { Provider: HostingProvider.AzureDevOps } ? "Hosting.SearchIssues.DescriptionWorkItems" : "Hosting.SearchIssues.Description"),
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;
        await SetIssueFilterAsync(_issueQuery with { Search = dialog.Value });
    }

    // Checks and review of the latest pull requests (J6.6), a few at a time. One that cannot be read keeps its previous
    // status (or none): the list itself is already shown. Shown once all are read.
    private async Task LoadStatusesAsync(IReadOnlyList<PullRequest> pulls, HostRepository repository, CancellationToken cancellationToken)
    {
        if (_accounts is null || pulls.Count == 0) return;

        using var gate = new SemaphoreSlim(StatusParallelism);
        var reads = pulls.Take(StatusLimit).Select(async pull =>
        {
            try
            {
                await gate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return (pull.Number, Status: (PullRequestStatus?)null);
            }
            try
            {
                var result = await _accounts.GetPullRequestStatusAsync(repository, pull, ProfileId, cancellationToken: cancellationToken);
                return (pull.Number, Status: result is Success<PullRequestStatus> { Value: var status } ? status : null);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var done = await Task.WhenAll(reads);
        if (cancellationToken.IsCancellationRequested) return;

        var statuses = new Dictionary<int, PullRequestStatus>();
        foreach (var (number, status) in done)
        {
            if (status is not null) statuses[number] = status;
            else if (_statuses.TryGetValue(number, out var previous)) statuses[number] = previous;
        }
        _statuses = statuses;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(
        HostingState state, HostRepository? repository, string? remoteName,
        IReadOnlyList<PullRequest> pullRequests, IReadOnlyList<Issue> issues, string error, bool issuesHaveMore = false)
    {
        if (Repository != repository)
        {
            _statuses = [];
            _issueQuery = IssueQuery.Default;
        }
        State = state;
        Repository = repository;
        RemoteName = remoteName;
        PullRequests = pullRequests;
        Issues = issues;
        _issuesHaveMore = issuesHaveMore;
        ErrorMessage = error;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanFilterIssues));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens a pull request, an issue or the repository in the browser.</summary>
    [RelayCommand]
    private async Task OpenUrlAsync(string url)
    {
        if (_launcher is null || await _launcher.OpenAsync(url)) return;
        _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Issue.Failure.BrowserFailed"), url);
    }

    /// <summary>Shows a pull request in the main area of the tab; set by the tab (J6.9a).</summary>
    public Func<PullRequest, Task>? PullRequestOpener { get; set; }

    /// <summary>
    /// Shows the pull request <paramref name="number"/> in the app when the service gives its detail, in the browser otherwise
    /// (J6.9a).
    /// </summary>
    public async Task ShowPullRequestAsync(int number)
    {
        if (PullRequests.FirstOrDefault(p => p.Number == number) is not { } pull) return;
        if (CanShowPullRequest && PullRequestOpener is { } open) await open(pull);
        else await OpenUrlAsync(pull.Url);
    }

    /// <summary>Shows an issue in the main area of the tab; set by the tab (J6.10a).</summary>
    public Func<Issue, Task>? IssueOpener { get; set; }

    /// <summary>Shows the issue <paramref name="number"/> in the app when the service gives its detail, in the browser otherwise (J6.10a).</summary>
    public async Task ShowIssueAsync(int number)
    {
        if (Issues.FirstOrDefault(i => i.Number == number) is not { } issue) return;
        if (CanShowIssue && IssueOpener is { } open) await open(issue);
        else await OpenUrlAsync(issue.Url);
    }

    /// <summary>"Nouvelle issue…" is offered: a hosted repository with an account, whose service accepts writes on issues (J6.10c).</summary>
    public bool CanCreateIssue => IsConnected && _inputs is not null && _accounts is not null && Repository is { } hosted && _accounts.SupportsIssueWrite(hosted.Provider);

    /// <summary>Asks for a title and a description, creates the issue on the service, then shows it in the issue screen (J6.10c).</summary>
    [RelayCommand]
    public async Task CreateIssueAsync()
    {
        var failureTitle = Loc.Get("Hosting.CreateFailed");
        if (_accounts is null || _inputs is null || Repository is not { } repository || !CanCreateIssue) return;

        var dialog = new InputDialogViewModel(Loc.Get("Hosting.NewIssue.Title"), Loc.Get("Hosting.NewIssue.Confirm"), Loc.Get("Hosting.TitleHeader"), name => name.Trim().Length == 0 ? string.Empty : null)
        {
            Description = repository.FullName,
            SecondHeader = Loc.Get("Hosting.NewIssue.BodyHeader"),
            SecondPlaceholder = Loc.Get("Hosting.NewIssue.BodyPlaceholder"),
            IsSecondMultiline = true,
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        var created = await _accounts.CreateIssueAsync(repository, new NewIssue(dialog.Value.Trim(), dialog.SecondText.Trim()), ProfileId, _lifetime);
        if (created is not Success<Issue> { Value: var issue })
        {
            ReportFailure(failureTitle, created);
            return;
        }

        _notifier?.Notify(NotificationSeverity.Success, Loc.Format("Hosting.IssueCreated", issue.Number), issue.Title);
        if (CanShowIssue && IssueOpener is { } open) await open(issue);
        else await OpenUrlAsync(issue.Url);
        await RefreshAsync();
    }

    /// <summary>The open pull request whose source is <paramref name="branch"/> on the repository itself (not a fork); null when there is none.</summary>
    public PullRequest? PullRequestOf(string branch) => PullRequests.FirstOrDefault(p =>
        p.HeadBranch == branch
        && (p.HeadRepository is null || string.Equals(p.HeadRepository, Repository?.FullName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>"Créer une pull request…" is offered for this ref: a local branch, with a hosted remote and an account.</summary>
    public bool CanCreatePullRequest(GitRef branch) => IsConnected && _inputs is not null && branch.Kind == GitRefKind.LocalBranch;

    /// <summary>
    /// Creates a pull request from a pushed local branch to the default branch of the repository: asks for a title, a
    /// description and the draft state, then opens the new pull request in the browser.
    /// </summary>
    [RelayCommand]
    private Task CreatePullRequestAsync(GitRef branch) => CreatePullRequestAsync(branch, null);

    /// <summary>
    /// Same as the command, towards <paramref name="target"/> (a local branch dropped on, whose pushed counterpart is the
    /// base) instead of the default branch of the repository.
    /// </summary>
    public async Task CreatePullRequestAsync(GitRef branch, GitRef? target)
    {
        var title = Loc.Get("Hosting.Pr.Title");
        if (_accounts is null || _inputs is null || Repository is not { } repository || RemoteName is not { } remote || !CanCreatePullRequest(branch)) return;

        var head = HeadBranchOn(branch, remote);
        if (head is null)
        {
            _notifier?.Notify(NotificationSeverity.Warning, title,
                Loc.Format("Hosting.Pr.PushFirst", branch.Name, remote));
            return;
        }

        if (PullRequestOf(head) is { } existing)
        {
            _notifier?.Notify(NotificationSeverity.Info, title, Loc.Format("Hosting.Pr.Exists", head, existing.Number));
            await OpenUrlAsync(existing.Url);
            return;
        }

        string baseBranch;
        if (target is { Kind: GitRefKind.RemoteBranch } remoteTarget)
        {
            // A remote badge is already the base on the host ("origin/main" gives "main"); another remote is a fork.
            if (!remoteTarget.Name.StartsWith(remote + "/", StringComparison.Ordinal) || remoteTarget.Name.Length <= remote.Length + 1)
            {
                _notifier?.Notify(NotificationSeverity.Warning, title, Loc.Format("Hosting.Pr.NotRemoteBranch", remoteTarget.Name, remote));
                return;
            }
            baseBranch = remoteTarget.Name[(remote.Length + 1)..];
        }
        else if (target is not null)
        {
            if (HeadBranchOn(target, remote) is not { } pushedTarget)
            {
                _notifier?.Notify(NotificationSeverity.Warning, title,
                    Loc.Format("Hosting.Pr.PushFirst", target.Name, remote));
                return;
            }
            baseBranch = pushedTarget;
        }
        else
        {
            var defaultBranch = await _accounts.GetDefaultBranchAsync(repository, ProfileId, _lifetime);
            if (defaultBranch is not Success<string> { Value: var name })
            {
                ReportFailure(Loc.Get("Hosting.CreateFailed"), defaultBranch);
                return;
            }
            baseBranch = name;
        }
        if (baseBranch == head)
        {
            _notifier?.Notify(NotificationSeverity.Warning, title, Loc.Format("Hosting.Pr.SameBranch", head, repository.FullName));
            return;
        }

        var request = _accounts.SupportsPullRequestCreateOptions(repository.Provider)
            ? await AskFullPullRequestAsync(repository, head, baseBranch)
            : await AskPullRequestAsync(repository, head, baseBranch);
        if (request is null) return;

        var created = await _accounts.CreatePullRequestAsync(repository, request, ProfileId, _lifetime);
        if (created is not Success<PullRequest> { Value: var pullRequest })
        {
            ReportFailure(Loc.Get("Hosting.CreateFailed"), created);
            return;
        }

        _notifier?.Notify(NotificationSeverity.Success, Loc.Format("Hosting.Pr.Created", pullRequest.Number), pullRequest.Title);
        if (request.Reviewers is { Count: > 0 } || request.Labels is { Count: > 0 })
        {
            // The pull request exists: a refused label or reviewer is a warning, not a failed creation.
            var applied = await _accounts.ApplyPullRequestCreateOptionsAsync(repository, pullRequest, request, ProfileId, _lifetime);
            switch (applied)
            {
                case Failure f:
                    _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Hosting.Pr.OptionsNotApplied"), f.Message);
                    break;
                case AuthenticationFailed a:
                    _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Hosting.Pr.OptionsNotApplied"), a.Message);
                    break;
                case Success<Done> or Canceled or Conflict:
                    break;
            }
        }
        await OpenUrlAsync(pullRequest.Url);
        await RefreshAsync();
    }

    // Title, description and draft state only; null when the user cancels.
    private async Task<NewPullRequest?> AskPullRequestAsync(HostRepository repository, string head, string baseBranch)
    {
        var dialog = new InputDialogViewModel(Loc.Get("Pr.New.Title"), Loc.Get("Pr.New.Confirm"), Loc.Get("Hosting.TitleHeader"), name => name.Length == 0 ? string.Empty : null, TitleFor(head))
        {
            Description = $"{head} → {baseBranch} · {repository.FullName}",
            SecondHeader = Loc.Get("Hosting.Pr.BodyHeader"),
            SecondPlaceholder = Loc.Get("Hosting.Pr.BodyPlaceholder"),
            IsSecondMultiline = true,
            OptionText = Loc.Get("Pr.State.Draft"),
        };
        return await _inputs!.ShowAsync(dialog, _lifetime)
            ? new NewPullRequest(dialog.Value, dialog.SecondText, head, baseBranch, dialog.OptionChecked)
            : null;
    }

    // Same plus reviewers, labels and linked issues (J6.9l). Reading the reviewers is best effort: a token that cannot list the
    // people still creates the pull request, with a warning instead of the list.
    private async Task<NewPullRequest?> AskFullPullRequestAsync(HostRepository repository, string head, string baseBranch)
    {
        var options = await _accounts!.GetPullRequestCreateOptionsAsync(repository, ProfileId, _lifetime);
        switch (options)
        {
            case Success<PullRequestCreateOptions>:
                break;
            case Canceled:
                return null;
            case AuthenticationFailed a:
                _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Hosting.Pr.ReviewersUnavailable"), a.Message);
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Hosting.Pr.ReviewersUnavailable"), f.Message);
                break;
            case Conflict:
                break;
        }

        var dialog = new NewPullRequestDialogViewModel(
            $"{head} → {baseBranch} · {repository.FullName}", TitleFor(head), head, baseBranch,
            options.Value is Success<PullRequestCreateOptions> { Value: var offered } ? offered : null, Issues);
        return await _inputs!.ShowAsync(dialog, _lifetime) && dialog.CanConfirm ? dialog.ToRequest() : null;
    }

    /// <summary>
    /// Name of the branch on <paramref name="remote"/> that <paramref name="branch"/> was pushed to ("feature/x" for the
    /// upstream "origin/feature/x"); null when it has no upstream, or one on another remote (forks are not supported).
    /// </summary>
    internal static string? HeadBranchOn(GitRef branch, string remote) =>
        branch.Upstream is { } upstream && upstream.StartsWith(remote + "/", StringComparison.Ordinal) && upstream.Length > remote.Length + 1
            ? upstream[(remote.Length + 1)..]
            : null;

    /// <summary>Default title of a pull request: "feature/oauth-refresh" becomes "Oauth refresh".</summary>
    internal static string TitleFor(string branch)
    {
        var name = branch[(branch.LastIndexOf('/') + 1)..].Replace('-', ' ').Replace('_', ' ').Trim();
        return name.Length == 0 ? branch : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private void ReportFailure<T>(string title, GitResult<T> result)
    {
        switch (result)
        {
            case AuthenticationFailed a: _notifier?.Notify(NotificationSeverity.Error, title, a.Message); break;
            case Failure f: _notifier?.Notify(NotificationSeverity.Error, title, f.Message); break;
            case Conflict or Canceled or Success<T>: break;
        }
    }
}
