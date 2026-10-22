using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class WorkingTreeViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly FakeWatcherFactory _watchers = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RepositoryInfo Info(TestRepoBuilder repo) =>
        new(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false);

    private WorkingTreeViewModel CreateSut(TestRepoBuilder repo) => new(_git, Info(repo), _watchers, NullLogger.Instance);

    private RepositoryTabViewModel CreateTab(TestRepoBuilder repo) =>
        new(Info(repo), _git, new FakeTimeProvider(Now), NullLogger.Instance, _watchers);

    /// <summary>Raises the watcher and waits for the refreshed status to be applied.</summary>
    private async Task ChangeAsync(WorkingTreeViewModel sut)
    {
        var applied = new TaskCompletionSource();
        void OnChanged(object? s, EventArgs e) => applied.TrySetResult();
        sut.StatusChanged += OnChanged;
        _watchers.Last!.Raise();
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sut.StatusChanged -= OnChanged;
    }

    [Fact]
    public async Task Start_reads_the_status_into_conflicted_unstaged_and_staged_lists()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"), ("b.txt", "b\n"));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt").WriteFile("a.txt", "a3\n");
        repo.WriteFile("b.txt", "b2\n").WriteFile("docs/new.md", "n\n");
        using var sut = CreateSut(repo);

        await sut.StartAsync();

        Assert.Equal(["a.txt", "b.txt", "new.md"], sut.Unstaged.Select(f => f.Name));
        Assert.Equal(["a.txt"], sut.Staged.Select(f => f.Name));
        Assert.Empty(sut.Conflicted);
        Assert.Equal(3, sut.ChangeCount);
        Assert.Equal("3 fichiers · sur main", sut.Subtitle);
        Assert.True(sut.Unstaged[0].ShowPartiallyStaged);
        Assert.False(sut.Staged[0].ShowPartiallyStaged);
        Assert.Equal("docs", sut.Unstaged[2].Directory);
        Assert.Equal("A", sut.Unstaged[2].StatusLetter);
        Assert.False(sut.IsStagedEmpty);
        Assert.NotNull(_watchers.Last);
    }

    [Fact]
    public async Task Watcher_change_updates_the_lists_in_place()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"), ("c.txt", "c\n"));
        repo.WriteFile("a.txt", "a2\n").WriteFile("c.txt", "c2\n");
        using var sut = CreateSut(repo);
        await sut.StartAsync();
        var a = sut.Unstaged[0];

        repo.WriteFile("b.txt", "new\n").Stage("c.txt");
        await ChangeAsync(sut);

        Assert.Equal(["a.txt", "b.txt"], sut.Unstaged.Select(f => f.Name));
        Assert.Same(a, sut.Unstaged[0]); // unchanged row kept: no flicker, no lost state
        Assert.Equal(["c.txt"], sut.Staged.Select(f => f.Name));

        repo.Stage("a.txt", "b.txt").Commit("all");
        await ChangeAsync(sut);

        Assert.Empty(sut.Unstaged);
        Assert.Empty(sut.Staged);
        Assert.Equal("Aucune modification · sur main", sut.Subtitle);
    }

    [Fact]
    public async Task Bare_repository_has_no_working_tree()
    {
        using var repo = TestRepoBuilder.Create(bare: true);
        using var sut = new WorkingTreeViewModel(_git, new RepositoryInfo(null, repo.Path, "repo", null, null, true, false, true), _watchers, NullLogger.Instance);

        await sut.StartAsync();

        Assert.False(sut.IsAvailable);
        Assert.Null(sut.Status);
        Assert.Null(_watchers.Last);
    }

    [Fact]
    public async Task Dispose_stops_watching()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        var sut = CreateSut(repo);
        await sut.StartAsync();

        sut.Dispose();

        Assert.True(_watchers.Last!.IsDisposed);
    }

    [Fact]
    public async Task Work_in_progress_row_shifts_the_rows_but_keeps_the_selected_commit()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.txt", "1\n"));
        repo.Commit("second", ("a.txt", "2\n"));
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        var graph = tab.Graph;
        Assert.False(graph.HasWorkInProgress);
        graph.SelectedRow = 1;
        var details = 0;
        tab.Details.PropertyChanged += (_, e) => details += e.PropertyName == nameof(CommitDetailsViewModel.HasSelection) ? 1 : 0;

        repo.WriteFile("a.txt", "3\n");
        await ChangeAsync(tab.WorkingTree);

        Assert.True(graph.HasWorkInProgress);
        Assert.Equal(3, graph.RowCount);
        Assert.Equal("1 modification non validée", graph.WorkInProgressText);
        Assert.True(graph.IsWorkInProgressRow(0));
        Assert.Equal(1, graph.HeadRow);
        Assert.Equal(graph.GetNodeLane(1), graph.GetNodeLane(0));
        Assert.Equal(2, graph.SelectedRow);
        Assert.Equal(first, graph.SelectedSha?.ToString());
        Assert.Equal(0, details); // same commit: not reloaded

        graph.SelectedRow = 0;
        Assert.Null(graph.SelectedSha);

        repo.Stage("a.txt").Commit("third");
        await ChangeAsync(tab.WorkingTree);

        Assert.False(graph.HasWorkInProgress);
        Assert.Equal(-1, graph.SelectedRow); // the selected WIP row disappeared
    }

    [Fact]
    public async Task Staging_the_previewed_file_opens_the_next_unstaged_one()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n"), ("b.txt", "1\n"));
        repo.WriteFile("a.txt", "2\n").WriteFile("b.txt", "2\n");
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Unstaged[0]);
        await WaitUntilAsync(() => tab.Diff is { IsOpen: true, IsLoading: false });

        repo.Stage("a.txt");
        await ChangeAsync(tab.WorkingTree);

        Assert.True(tab.Diff.IsOpen);
        Assert.Equal("b.txt", tab.Diff.WorkingFile?.Entry.Path);
    }

    [Fact]
    public async Task Next_file_follows_the_tree_order_where_folders_come_first()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n"), ("dir/x.txt", "1\n"));
        repo.WriteFile("a.txt", "2\n").WriteFile("dir/x.txt", "2\n");
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        tab.WorkingTree.ShowAsTree = true;
        // Tree: dir/x.txt, then a.txt (list: a.txt, then dir/x.txt).
        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Unstaged.Single(f => f.Path == "dir/x.txt"));
        await WaitUntilAsync(() => tab.Diff is { IsOpen: true, IsLoading: false });

        repo.Stage("dir/x.txt");
        await ChangeAsync(tab.WorkingTree);

        Assert.Equal("a.txt", tab.Diff.WorkingFile?.Entry.Path);
    }

    [Fact]
    public async Task Right_panel_and_diff_follow_the_selection_and_the_working_directory()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n2\n"));
        repo.WriteFile("a.txt", "1\n2\n3\n");
        using var tab = CreateTab(repo);
        await tab.LoadAsync();
        Assert.True(tab.ShowWorkingTree);

        // Opening an unstaged file shows its diff and highlights its row.
        var item = Assert.Single(tab.WorkingTree.Unstaged);
        tab.WorkingTree.OpenFileCommand.Execute(item);
        await WaitUntilAsync(() => tab.Diff is { IsOpen: true, IsLoading: false });
        Assert.Equal("Non indexé", tab.Diff.SideDisplay);
        Assert.Equal("+1", tab.Diff.AdditionsDisplay);
        Assert.True(item.IsOpen);

        // The file changes on disk: the diff is read again.
        repo.WriteFile("a.txt", "1\n2\n3\n4\n");
        await ChangeAsync(tab.WorkingTree);
        await WaitUntilAsync(() => tab.Diff.Diff?.Additions == 2);

        // Fully staged: nothing left on the unstaged side, the diff closes.
        repo.Stage("a.txt");
        await ChangeAsync(tab.WorkingTree);
        Assert.False(tab.Diff.IsOpen);

        // Selecting a commit shows its details instead of the working directory.
        tab.Graph.SelectedRow = 1;
        Assert.False(tab.ShowWorkingTree);
        tab.Details.CloseCommand.Execute(null);
        Assert.True(tab.ShowWorkingTree);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class FakeWatcherFactory : IRepositoryWatcherFactory
    {
        public FakeWatcher? Last { get; private set; }

        public IRepositoryWatcher? Watch(RepositoryInfo repository) =>
            repository.WorkingDirectory is null ? null : Last = new FakeWatcher();
    }

    private sealed class FakeWatcher : IRepositoryWatcher
    {
        public event EventHandler? Changed;

        public event EventHandler? RefsChanged;

        public bool IsDisposed { get; private set; }

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public void RaiseRefs() => RefsChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose() => IsDisposed = true;
    }
}
