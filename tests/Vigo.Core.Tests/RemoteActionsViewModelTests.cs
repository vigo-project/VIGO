using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class RemoteActionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeConfirmations _confirmations = new();
    private readonly TestRepoBuilder _remote;
    private readonly TestRepoBuilder _mine;
    private readonly TestRepoBuilder _theirs;

    public RemoteActionsViewModelTests()
    {
        _remote = TestRepoBuilder.Create(bare: true);
        _mine = TestRepoBuilder.Create();
        _mine.Commit("init", ("a.txt", "1\n2\n3\n"));
        _mine.Git("remote", "add", "origin", _remote.Path);
        _mine.Git("push", "--quiet", "--set-upstream", "origin", "main");
        _theirs = TestRepoBuilder.Clone(_remote.Path);
    }

    public void Dispose()
    {
        _theirs.Dispose();
        _mine.Dispose();
        _remote.Dispose();
    }

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var tab = new RepositoryTabViewModel(
            new RepositoryInfo(_mine.Path, Path.Combine(_mine.Path, ".git"), "repo", "main", null, false, false, false),
            _git, _time, NullLogger.Instance, watchers: null, _notifier, _confirmations);
        await tab.LoadAsync();
        return tab;
    }

    private string RemoteHead() => _remote.Git("rev-parse", "refs/heads/main").Trim();

    private void TheyPush(string file = "b.txt", string content = "b\n")
    {
        _theirs.Commit("theirs", (file, content));
        _theirs.Git("push", "--quiet");
    }

    [Fact]
    public async Task Fetch_follows_the_prune_preference()
    {
        _theirs.Git("push", "--quiet", "origin", "main:refs/heads/gone", "main:refs/heads/gone2");
        _mine.Git("fetch", "--quiet");
        _theirs.Git("push", "--quiet", "origin", "--delete", "gone", "gone2");
        var preferences = new Preferences(new InMemoryPreferencesStore(), new UserPreferences { AutoPrune = false });
        using var tab = new RepositoryTabViewModel(
            new RepositoryInfo(_mine.Path, Path.Combine(_mine.Path, ".git"), "repo", "main", null, false, false, false),
            _git, _time, NullLogger.Instance, watchers: null, _notifier, _confirmations, preferences: preferences);
        await tab.LoadAsync();

        await tab.Remote.FetchCommand.ExecuteAsync(null);
        Assert.Contains("origin/gone", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);

        await preferences.UpdateAsync(p => p with { AutoPrune = true }, TestContext.Current.CancellationToken);
        await tab.Remote.FetchCommand.ExecuteAsync(null);
        Assert.DoesNotContain("origin/gone", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_shows_the_commits_to_pull_then_pull_brings_them()
    {
        TheyPush();
        using var tab = await OpenAsync();
        var remote = tab.Remote;
        Assert.Equal(0, tab.WorkingTree.Status?.Behind);
        Assert.True(remote.CanPull);

        await remote.FetchCommand.ExecuteAsync(null);
        await remote.WhenGraphReloadedAsync();

        Assert.False(remote.IsRunning);
        Assert.Equal(1, tab.WorkingTree.Status?.Behind);
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Success, Message: "1 commit à récupérer depuis origin/main." });
        Assert.Equal(2, tab.Graph.RowCount);

        await remote.PullCommand.ExecuteAsync(null);
        await remote.WhenGraphReloadedAsync();

        Assert.Equal(_theirs.Head(), _mine.Head());
        Assert.Equal(0, tab.WorkingTree.Status?.Behind);
        Assert.Contains(_notifier.Items, n => n is { Title: "Pull terminé", Message: "main est à jour avec origin/main." });
        Assert.Equal(GitSha.Parse(_theirs.Head()), tab.Graph.Head);
    }

    [Fact]
    public async Task Fetch_from_a_badge_fetches_the_remote_of_the_branch()
    {
        TheyPush();
        using var tab = await OpenAsync();
        var remote = tab.Remote;
        var refs = tab.Graph.Refs!;
        var local = refs.OfKind(GitRefKind.LocalBranch).Single(r => r.Name == "main");
        var tag = new GitRef("refs/tags/v1", "v1", GitRefKind.Tag, local.Target);

        Assert.False(remote.TryFetchRemoteOf(tag));
        Assert.True(remote.TryFetchRemoteOf(local));
        // IsRunning drops before the status is reread: the toast is the end of the operation.
        while (_notifier.Items.Count == 0) await Task.Delay(10, TestContext.Current.CancellationToken);
        await remote.WhenGraphReloadedAsync();

        Assert.Equal(1, tab.WorkingTree.Status?.Behind);
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Success, Message: "1 commit à récupérer depuis origin/main." });
    }

    [Fact]
    public async Task Push_sends_the_local_commits()
    {
        var mine = _mine.Commit("mine", ("b.txt", "b\n"));
        using var tab = await OpenAsync();
        Assert.Equal(1, tab.WorkingTree.Status?.Ahead);

        await tab.Remote.PushCommand.ExecuteAsync(null);

        Assert.Equal(mine, RemoteHead());
        Assert.Equal(0, tab.WorkingTree.Status?.Ahead);
        Assert.Contains(_notifier.Items, n => n is { Title: "Push terminé", Message: "1 commit poussé vers origin/main." });
    }

    [Fact]
    public async Task Rejected_push_is_reported_and_force_push_asks_first()
    {
        TheyPush("a.txt", "theirs\n");
        _mine.Commit("mine", ("a.txt", "mine\n"));
        var theirs = RemoteHead();
        using var tab = await OpenAsync();

        await tab.Remote.PushCommand.ExecuteAsync(null);
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Error, Title: "Échec du push" }
                                              && n.Message!.StartsWith("Push refusé", StringComparison.Ordinal));

        _confirmations.Answer = false;
        await tab.Remote.ForcePushCommand.ExecuteAsync(null);
        Assert.Equal(1, _confirmations.Asked);
        Assert.Equal(theirs, RemoteHead());
    }

    [Fact]
    public async Task Pull_conflict_is_reported_and_shown_in_the_working_tree()
    {
        TheyPush("a.txt", "1\ntheirs\n3\n");
        _mine.Commit("mine", ("a.txt", "1\nmine\n3\n"));
        using var tab = await OpenAsync();

        await tab.Remote.PullMergeCommand.ExecuteAsync(null);

        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Warning, Title: "Conflits à résoudre" });
        Assert.Equal(["a.txt"], tab.WorkingTree.Conflicted.Select(f => f.Path));
        _mine.Git("merge", "--abort");
    }

    [Fact]
    public async Task Commit_and_push_from_the_form()
    {
        using var tab = await OpenAsync();
        _mine.WriteFile("b.txt", "b\n").Stage("b.txt");
        await tab.WorkingTree.RefreshAsync();
        var form = tab.WorkingTree.CommitForm;
        form.Summary = "Add b";
        Assert.True(form.CanCommitAndPush);

        await form.CommitAndPushCommand.ExecuteAsync(null);

        Assert.Equal(_mine.Head(), RemoteHead());
        Assert.Equal("Add b", _remote.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Detached_HEAD_cannot_pull_nor_push()
    {
        _mine.Git("checkout", "--quiet", "--detach");
        using var tab = await OpenAsync();

        Assert.False(tab.Remote.CanPull);
        Assert.False(tab.Remote.CanPush);
        Assert.True(tab.Remote.CanFetch);
    }

    [Fact]
    public async Task Auto_fetch_is_silent_unless_new_commits_arrive_on_the_upstream()
    {
        using var tab = await OpenAsync();
        var remote = tab.Remote;
        Assert.True(remote.IsAutoFetchDue(TimeSpan.FromMinutes(1)));

        await remote.AutoFetchAsync();
        Assert.Empty(_notifier.Items);
        Assert.False(remote.IsAutoFetchDue(TimeSpan.FromMinutes(1)));

        TheyPush();
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(remote.IsAutoFetchDue(TimeSpan.FromMinutes(1)));
        await remote.AutoFetchAsync();
        await remote.WhenGraphReloadedAsync();

        Assert.False(remote.IsRunning);
        Assert.Equal(1, tab.WorkingTree.Status?.Behind);
        var toast = Assert.Single(_notifier.Items);
        Assert.Equal((NotificationSeverity.Info, "Nouveaux commits sur origin/main", "1 commit récupéré par le fetch automatique (1 commit à récupérer)."), toast);
    }

    [Fact]
    public async Task A_manual_fetch_announces_its_end_but_the_automatic_one_does_not()
    {
        using var tab = await OpenAsync();
        var announced = 0;
        tab.Remote.FetchCompleted += (_, _) => announced++;

        await tab.Remote.FetchCommand.ExecuteAsync(null);
        Assert.Equal(1, announced);

        await tab.Remote.AutoFetchAsync();
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task Manual_fetch_postpones_the_automatic_one()
    {
        using var tab = await OpenAsync();

        await tab.Remote.FetchCommand.ExecuteAsync(null);

        Assert.Equal(Now, tab.Remote.LastFetchStarted);
        Assert.False(tab.Remote.IsAutoFetchDue(TimeSpan.FromMinutes(1)));
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(tab.Remote.IsAutoFetchDue(TimeSpan.FromMinutes(1)));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tab.Remote.IsAutoFetchDue(TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(GitProgressPhase.Receiving, "Receiving objects:  42% (123/456), 1.20 MiB | 4.20 MiB/s", 42, 123L, 456L, "4.20 MiB/s", false, "Réception des objets : 42 % (123 / 456) · 4.20 MiB/s")]
    [InlineData(GitProgressPhase.Counting, "Counting objects: 100% (4/4), done.", 100, 4L, 4L, null, true, "Serveur : comptage des objets : 100 % (4 / 4)")]
    [InlineData(GitProgressPhase.Other, "Fetching origin", null, null, null, null, false, "Récupération de origin…")]
    public void Progress_is_described_in_French(
        GitProgressPhase phase, string text, int? percent, long? current, long? total, string? rate, bool remote, string expected) =>
        Assert.Equal(expected, RemoteActionsViewModel.Describe(new GitProgress(phase, text, percent, current, total, rate, remote)));

    private sealed class FakeConfirmations : IConfirmationService
    {
        public bool Answer { get; set; }

        public int Asked { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
