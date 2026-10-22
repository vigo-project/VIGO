using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class HistoryActionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly TestRepoBuilder _repo;
    private readonly GitSha _base;
    private readonly GitSha _pick;
    private readonly GitSha _conflicting;
    private readonly GitSha _mainTip;

    /// <summary>main: base (a.txt) → main-2 (a.txt); feature (from base): feature-1 (b.txt), feature-2 (a.txt: conflicts with main).</summary>
    public HistoryActionsViewModelTests()
    {
        _repo = TestRepoBuilder.Create();
        _base = GitSha.Parse(_repo.Commit("base", ("a.txt", "base\n")));
        _repo.Branch("feature");
        _pick = GitSha.Parse(_repo.Commit("feature-1", ("b.txt", "feature\n")));
        _conflicting = GitSha.Parse(_repo.Commit("feature-2", ("a.txt", "feature\n")));
        _repo.Checkout("main");
        _mainTip = GitSha.Parse(_repo.Commit("main-2", ("a.txt", "main\n")));
    }

    public void Dispose() => _repo.Dispose();

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, _confirmations);
        await tab.LoadAsync();
        return tab;
    }

    private async Task ResolveAsync(RepositoryTabViewModel tab, string path)
    {
        _repo.WriteFile(path, "resolved\n");
        Assert.True((await _git.StageAsync(_repo.Path, [path], TestContext.Current.CancellationToken)).Value is Results.Done);
        await tab.WorkingTree.RefreshAsync();
    }

    [Fact]
    public async Task The_menu_entries_follow_the_checked_out_commit()
    {
        using var tab = await OpenAsync();

        Assert.Equal("Reset main ici", tab.History.ResetLabel);
        Assert.True(tab.History.CanCherryPick(_pick));
        Assert.False(tab.History.CanCherryPick(_mainTip));
        Assert.True(tab.History.CanRevert(_mainTip));
        Assert.True(tab.History.CanReset(_base));
    }

    [Fact]
    public async Task Cherry_pick_applies_the_commit_and_says_so()
    {
        using var tab = await OpenAsync();

        await tab.History.CherryPickCommand.ExecuteAsync(_pick);

        Assert.Equal("feature-1", _repo.Git("log", "-1", "--format=%s").Trim());
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Cherry-pick appliqué", var m) && m == $"{_pick.ToShortString()} « feature-1 » appliqué sur main.");
        Assert.Equal(GitSha.Parse(_repo.Head()), tab.Graph.Head);
    }

    [Fact]
    public async Task A_conflicting_cherry_pick_shows_the_banner_and_continues_once_resolved()
    {
        using var tab = await OpenAsync();
        Assert.True(tab.Graph.TrySelect(_conflicting));

        await tab.History.CherryPickCommand.ExecuteAsync(_conflicting);

        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning && n.Title == "Conflit détecté");
        Assert.True(tab.Graph.IsWorkInProgressRow(tab.Graph.SelectedRow));
        Assert.True(tab.ShowWorkingTree);
        Assert.True(tab.Merge.CanContinue);
        Assert.Equal($"Cherry-pick de {_conflicting.ToShortString()} « feature-2 ».", tab.Merge.OperationTitle);
        Assert.StartsWith("1 fichier en conflit.", tab.Merge.OperationDetail, StringComparison.Ordinal);
        Assert.Equal("Abandonner le cherry-pick", tab.Merge.AbortLabel);
        Assert.Equal($"Cherry-pick de {_conflicting.ToShortString()} en cours · 1 conflit", tab.Graph.WorkInProgressText);
        Assert.False(tab.History.CanStart);
        Assert.Equal("feature-2", tab.WorkingTree.CommitForm.Summary);

        await ResolveAsync(tab, "a.txt");
        await tab.Merge.ContinueCommand.ExecuteAsync(null);

        Assert.False(tab.Merge.HasOperation);
        Assert.Equal("feature-2", _repo.Git("log", "-1", "--format=%s").Trim());
        Assert.Equal(_mainTip.ToString(), _repo.Git("rev-parse", "HEAD~1").Trim());
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Cherry-pick terminé", _));
    }

    [Fact]
    public async Task A_conflicting_cherry_pick_can_be_concluded_by_the_commit_form()
    {
        using var tab = await OpenAsync();
        await tab.History.CherryPickCommand.ExecuteAsync(_conflicting);
        await ResolveAsync(tab, "a.txt");

        await tab.WorkingTree.CommitForm.CommitCommand.ExecuteAsync(null);

        Assert.False(tab.Merge.HasOperation);
        Assert.Equal("feature-2", _repo.Git("log", "-1", "--format=%s").Trim());
        Assert.Single(_repo.Git("log", "-1", "--format=%P").Trim().Split(' '));
    }

    [Fact]
    public async Task Aborting_a_cherry_pick_goes_back()
    {
        using var tab = await OpenAsync();
        await tab.History.CherryPickCommand.ExecuteAsync(_conflicting);

        await tab.Merge.AbortCommand.ExecuteAsync(null);

        Assert.False(tab.Merge.HasOperation);
        Assert.Equal(_mainTip.ToString(), _repo.Head());
        Assert.Contains(_notifier.Items, n => n.Title == "Cherry-pick abandonné");
    }

    [Fact]
    public async Task Revert_creates_the_undoing_commit()
    {
        _repo.Commit("main-3", ("c.txt", "c\n"));
        using var tab = await OpenAsync();

        await tab.History.RevertCommand.ExecuteAsync(GitSha.Parse(_repo.Head()));

        Assert.Equal("Revert \"main-3\"", _repo.Git("log", "-1", "--format=%s").Trim());
        Assert.False(File.Exists(Path.Combine(_repo.Path, "c.txt")));
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Revert effectué", _));
    }

    [Fact]
    public async Task Mixed_reset_moves_the_branch_and_keeps_the_changes()
    {
        using var tab = await OpenAsync();

        await tab.History.ResetCommand.ExecuteAsync(new ResetPlan(_base, ResetMode.Mixed));

        Assert.Equal(_base.ToString(), _repo.Head());
        Assert.Empty(_confirmations.Requests);
        Assert.Equal("main\n", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Reset effectué", var m)
            && m == $"main ramenée sur {_base.ToShortString()} « base » (1 commit retiré de la branche). Leurs modifications sont dans le répertoire de travail.");
        Assert.Equal(_base, tab.Graph.Head);
        Assert.Equal(1, tab.WorkingTree.ChangeCount);
    }

    [Fact]
    public async Task Hard_reset_asks_first_and_does_nothing_when_refused()
    {
        _repo.WriteFile("a.txt", "local\n");
        using var tab = await OpenAsync();

        await tab.History.ResetCommand.ExecuteAsync(new ResetPlan(_base, ResetMode.Hard));

        var request = Assert.Single(_confirmations.Requests);
        Assert.True(request.IsDestructive);
        Assert.Equal("Reset hard de main ?", request.Title);
        Assert.Contains("1 commit ne sera plus sur la branche.", request.Message, StringComparison.Ordinal);
        Assert.Contains("Les modifications non validées de 1 fichier suivi seront perdues définitivement.", request.Message, StringComparison.Ordinal);
        Assert.Equal(_mainTip.ToString(), _repo.Head());
        Assert.Equal("local\n", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Hard_reset_once_confirmed_drops_the_changes()
    {
        _repo.WriteFile("a.txt", "local\n");
        _confirmations.Answer = true;
        using var tab = await OpenAsync();

        await tab.History.ResetCommand.ExecuteAsync(new ResetPlan(_base, ResetMode.Hard));

        Assert.Equal(_base.ToString(), _repo.Head());
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Equal(0, tab.WorkingTree.ChangeCount);
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Reset effectué", _));
    }

    private sealed class FakeConfirmations : IConfirmationService
    {
        public bool Answer { get; set; }

        public List<ConfirmationRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer);

        public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConfirmationOutcome(Answer, request.OptionChecked));
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
