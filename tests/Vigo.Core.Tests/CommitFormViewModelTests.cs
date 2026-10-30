using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class CommitFormViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeConfirmations _confirmations = new();

    private RepositoryTabViewModel CreateTab(TestRepoBuilder repo) => new(
        new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false),
        _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, _confirmations);

    private static string Message(TestRepoBuilder repo) => repo.Git("log", "-1", "--format=%B").ReplaceLineEndings("\n");

    [Fact]
    public async Task Commit_records_the_staged_files_then_clears_the_form_and_reloads_the_graph()
    {
        using var repo = TestRepoBuilder.Create();
        var init = repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").WriteFile("b.txt", "b\n").Stage("a.txt");
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        var form = tab.WorkingTree.CommitForm;

        Assert.Equal("Valider 1 fichier", form.CommitLabel);
        Assert.False(form.CanCommit);
        form.Summary = "Change a";
        form.Description = "Why\r\nit matters";
        Assert.True(form.CanCommit);
        Assert.Equal("8 / 72", form.SummaryLengthText);

        await form.CommitCommand.ExecuteAsync(null);
        await tab.Graph.WhenReloadedAsync();

        Assert.Equal("Change a\n\nWhy\nit matters\n\n", Message(repo));
        Assert.Equal(init, repo.Git("rev-parse", "HEAD^").Trim());
        Assert.Equal((string.Empty, string.Empty, false), (form.Summary, form.Description, form.IsAmend));
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Success && n.Message == "Change a");
        Assert.Equal(["b.txt"], tab.WorkingTree.Unstaged.Select(f => f.Name));
        Assert.Equal("Aucun fichier indexé", form.CommitLabel);

        // The new commit is HEAD, below the work-in-progress row of b.txt.
        Assert.Equal(GitSha.Parse(repo.Head()), tab.Graph.Head);
        Assert.Equal(3, tab.Graph.RowCount);
        Assert.Equal("Change a", tab.Graph.GetCommit(1).Subject);
        Assert.Equal(1, tab.Graph.HeadRow);
    }

    [Fact]
    public async Task First_commit_of_an_empty_repository()
    {
        using var repo = TestRepoBuilder.Create();
        repo.WriteFile("a.txt", "a\n").Stage("a.txt");
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        var form = tab.WorkingTree.CommitForm;
        Assert.False(form.CanAmend);

        form.Summary = "Initial commit";
        await form.CommitCommand.ExecuteAsync(null);
        await tab.Graph.WhenReloadedAsync();

        Assert.Equal("Initial commit\n\n", Message(repo));
        Assert.Equal(1, tab.Graph.RowCount);
        Assert.True(form.CanAmend);
        Assert.Equal("main", tab.HeadDisplay);
    }

    [Fact]
    public async Task Amend_starts_from_the_HEAD_message_and_unchecking_restores_an_empty_form()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.Commit("Summary line\n\nBody line", ("b.txt", "b\n"));
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        var form = tab.WorkingTree.CommitForm;

        form.IsAmend = true;
        await form.WhenLoadedAsync();
        Assert.Equal(("Summary line", "Body line"), (form.Summary, form.Description));
        Assert.StartsWith("Modifier le commit ", form.CommitLabel, StringComparison.Ordinal);
        Assert.True(form.CanCommit);

        form.IsAmend = false;
        Assert.Equal((string.Empty, string.Empty), (form.Summary, form.Description));

        form.IsAmend = true;
        await form.WhenLoadedAsync();
        form.Summary = "Reworded";
        await form.CommitCommand.ExecuteAsync(null);
        await tab.Graph.WhenReloadedAsync();

        Assert.Equal("Reworded\n\nBody line\n\n", Message(repo));
        Assert.Equal(2, tab.Graph.RowCount);
        Assert.Equal("Reworded", tab.Graph.GetCommit(0).Subject);
        Assert.Equal(0, _confirmations.Asked);
    }

    [Fact]
    public async Task Amending_a_pushed_commit_asks_first()
    {
        using var remote = TestRepoBuilder.Create(bare: true);
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.Git("remote", "add", "origin", remote.Path);
        repo.Git("push", "--quiet", "-u", "origin", "main");
        var head = repo.Head();
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        var form = tab.WorkingTree.CommitForm;
        form.IsAmend = true;
        await form.WhenLoadedAsync();
        form.Summary = "Rewritten";

        _confirmations.Answer = false;
        await form.CommitCommand.ExecuteAsync(null);

        Assert.Equal(1, _confirmations.Asked);
        Assert.Equal(head, repo.Head());
        Assert.Equal("Rewritten", form.Summary);
    }

    [Fact]
    public async Task Hook_failure_is_reported_in_full_and_the_message_is_kept()
    {
        using var repo = TestRepoBuilder.Create();
        var head = repo.Commit("init", ("a.txt", "a\n"));
        var hooks = Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        TestRepoBuilder.WriteHook(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\necho 'first' >&2\necho 'second' >&2\nexit 1\n");
        repo.Git("config", "core.hooksPath", hooks.Replace('\\', '/'));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        var form = tab.WorkingTree.CommitForm;
        form.Summary = "Blocked";

        await form.CommitCommand.ExecuteAsync(null);

        Assert.Equal(head, repo.Head());
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Error, Message: "first\nsecond" });
        Assert.Equal("Blocked", form.Summary);

        await form.CommitSkippingHooksCommand.ExecuteAsync(null);
        Assert.Equal("Blocked\n\n", Message(repo));
    }

    [Fact]
    public async Task Signing_indicator_follows_the_config()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.Git("config", "commit.gpgsign", "yes");
        repo.Git("config", "gpg.format", "ssh");
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        await tab.WorkingTree.CommitForm.WhenLoadedAsync();

        Assert.Equal("Signé SSH", tab.WorkingTree.CommitForm.SigningLabel);
    }

    [Fact]
    public async Task Graph_follows_HEAD_moved_outside_and_keeps_the_selected_commit()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.txt", "1\n"));
        repo.Commit("second", ("a.txt", "2\n"));
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        Assert.True(tab.Graph.TrySelect(GitSha.Parse(first)));
        Assert.Equal(1, tab.Graph.SelectedRow);

        repo.Commit("third (terminal)", ("a.txt", "3\n"));
        await tab.WorkingTree.RefreshAsync();
        await tab.Graph.WhenReloadedAsync();

        Assert.Equal(3, tab.Graph.RowCount);
        Assert.Equal(GitSha.Parse(first), tab.Graph.SelectedSha);
        Assert.Equal(2, tab.Graph.SelectedRow);

        repo.Branch("feature");
        await tab.WorkingTree.RefreshAsync();
        await tab.Graph.WhenReloadedAsync();
        Assert.Equal("feature", tab.Graph.HeadBranch);
        Assert.Equal("feature", tab.HeadDisplay);
    }

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

