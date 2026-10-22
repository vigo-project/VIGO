using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class InteractiveRebaseViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly TestRepoBuilder _repo;
    private readonly GitSha _base;

    /// <summary>main: base, then one, two, "fixup! one", three.</summary>
    public InteractiveRebaseViewModelTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Git("config", "commit.gpgSign", "false");
        _base = GitSha.Parse(_repo.Commit("base", ("base.txt", "base\n")));
        _repo.Commit("one", ("one.txt", "1\n"));
        _repo.Commit("two", ("two.txt", "2\n"));
        _repo.Commit("fixup! one", ("one.txt", "1 fixed\n"));
        _repo.Commit("three", ("three.txt", "3\n"));
    }

    public void Dispose() => _repo.Dispose();

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier);
        await tab.LoadAsync();
        await tab.Rebase.OpenAsync(_base.ToString(), _base.ToShortString(), _base);
        return tab;
    }

    private static string[] Rows(InteractiveRebaseViewModel rebase) =>
        [.. rebase.Rows.Select(r => $"{r.Action.ToString().ToLowerInvariant()} {r.Subject}")];

    private string[] Subjects() => _repo.Git("log", "--format=%s", $"{_base}..HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Opens_with_the_commits_oldest_first_and_autosquash_applied()
    {
        using var tab = await OpenAsync();

        Assert.True(tab.Rebase.IsOpen);
        Assert.False(tab.ShowGraph);
        Assert.Equal(["pick one", "fixup fixup! one", "pick two", "pick three"], Rows(tab.Rebase));
        Assert.Equal("4 commits · autosquash : 1 commit fixup! déplacé", tab.Rebase.Details);
        Assert.Equal("4 commits → 3 commits", tab.Rebase.Summary);
        Assert.Equal("2 commits combinés", tab.Rebase.Result[0].Detail);
        Assert.Equal("main", tab.Rebase.BranchName);
    }

    [Fact]
    public async Task Moves_rows_and_checks_the_list()
    {
        using var tab = await OpenAsync();
        var rebase = tab.Rebase;

        rebase.SelectedRow = rebase.Rows[0];
        rebase.MoveSelected(1);
        Assert.Equal(["fixup fixup! one", "pick one", "pick two", "pick three"], Rows(rebase));
        Assert.NotNull(rebase.Error);
        Assert.False(rebase.StartCommand.CanExecute(null));

        rebase.ResetCommand.Execute(null);
        Assert.Null(rebase.Error);
        rebase.SelectedRow = rebase.Rows[3];
        rebase.SetAction(RebaseAction.Drop);
        Assert.Equal("4 commits → 2 commits · 1 supprimé", rebase.Summary);
        Assert.True(rebase.Rows[3].IsDropped);
    }

    [Fact]
    public async Task Starts_the_rebase_and_closes()
    {
        using var tab = await OpenAsync();
        var rebase = tab.Rebase;
        rebase.Rows[2].Action = RebaseAction.Reword;
        rebase.Rows[2].Subject = "two, reworded";
        rebase.Rows[3].Action = RebaseAction.Drop;

        await rebase.StartCommand.ExecuteAsync(null);

        Assert.False(rebase.IsOpen);
        Assert.True(tab.ShowGraph);
        Assert.Equal(["two, reworded", "one"], Subjects());
        Assert.Equal("1 fixed\n", File.ReadAllText(Path.Combine(_repo.Path, "one.txt")).ReplaceLineEndings("\n"));
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Rebase interactif terminé", _));
        Assert.Equal("Rebase interactif de main", tab.Undo.UndoLabel);
    }

    [Fact]
    public async Task An_edit_step_stops_and_lets_the_commit_form_amend()
    {
        using var tab = await OpenAsync();
        tab.Rebase.Rows[2].Action = RebaseAction.Edit;

        await tab.Rebase.StartCommand.ExecuteAsync(null);

        Assert.False(tab.Rebase.IsOpen);
        Assert.True(tab.Merge.Operation?.IsEditStop);
        Assert.StartsWith("Arrêt pour modification", tab.Merge.OperationDetail, StringComparison.Ordinal);
        tab.WorkingTree.CommitForm.Summary = "two, amended";
        tab.WorkingTree.CommitForm.IsAmend = true;
        Assert.True(tab.WorkingTree.CommitForm.CanCommit);

        await tab.Merge.ContinueCommand.ExecuteAsync(null);
        Assert.Null(tab.Merge.Operation);
        Assert.Equal(["three", "two", "one"], Subjects());
    }

    [Fact]
    public async Task Refuses_a_range_with_a_merge_commit()
    {
        _repo.Branch("side");
        _repo.Commit("side", ("side.txt", "s\n"));
        _repo.Checkout("main");
        _repo.Git("merge", "--no-ff", "--no-edit", "--quiet", "side");

        using var tab = await OpenAsync();

        Assert.False(tab.Rebase.IsOpen);
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Warning, "Rebase interactif impossible", _));
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
