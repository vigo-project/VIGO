using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class UndoHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly TestRepoBuilder _repo;
    private readonly GitSha _base;
    private readonly GitSha _mainTip;

    /// <summary>main: base (a.txt) → main-2 (a.txt); feature (from base): feature-1 (a.txt: conflicts with main).</summary>
    public UndoHistoryTests()
    {
        _repo = TestRepoBuilder.Create();
        _base = GitSha.Parse(_repo.Commit("base", ("a.txt", "base\n")));
        _repo.Branch("feature");
        _repo.Commit("feature-1", ("a.txt", "feature\n"));
        _repo.Checkout("main");
        _mainTip = GitSha.Parse(_repo.Commit("main-2", ("a.txt", "main\n")));
    }

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, _confirmations);
        await tab.LoadAsync();
        return tab;
    }

    private async Task CommitAsync(RepositoryTabViewModel tab, string path, string content, string summary)
    {
        _repo.WriteFile(path, content);
        Assert.True((await _git.StageAsync(_repo.Path, [path], Ct)).Value is Done);
        await tab.WorkingTree.RefreshAsync();
        tab.WorkingTree.CommitForm.Summary = summary;
        await tab.WorkingTree.CommitForm.CommitCommand.ExecuteAsync(null);
    }

    private string Status() => _repo.Git("status", "--porcelain").ReplaceLineEndings("\n").TrimEnd();

    [Fact]
    public async Task Nothing_to_undo_at_first()
    {
        using var tab = await OpenAsync();

        Assert.False(tab.Undo.CanUndo);
        Assert.False(tab.Undo.CanRedo);
        Assert.Equal("Rien à annuler (Ctrl+Z)", tab.Undo.UndoTooltip);
        Assert.Equal("Rien à rétablir (Ctrl+Y)", tab.Undo.RedoTooltip);
    }

    [Fact]
    public async Task A_commit_is_undone_with_its_changes_staged_then_redone()
    {
        using var tab = await OpenAsync();
        await CommitAsync(tab, "b.txt", "b\n", "Add b");
        var committed = _repo.Head();

        Assert.True(tab.Undo.CanUndo);
        Assert.Equal("Annuler : Commit « Add b » (Ctrl+Z)", tab.Undo.UndoTooltip);

        await tab.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(_mainTip.ToString(), _repo.Head());
        Assert.Equal("A  b.txt", Status());
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Action annulée", "Commit « Add b »."));
        Assert.False(tab.Undo.CanUndo);
        Assert.Equal("Rétablir : Commit « Add b » (Ctrl+Y)", tab.Undo.RedoTooltip);

        await tab.Undo.RedoCommand.ExecuteAsync(null);

        Assert.Equal(committed, _repo.Head());
        Assert.Equal(string.Empty, Status());
        Assert.True(tab.Undo.CanUndo);
        Assert.False(tab.Undo.CanRedo);
    }

    [Fact]
    public async Task Operations_are_undone_in_reverse_order_and_a_new_one_clears_redo()
    {
        using var tab = await OpenAsync();
        await CommitAsync(tab, "b.txt", "b\n", "Add b");
        await CommitAsync(tab, "c.txt", "c\n", "Add c");

        await tab.Undo.UndoCommand.ExecuteAsync(null);
        Assert.Equal("Annuler : Commit « Add b » (Ctrl+Z)", tab.Undo.UndoTooltip);
        Assert.True(tab.Undo.CanRedo);

        await CommitAsync(tab, "c.txt", "c2\n", "Add c again");

        Assert.False(tab.Undo.CanRedo);
        Assert.Equal("Annuler : Commit « Add c again » (Ctrl+Z)", tab.Undo.UndoTooltip);
    }

    [Fact]
    public async Task A_hard_reset_is_undone()
    {
        _confirmations.Answer = true;
        using var tab = await OpenAsync();
        await tab.History.ResetCommand.ExecuteAsync(new ResetPlan(_base, ResetMode.Hard));
        Assert.Equal(_base.ToString(), _repo.Head());

        await tab.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(_mainTip.ToString(), _repo.Head());
        Assert.Equal("main\n", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, Status());
    }

    [Fact]
    public async Task A_mixed_reset_is_undone_without_leftover_changes()
    {
        using var tab = await OpenAsync();
        await tab.History.ResetCommand.ExecuteAsync(new ResetPlan(_base, ResetMode.Mixed));
        Assert.Equal(" M a.txt", Status());

        await tab.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(_mainTip.ToString(), _repo.Head());
        Assert.Equal(string.Empty, Status());
    }

    [Fact]
    public async Task A_merge_stopped_on_conflicts_and_concluded_is_undone_as_a_whole()
    {
        using var tab = await OpenAsync();
        var feature = tab.Graph.Refs!.OfKind(GitRefKind.LocalBranch).Single(r => r.Name == "feature");
        var main = tab.Graph.Refs!.OfKind(GitRefKind.LocalBranch).Single(r => r.Name == "main");
        await tab.Merge.MergeCommand.ExecuteAsync(new MergePlan(feature, main, MergeMode.IntoHead));
        Assert.True(tab.Merge.HasOperation);
        Assert.False(tab.Undo.CanUndo);

        _repo.WriteFile("a.txt", "resolved\n");
        Assert.True((await _git.StageAsync(_repo.Path, ["a.txt"], Ct)).Value is Done);
        await tab.WorkingTree.RefreshAsync();
        await tab.WorkingTree.CommitForm.CommitCommand.ExecuteAsync(null);
        Assert.False(tab.Merge.HasOperation);

        Assert.Equal("Annuler : Fusion de feature dans main (Ctrl+Z)", tab.Undo.UndoTooltip);
        await tab.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(_mainTip.ToString(), _repo.Head());
        Assert.Equal(string.Empty, Status());
    }

    [Fact]
    public async Task An_aborted_merge_leaves_nothing_to_undo()
    {
        using var tab = await OpenAsync();
        var feature = tab.Graph.Refs!.OfKind(GitRefKind.LocalBranch).Single(r => r.Name == "feature");
        var main = tab.Graph.Refs!.OfKind(GitRefKind.LocalBranch).Single(r => r.Name == "main");
        await tab.Merge.MergeCommand.ExecuteAsync(new MergePlan(feature, main, MergeMode.IntoHead));

        await tab.Merge.AbortCommand.ExecuteAsync(null);

        Assert.False(tab.Undo.CanUndo);
    }

    [Fact]
    public async Task Undo_refuses_when_local_changes_are_in_the_way_and_keeps_the_entry()
    {
        _confirmations.Answer = true;
        using var tab = await OpenAsync();
        await tab.History.ResetCommand.ExecuteAsync(new ResetPlan(_base, ResetMode.Hard));
        // a.txt differs between base and main-2: going back would overwrite this change.
        _repo.WriteFile("a.txt", "local\n");
        await tab.WorkingTree.RefreshAsync();

        await tab.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(_base.ToString(), _repo.Head());
        Assert.Equal("local\n", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Error
            && n.Title == $"Impossible d'annuler : Reset de main sur {_base.ToShortString()} « base »"
            && n.Message!.Contains("stash", StringComparison.Ordinal));
        Assert.True(tab.Undo.CanUndo);
    }

    [Fact]
    public async Task A_change_made_outside_clears_the_history()
    {
        using var tab = await OpenAsync();
        await CommitAsync(tab, "b.txt", "b\n", "Add b");
        _repo.Commit("outside", ("c.txt", "c\n"));
        await tab.WorkingTree.RefreshAsync();

        await tab.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal("outside", _repo.Git("log", "-1", "--format=%s").Trim());
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Error && n.Message!.Contains("l'historique d'annulation est effacé", StringComparison.Ordinal));
        Assert.False(tab.Undo.CanUndo);
        Assert.False(tab.Undo.CanRedo);
    }

    [Fact]
    public void The_plan_only_touches_the_references_of_the_entry()
    {
        var (a, b, c) = (GitSha.Parse(new string('a', 40)), GitSha.Parse(new string('b', 40)), GitSha.Parse(new string('c', 40)));
        static RefSnapshot Snapshot(GitSha head, params (string Name, GitSha Target)[] refs) =>
            new("main", head, refs.ToDictionary(r => r.Name, r => new RefState(r.Target), StringComparer.Ordinal));

        // A tag created; meanwhile main moved (not touched by the entry).
        var entry = new UndoEntry(new UndoStep("Création du tag v1"), Snapshot(a, ("refs/heads/main", a)),
            Snapshot(a, ("refs/heads/main", a), ("refs/tags/v1", b)));
        var now = Snapshot(c, ("refs/heads/main", c), ("refs/tags/v1", b));

        var plan = UndoHistory.Plan(entry, undo: true, now);

        Assert.NotNull(plan);
        Assert.Equal(("main", (GitSha?)c), (plan.HeadBranch, plan.Head));
        Assert.Equal(["refs/heads/main"], plan.Refs.Keys);
        Assert.Null(UndoHistory.Plan(entry, undo: true, Snapshot(c, ("refs/heads/main", c), ("refs/tags/v1", c))));
    }

    private sealed class FakeConfirmations : IConfirmationService
    {
        public bool Answer { get; set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer);

        public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfirmationOutcome(Answer, false));
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
