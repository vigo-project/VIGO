using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class MergeActionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly TestRepoBuilder _repo;
    private readonly string _mainTip;
    private readonly string _featureTip;

    /// <summary>main: base → main-2 (a.txt); feature (from base): feature-1 (b.txt, or a.txt for a conflict).</summary>
    public MergeActionsViewModelTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Git("config", "merge.ff", "true");
        _repo.Commit("base", ("a.txt", "base\n"));
        _repo.Branch("feature");
        _featureTip = _repo.Commit("feature-1", ("b.txt", "feature\n"));
        _repo.Checkout("main");
        _mainTip = _repo.Commit("main-2", ("a.txt", "main\n"));
    }

    public void Dispose() => _repo.Dispose();

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier);
        await tab.LoadAsync();
        return tab;
    }

    private static GitRef Ref(RepositoryTabViewModel tab, string name) => tab.Graph.Refs!.Refs.Single(r => r.Name == name);

    /// <summary>feature changes a.txt too: merging it into main, or rebasing it on main, conflicts.</summary>
    private void MakeFeatureConflict()
    {
        _repo.Checkout("feature");
        _repo.Commit("feature-2", ("a.txt", "feature\n"));
        _repo.Checkout("main");
    }

    private async Task ResolveAsync(RepositoryTabViewModel tab, string path)
    {
        _repo.WriteFile(path, "resolved\n");
        Assert.True((await _git.StageAsync(_repo.Path, [path], TestContext.Current.CancellationToken)).Value is Results.Done);
        await tab.WorkingTree.RefreshAsync();
    }

    [Fact]
    public async Task Branch_menus_merge_into_and_rebase_the_checked_out_branch()
    {
        using var tab = await OpenAsync();

        var menu = tab.Merge.HeadMenuFor(Ref(tab, "feature"));

        Assert.Equal("Fusionner feature dans main", menu?.Merge.Label);
        Assert.Equal("Rebaser main sur feature", menu?.Rebase.Label);
        Assert.Null(tab.Merge.HeadMenuFor(Ref(tab, "main")));
        var row = tab.Refs.Rows.Select(i => i.Row).Single(r => r.Label == "feature");
        Assert.Equal("Fusionner feature dans main", tab.Refs.MenuFor(row)?.Merge?.Label);
    }

    [Fact]
    public async Task The_drop_menu_says_how_the_merge_is_carried_out()
    {
        _repo.Git("branch", "behind", "HEAD~1");
        using var tab = await OpenAsync();

        var intoHead = await tab.Merge.DropMenuAsync(Ref(tab, "feature"), Ref(tab, "main"));
        var forward = await tab.Merge.DropMenuAsync(Ref(tab, "main"), Ref(tab, "behind"));
        var checkout = await tab.Merge.DropMenuAsync(Ref(tab, "main"), Ref(tab, "feature"));
        var upToDate = await tab.Merge.DropMenuAsync(Ref(tab, "behind"), Ref(tab, "main"));

        Assert.Equal("feature → main", intoHead?.Title);
        Assert.Equal((MergeMode.IntoHead, "Fusionner feature dans main"), (intoHead?.Merge?.Mode, intoHead?.Merge?.Label));
        Assert.Equal("Rebaser feature sur main", intoHead?.Rebase?.Label);
        Assert.Equal((MergeMode.FastForwardBranch, "Avancer behind jusqu'à main"), (forward?.Merge?.Mode, forward?.Merge?.Label));
        Assert.Equal((MergeMode.CheckoutThenMerge, "Basculer sur feature puis fusionner main"), (checkout?.Merge?.Mode, checkout?.Merge?.Label));
        Assert.Equal(MergeMode.UpToDate, upToDate?.Merge?.Mode);
        Assert.Null(await tab.Merge.DropMenuAsync(Ref(tab, "main"), Ref(tab, "main")));
    }

    [Fact]
    public async Task Merging_into_the_checked_out_branch_creates_the_merge_commit()
    {
        using var tab = await OpenAsync();

        await tab.Merge.MergeCommand.ExecuteAsync(tab.Merge.HeadMenuFor(Ref(tab, "feature"))!.Value.Merge);

        Assert.Equal($"{_mainTip} {_featureTip}", _repo.Git("log", "-1", "--format=%P").Trim());
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Fusion effectuée", "feature fusionnée dans main."));
        Assert.Equal(GitSha.Parse(_repo.Head()), tab.Graph.Head);
    }

    [Fact]
    public async Task A_branch_behind_moves_forward_without_checkout()
    {
        _repo.Git("branch", "behind", "HEAD~1");
        using var tab = await OpenAsync();
        var menu = await tab.Merge.DropMenuAsync(Ref(tab, "main"), Ref(tab, "behind"));

        await tab.Merge.MergeCommand.ExecuteAsync(menu!.Merge);

        Assert.Equal(_mainTip, _repo.Git("rev-parse", "behind").Trim());
        Assert.Equal("main", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal(GitSha.Parse(_mainTip), Ref(tab, "behind").Target);
    }

    [Fact]
    public async Task A_diverged_target_is_checked_out_then_merged()
    {
        using var tab = await OpenAsync();
        var menu = await tab.Merge.DropMenuAsync(Ref(tab, "main"), Ref(tab, "feature"));

        await tab.Merge.MergeCommand.ExecuteAsync(menu!.Merge);

        Assert.Equal("feature", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal($"{_featureTip} {_mainTip}", _repo.Git("log", "-1", "--format=%P").Trim());
    }

    [Fact]
    public async Task A_conflicting_merge_shows_the_banner_and_is_concluded_by_the_commit_form()
    {
        MakeFeatureConflict();
        using var tab = await OpenAsync();

        await tab.Merge.MergeCommand.ExecuteAsync(tab.Merge.HeadMenuFor(Ref(tab, "feature"))!.Value.Merge);

        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning && n.Title == "Conflit détecté");
        Assert.True(tab.Merge.HasOperation);
        Assert.False(tab.Merge.IsRebase);
        Assert.Equal("Fusion de feature dans main.", tab.Merge.OperationTitle);
        Assert.StartsWith("1 fichier en conflit.", tab.Merge.OperationDetail, StringComparison.Ordinal);
        Assert.Equal("Abandonner la fusion", tab.Merge.AbortLabel);
        Assert.Equal("Fusion de feature en cours · 1 conflit", tab.Graph.WorkInProgressText);
        Assert.True(tab.Graph.IsWorkInProgressWarning);
        Assert.False(tab.Merge.CanStart);
        Assert.Null(tab.Merge.HeadMenuFor(Ref(tab, "feature")));
        var form = tab.WorkingTree.CommitForm;
        Assert.Equal("Merge branch 'feature'", form.Summary);
        Assert.False(form.CanCommit);

        await ResolveAsync(tab, "a.txt");
        Assert.Equal("Conflits résolus : validez le commit pour terminer.", tab.Merge.OperationDetail);
        Assert.True(form.CanCommit);
        Assert.Equal("Valider la fusion sur main", form.CommitLabel);

        await form.CommitCommand.ExecuteAsync(null);

        Assert.False(tab.Merge.HasOperation);
        Assert.Equal(2, _repo.Git("log", "-1", "--format=%P").Trim().Split(' ').Length);
        Assert.Equal("Merge branch 'feature'", _repo.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Aborting_a_merge_goes_back_and_clears_the_prepared_message()
    {
        MakeFeatureConflict();
        using var tab = await OpenAsync();
        await tab.Merge.MergeCommand.ExecuteAsync(tab.Merge.HeadMenuFor(Ref(tab, "feature"))!.Value.Merge);

        await tab.Merge.AbortCommand.ExecuteAsync(null);

        Assert.False(tab.Merge.HasOperation);
        Assert.Equal(_mainTip, _repo.Head());
        Assert.Equal(string.Empty, tab.WorkingTree.CommitForm.Summary);
        Assert.Contains(_notifier.Items, n => n.Title == "Fusion abandonnée");
        Assert.False(tab.Graph.IsWorkInProgressWarning);
    }

    [Fact]
    public async Task A_stopped_rebase_continues_once_resolved()
    {
        MakeFeatureConflict();
        using var tab = await OpenAsync();
        var menu = await tab.Merge.DropMenuAsync(Ref(tab, "feature"), Ref(tab, "main"));

        await tab.Merge.RebaseCommand.ExecuteAsync(menu!.Rebase);

        Assert.True(tab.Merge.IsRebase);
        Assert.Equal("Rebase de feature sur main · commit 2 sur 2.", tab.Merge.OperationTitle);
        Assert.Equal("Rebase en cours", tab.WorkingTree.CommitForm.CommitLabel);
        Assert.False(tab.WorkingTree.CommitForm.CanCommit);

        await ResolveAsync(tab, "a.txt");
        Assert.Equal("Conflits résolus : continuez le rebase.", tab.Merge.OperationDetail);
        await tab.Merge.ContinueCommand.ExecuteAsync(null);

        Assert.False(tab.Merge.HasOperation);
        Assert.Equal("feature", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal(_mainTip, _repo.Git("rev-parse", "HEAD~2").Trim());
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Rebase terminé", _));
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