public sealed class CommitFormGenerationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class BlockingBackend : IAiBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AiProvider Provider => AiProvider.Cli;

        public Task<GitResult> CheckAsync(AiRequest settings, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult>(Done.Instance);

        public async Task<GitResult<string>> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                return new Success<string>(await Reply.Task.WaitAsync(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                return Canceled.Instance;
            }
        }
    }

    private sealed class NoVault : ISecretVault
    {
        public string? Retrieve(string name) => null;

        public void Store(string name, string secret) { }

        public void Remove(string name) { }
    }

    private static async Task<(RepositoryTabViewModel Tab, BlockingBackend Backend, TestRepoBuilder Repo)> CreateAsync()
    {
        var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");
        var git = new GitService(new GitCli());
        var preferences = new Preferences(new InMemoryPreferencesStore(), new UserPreferences { AiProvider = AiProvider.Cli });
        var backend = new BlockingBackend();
        var generator = new CommitMessageGenerator(preferences, git, new NoVault(), [backend]);
        var tab = new RepositoryTabViewModel(
            new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false),
            git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null,
            commitMessages: generator);
        await tab.LoadAsync();
        return (tab, backend, repo);
    }

    [Fact]
    public async Task Generating_shows_the_loader_then_fills_the_form()
    {
        var (tab, backend, repo) = await CreateAsync();
        using var _ = repo;
        using var __ = tab;
        var form = tab.WorkingTree.CommitForm;
        Assert.True(form.CanGenerate);

        var run = form.GenerateCommand.ExecuteAsync(null);
        await backend.Started.Task.WaitAsync(Ct);
        Assert.True(form.IsGenerating);
        Assert.False(form.IsGenerateIdle);
        Assert.False(form.CanGenerate);

        backend.Reply.SetResult("Add feature\n\nBecause.");
        await run;

        Assert.False(form.IsGenerating);
        Assert.Equal(("Add feature", "Because."), (form.Summary, form.Description));
    }

    [Fact]
    public async Task Cancel_stops_the_generation_and_leaves_the_form_untouched()
    {
        var (tab, backend, repo) = await CreateAsync();
        using var _ = repo;
        using var __ = tab;
        var form = tab.WorkingTree.CommitForm;
        form.Summary = "mine";

        var run = form.GenerateCommand.ExecuteAsync(null);
        await backend.Started.Task.WaitAsync(Ct);
        form.CancelGenerationCommand.Execute(null);
        await run;

        Assert.False(form.IsGenerating);
        Assert.True(form.IsGenerateIdle);
        Assert.Equal("mine", form.Summary);
    }
}
