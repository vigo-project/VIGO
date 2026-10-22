using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>Conflict prevention of the branch button (J7.11): states, local changes, cache, actions.</summary>
public sealed class ConflictPreventionViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _remote = TestRepoBuilder.Create(bare: true);
    private readonly TestRepoBuilder _seed = TestRepoBuilder.Create();
    private readonly TestRepoBuilder _mine;

    /// <summary>The remote has main (a.txt, b.txt); mine is a clone on branch "feature".</summary>
    public ConflictPreventionViewModelTests()
    {
        _seed.Commit("init", ("a.txt", "1\n2\n3\n"), ("b.txt", "b\n"));
        _seed.Git("remote", "add", "origin", _remote.Path);
        _seed.Git("push", "--quiet", "origin", "main");
        _mine = TestRepoBuilder.Clone(_remote.Path, firstTick: 50_000);
        _mine.Branch("feature");
    }

    public void Dispose()
    {
        _mine.Dispose();
        _seed.Dispose();
        _remote.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RemoteAdvances(params (string Path, string Content)[] files)
    {
        _seed.Commit("on main", files);
        _seed.Git("push", "--quiet", "origin", "main");
        _mine.Git("fetch", "--quiet");
    }

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var info = new RepositoryInfo(_mine.Path, Path.Combine(_mine.Path, ".git"), "repo", "feature", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null);
        await tab.LoadAsync();
        return tab;
    }

    private static async Task WaitForAsync(ConflictPreventionViewModel prevention, ConflictState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (prevention.State != state || prevention.IsChecking)
        {
            Assert.True(DateTime.UtcNow < deadline, $"state stayed {prevention.State}, expected {state}");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task Two_edits_of_the_same_lines_make_a_certain_conflict_with_the_remote_default_branch()
    {
        _mine.Commit("mine", ("a.txt", "1\nmine\n3\n"));
        RemoteAdvances(("a.txt", "1\ntheirs\n3\n"));
        using var tab = await OpenAsync();
        var prevention = tab.ConflictPrevention;

        await WaitForAsync(prevention, ConflictState.Conflict);

        Assert.Equal("origin/main", prevention.Target);
        Assert.Equal(["a.txt"], prevention.ConflictingFiles);
        Assert.True(prevention.HasIssue);
        Assert.Equal("Conflit avec origin/main : 1 fichier", prevention.Summary);
    }

    [Fact]
    public async Task A_branch_that_merges_cleanly_is_up_to_date()
    {
        _mine.Commit("mine", ("c.txt", "c\n"));
        RemoteAdvances(("d.txt", "d\n"));
        using var tab = await OpenAsync();

        await WaitForAsync(tab.ConflictPrevention, ConflictState.UpToDate);

        Assert.False(tab.ConflictPrevention.HasIssue);
        Assert.Empty(tab.ConflictPrevention.PossibleFiles);
        Assert.Equal("Aucun conflit avec origin/main", tab.ConflictPrevention.Summary);
    }

    [Fact]
    public async Task Uncommitted_changes_on_a_file_the_target_changed_are_a_possible_conflict_without_asking_git_again()
    {
        RemoteAdvances(("a.txt", "1\n2\ntheirs\n"), ("other.txt", "o\n"));
        _mine.WriteFile("a.txt", "mine\n2\n3\n");
        using var tab = await OpenAsync();
        var prevention = tab.ConflictPrevention;

        await WaitForAsync(prevention, ConflictState.PossibleConflict);

        Assert.Equal(["a.txt"], prevention.PossibleFiles);
        Assert.Equal("Conflit possible avec origin/main : 1 fichier", prevention.Summary);
        Assert.Equal(1, prevention.DryRuns);

        // The change goes away: only the intersection is redone, the dry run is not.
        _mine.Git("checkout", "--", "a.txt");
        await tab.WorkingTree.RefreshAsync();
        await WaitForAsync(prevention, ConflictState.UpToDate);
        Assert.Empty(prevention.PossibleFiles);
        Assert.Equal(1, prevention.DryRuns);

        // Another file changed locally, one the target did not touch: still nothing.
        _mine.WriteFile("b.txt", "local\n");
        await tab.WorkingTree.RefreshAsync();
        await WaitForAsync(prevention, ConflictState.UpToDate);
        Assert.Equal(1, prevention.DryRuns);
    }

    [Fact]
    public async Task A_fetch_that_brings_a_conflict_is_noticed_by_the_next_check_and_the_pair_of_commits_is_cached()
    {
        _mine.Commit("mine", ("a.txt", "1\nmine\n3\n"));
        using var tab = await OpenAsync();
        var prevention = tab.ConflictPrevention;
        await WaitForAsync(prevention, ConflictState.UpToDate);
        Assert.Equal(1, prevention.DryRuns);

        await prevention.RefreshAsync();
        Assert.Equal(1, prevention.DryRuns);

        RemoteAdvances(("a.txt", "1\ntheirs\n3\n"));
        await prevention.RefreshAsync();
        await WaitForAsync(prevention, ConflictState.Conflict);
        Assert.Equal(2, prevention.DryRuns);
    }

    [Fact]
    public async Task Nothing_is_checked_on_the_target_branch_itself_on_a_detached_head_or_without_a_target()
    {
        _mine.Checkout("main");
        using var tab = await OpenAsync();
        await tab.ConflictPrevention.RefreshAsync();
        Assert.Equal(ConflictState.None, tab.ConflictPrevention.State);
        Assert.Equal(string.Empty, tab.ConflictPrevention.Summary);

        _mine.Git("checkout", "--quiet", "--detach");
        await tab.WorkingTree.RefreshAsync();
        await tab.ConflictPrevention.RefreshAsync();
        Assert.Equal(ConflictState.None, tab.ConflictPrevention.State);

        _mine.Checkout("feature");
        _mine.Git("remote", "remove", "origin");
        await tab.WorkingTree.RefreshAsync();
        await tab.ConflictPrevention.RefreshAsync();
        Assert.Equal(ConflictState.None, tab.ConflictPrevention.State);
    }

    [Fact]
    public async Task The_files_of_the_list_open_their_history_and_the_actions_use_the_merge_and_rebase_of_the_branch_menu()
    {
        _mine.Commit("mine", ("c.txt", "c\n"));
        RemoteAdvances(("d.txt", "d\n"));
        using var tab = await OpenAsync();
        var prevention = tab.ConflictPrevention;
        await WaitForAsync(prevention, ConflictState.UpToDate);
        FileInspection? asked = null;
        prevention.InspectionRequested += (_, request) => asked = request;

        prevention.ShowHistoryCommand.Execute("a.txt");
        Assert.Equal(new FileInspection(FileInspectionKind.History, "a.txt"), asked);

        Assert.True(prevention.CanIntegrate);
        Assert.Equal("Fusionner origin/main dans cette branche", prevention.MergeText);
        await prevention.MergeCommand.ExecuteAsync(null);
        Assert.Equal(2, _mine.Git("log", "-1", "--format=%P").Trim().Split(' ').Length);
        Assert.Equal("origin/main", prevention.Target);
    }
}
