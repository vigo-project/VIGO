using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class StagingViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeConfirmations _confirmations = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RepositoryTabViewModel CreateTab(TestRepoBuilder repo) => new(
        new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false),
        _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, _confirmations);

    private static string Read(TestRepoBuilder repo, string path) => File.ReadAllText(Path.Combine(repo.Path, path));

    [Fact]
    public async Task Row_toggle_stages_and_unstages_files()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").WriteFile("b.txt", "b\n");
        using var tab = CreateTab(repo);
        var tree = tab.WorkingTree;
        await tree.StartAsync();

        await tree.ToggleStageCommand.ExecuteAsync(tree.Unstaged.Single(f => f.Name == "b.txt"));
        Assert.Equal(["b.txt"], tree.Staged.Select(f => f.Name));
        Assert.Equal(["a.txt"], tree.Unstaged.Select(f => f.Name));

        await tree.StageAllCommand.ExecuteAsync(null);
        Assert.Empty(tree.Unstaged);
        Assert.Equal(2, tree.Staged.Count);

        await tree.ToggleStageCommand.ExecuteAsync(tree.Staged.Single(f => f.Name == "a.txt"));
        Assert.Equal(["a.txt"], tree.Unstaged.Select(f => f.Name));

        await tree.UnstageAllCommand.ExecuteAsync(null);
        Assert.Empty(tree.Staged);
        Assert.Empty(_notifier.Errors);
    }

    [Fact]
    public async Task Row_toggle_acts_on_the_whole_selection_with_ctrl_and_shift()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("seed.txt", "s\n"));
        repo.WriteFile("a.txt", "a\n").WriteFile("b.txt", "b\n").WriteFile("c.txt", "c\n").WriteFile("d.txt", "d\n");
        using var tab = CreateTab(repo);
        var tree = tab.WorkingTree;
        await tree.StartAsync();

        tree.Select(tree.Unstaged.Single(f => f.Name == "a.txt"), FileSelectionMode.Replace);
        tree.Select(tree.Unstaged.Single(f => f.Name == "c.txt"), FileSelectionMode.Toggle);
        Assert.Equal(["a.txt", "c.txt"], tree.Unstaged.Where(f => f.IsSelected).Select(f => f.Name));

        await tree.ToggleStageCommand.ExecuteAsync(tree.Unstaged.Single(f => f.Name == "c.txt"));
        Assert.Equal(["a.txt", "c.txt"], tree.Staged.Select(f => f.Name));
        Assert.Equal(["b.txt", "d.txt"], tree.Unstaged.Select(f => f.Name));
        Assert.DoesNotContain(tree.Staged, f => f.IsSelected);

        tree.Select(tree.Staged.Single(f => f.Name == "a.txt"), FileSelectionMode.Replace);
        tree.Select(tree.Staged.Single(f => f.Name == "c.txt"), FileSelectionMode.Range);
        Assert.Equal(2, tree.SelectionSizeOf(tree.Staged.Single(f => f.Name == "a.txt")));
        await tree.ToggleStageCommand.ExecuteAsync(tree.Staged.Single(f => f.Name == "a.txt"));
        Assert.Empty(tree.Staged);

        // A row outside the selection acts alone.
        tree.Select(tree.Unstaged.Single(f => f.Name == "a.txt"), FileSelectionMode.Replace);
        await tree.ToggleStageCommand.ExecuteAsync(tree.Unstaged.Single(f => f.Name == "d.txt"));
        Assert.Equal(["d.txt"], tree.Staged.Select(f => f.Name));
        Assert.Empty(_notifier.Errors);
    }

    [Fact]
    public async Task Folder_button_stages_and_unstages_every_file_below()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("seed.txt", "s\n"));
        repo.WriteFile("src/a/one.txt", "1\n").WriteFile("src/a/two.txt", "2\n").WriteFile("src/three.txt", "3\n").WriteFile("other.txt", "o\n");
        using var tab = CreateTab(repo);
        var tree = tab.WorkingTree;
        tree.ShowAsTree = true;
        await tree.StartAsync();

        var folder = tree.UnstagedRows.OfType<FolderRow>().Single(f => f.Path == "src");
        await tree.ToggleFolderStageCommand.ExecuteAsync(folder);
        Assert.Equal(["src/a/one.txt", "src/a/two.txt", "src/three.txt"], tree.Staged.Select(f => f.Path));
        Assert.Equal(["other.txt"], tree.Unstaged.Select(f => f.Path));

        await tree.ToggleFolderStageCommand.ExecuteAsync(tree.StagedRows.OfType<FolderRow>().Single(f => f.Path == "src/a"));
        Assert.Equal(["src/three.txt"], tree.Staged.Select(f => f.Path));

        // Ctrl + click on a folder selects the files below it.
        tree.Select(tree.UnstagedRows.OfType<FolderRow>().Single(f => f.Path == "src/a"), FileSelectionMode.Replace);
        Assert.Equal(["src/a/one.txt", "src/a/two.txt"], tree.Unstaged.Where(f => f.IsSelected).Select(f => f.Path));
        Assert.Empty(_notifier.Errors);
    }

    [Fact]
    public async Task Folder_menu_discards_ignores_and_stashes_what_is_below_the_folder()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("src/a.txt", "a\n"), ("keep/k.txt", "k\n"));
        repo.WriteFile("src/a.txt", "changed\n").WriteFile("src/new.txt", "n\n").WriteFile("keep/k.txt", "kk\n").WriteFile("audit/log.txt", "l\n");
        using var tab = CreateTab(repo);
        var tree = tab.WorkingTree;
        tree.ShowAsTree = true;
        await tree.StartAsync();
        FolderRow Folder(string path) => tree.UnstagedRows.OfType<FolderRow>().Single(f => f.Path == path);

        // Ignore: the untracked folder goes to .gitignore and leaves the status.
        await tab.Folders.IgnoreFolderCommand.ExecuteAsync(Folder("audit"));
        await tree.RefreshAsync();
        Assert.Contains("audit/", Read(repo, ".gitignore").Split('\n').Select(l => l.Trim()));
        Assert.DoesNotContain(tree.Unstaged, f => f.Path.StartsWith("audit/", StringComparison.Ordinal));

        // Stash: only the changes under the folder, untracked files included.
        await tab.Stash.StashFolderCommand.ExecuteAsync(Folder("keep"));
        Assert.Equal("k\n", Read(repo, "keep/k.txt"));
        Assert.Equal("changed\n", Read(repo, "src/a.txt"));

        // Discard: asks first, then restores the tracked file and deletes the untracked one.
        _confirmations.Answer = false;
        await tab.Folders.DiscardFolderCommand.ExecuteAsync(Folder("src"));
        Assert.Equal("changed\n", Read(repo, "src/a.txt"));
        _confirmations.Answer = true;
        await tab.Folders.DiscardFolderCommand.ExecuteAsync(Folder("src"));
        Assert.Equal("a\n", Read(repo, "src/a.txt"));
        Assert.False(File.Exists(Path.Combine(repo.Path, "src", "new.txt")));
        Assert.Empty(_notifier.Errors);
    }

    [Fact]
    public async Task Discard_asks_first_and_does_nothing_when_refused()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "changed\n").WriteFile("new.txt", "n\n");
        using var tab = CreateTab(repo);
        var tree = tab.WorkingTree;
        await tree.StartAsync();

        _confirmations.Answer = false;
        await tree.DiscardCommand.ExecuteAsync(tree.Unstaged.Single(f => f.Name == "a.txt"));
        Assert.Equal("changed\n", Read(repo, "a.txt"));
        Assert.Equal(1, _confirmations.Asked);

        _confirmations.Answer = true;
        await tree.DiscardCommand.ExecuteAsync(tree.Unstaged.Single(f => f.Name == "a.txt"));
        await tree.DiscardCommand.ExecuteAsync(tree.Unstaged.Single(f => f.Name == "new.txt"));

        Assert.Equal("a\n", Read(repo, "a.txt"));
        Assert.False(File.Exists(Path.Combine(repo.Path, "new.txt")));
        Assert.Equal(0, tree.ChangeCount);
    }

    [Fact]
    public async Task Discard_acts_on_the_whole_selection_but_a_row_outside_it_acts_alone()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"), ("b.txt", "b\n"), ("c.txt", "c\n"), ("d.txt", "d\n"));
        repo.WriteFile("a.txt", "a2\n").WriteFile("b.txt", "b2\n").WriteFile("c.txt", "c2\n").WriteFile("d.txt", "d2\n");
        using var tab = CreateTab(repo);
        var tree = tab.WorkingTree;
        await tree.StartAsync();
        _confirmations.Answer = true;

        tree.Select(tree.Unstaged.Single(f => f.Name == "a.txt"), FileSelectionMode.Replace);
        tree.Select(tree.Unstaged.Single(f => f.Name == "b.txt"), FileSelectionMode.Toggle);
        await tree.DiscardCommand.ExecuteAsync(tree.Unstaged.Single(f => f.Name == "b.txt"));
        Assert.Equal("a\n", Read(repo, "a.txt"));
        Assert.Equal("b\n", Read(repo, "b.txt"));
        Assert.Equal("c2\n", Read(repo, "c.txt"));

        tree.Select(tree.Unstaged.Single(f => f.Name == "c.txt"), FileSelectionMode.Replace);
        await tree.DiscardCommand.ExecuteAsync(tree.Unstaged.Single(f => f.Name == "d.txt"));
        Assert.Equal("c2\n", Read(repo, "c.txt"));
        Assert.Equal("d\n", Read(repo, "d.txt"));
    }

    [Fact]
    public async Task Discard_all_resets_staged_unstaged_and_untracked_files()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"), ("b.txt", "b\n"));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt").WriteFile("a.txt", "a3\n");
        repo.WriteFile("added.txt", "x\n").Stage("added.txt");
        repo.DeleteFile("b.txt");
        repo.WriteFile("untracked/u.txt", "u\n");
        using var tab = CreateTab(repo);
        var tree = tab.WorkingTree;
        await tree.StartAsync();
        Assert.True(tree.CanDiscardAll);
        _confirmations.Answer = true;

        await tree.DiscardAllCommand.ExecuteAsync(null);

        Assert.Equal(0, tree.ChangeCount);
        Assert.Equal("a\n", Read(repo, "a.txt"));
        Assert.True(File.Exists(Path.Combine(repo.Path, "b.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "added.txt")));
        Assert.False(Directory.Exists(Path.Combine(repo.Path, "untracked")));
        Assert.False(tree.CanDiscardAll);
    }

    [Fact]
    public async Task Selected_lines_are_staged_from_the_diff_then_the_diff_follows()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n2\n3\n4\n"));
        repo.WriteFile("a.txt", "1\nTWO\n3\nFOUR\n");
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();
        var diff = tab.Diff;
        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Unstaged.Single());
        await WaitUntilAsync(() => diff is { IsOpen: true, IsLoading: false, CanEditLines: true });

        // "-2" then Shift+click on "+TWO": both selected.
        var lines = diff.Diff!.Hunks[0].Lines;
        var deleted = new DiffLineRef(0, IndexOf(lines, DiffLineKind.Deleted, "2"));
        var added = new DiffLineRef(0, IndexOf(lines, DiffLineKind.Added, "TWO"));
        diff.ToggleLine(deleted, extend: false);
        diff.ToggleLine(added, extend: true);
        Assert.Equal(2, diff.SelectionCount);
        Assert.Equal("Indexer 2 lignes", diff.SelectionActionLabel);

        await diff.ApplySelectionCommand.ExecuteAsync(null);

        Assert.Equal("1\nTWO\n3\n4\n", repo.Git("show", ":a.txt"));
        Assert.Equal(0, diff.SelectionCount);
        // The unstaged diff was reread: only the second change is left.
        await WaitUntilAsync(() => diff.Diff is { } d && d.Additions == 1 && d.Hunks[0].Lines.Any(l => l.Text == "FOUR"));
        Assert.True(tab.WorkingTree.Unstaged.Single().ShowPartiallyStaged);
    }

    [Fact]
    public async Task Staged_hunk_is_unstaged_and_unstaged_hunk_discarded()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n2\n3\n"));
        repo.WriteFile("a.txt", "1\nTWO\n3\n").Stage("a.txt");
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();
        var diff = tab.Diff;

        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Staged.Single());
        await WaitUntilAsync(() => diff is { IsOpen: true, IsLoading: false, CanEditLines: true });
        Assert.Equal("Désindexer le hunk", diff.HunkActionLabel);
        Assert.False(diff.CanDiscardLines);
        await diff.ApplyHunkCommand.ExecuteAsync(0);
        Assert.Equal("1\n2\n3\n", repo.Git("show", ":a.txt"));
        Assert.False(diff.IsOpen); // nothing staged any more: the staged diff closed

        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Unstaged.Single());
        await WaitUntilAsync(() => diff is { IsOpen: true, IsLoading: false, CanDiscardLines: true });
        _confirmations.Answer = true;
        await diff.DiscardHunkCommand.ExecuteAsync(0);

        Assert.Equal("1\n2\n3\n", Read(repo, "a.txt"));
        Assert.Equal(0, tab.WorkingTree.ChangeCount);
    }

    [Fact]
    public async Task Hunk_button_stages_the_whole_hunk_without_any_selected_line()
    {
        using var repo = TestRepoBuilder.Create();
        var lines = Enumerable.Range(1, 30).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        repo.Commit("init", ("a.txt", string.Join("\n", lines) + "\n"));
        var edited = (string[])lines.Clone();
        edited[1] = "TWO";
        edited[27] = "TWENTY-EIGHT";
        repo.WriteFile("a.txt", string.Join("\n", edited) + "\n");
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();
        var diff = tab.Diff;

        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Unstaged.Single());
        await WaitUntilAsync(() => diff is { IsOpen: true, IsLoading: false, CanEditLines: true } && diff.Diff?.Hunks.Count == 2);
        Assert.False(diff.HasSelection);
        await diff.ApplyHunkCommand.ExecuteAsync(1);

        var staged = repo.Git("show", ":a.txt");
        Assert.Contains("TWENTY-EIGHT", staged);
        Assert.DoesNotContain("TWO", staged);
    }

    [Fact]
    public async Task Lines_of_untracked_files_cannot_be_selected()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("new.txt", "n\n");
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();

        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Unstaged.Single());
        await WaitUntilAsync(() => tab.Diff is { IsOpen: true, IsLoading: false });
        tab.Diff.ToggleLine(new DiffLineRef(0, 0), extend: false);

        Assert.False(tab.Diff.CanEditLines);
        Assert.Equal(0, tab.Diff.SelectionCount);
    }

    [Fact]
    public async Task Lines_of_a_file_that_is_not_utf8_cannot_be_edited()
    {
        using var repo = TestRepoBuilder.Create();
        File.WriteAllBytes(Path.Combine(repo.Path, "a.txt"), [(byte)'c', 0xE9, (byte)'\n', (byte)'x', (byte)'\n']);
        repo.Git("add", "a.txt");
        repo.Git("commit", "-m", "init");
        File.WriteAllBytes(Path.Combine(repo.Path, "a.txt"), [(byte)'c', 0xE9, (byte)'\n', (byte)'y', (byte)'\n']);
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();

        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Unstaged.Single());
        await WaitUntilAsync(() => tab.Diff is { IsOpen: true, IsLoading: false });

        Assert.False(tab.Diff.CanEditLines);
        Assert.False(tab.Diff.CanDiscardLines);
    }

    [Fact]
    public async Task Failure_is_reported_as_a_toast()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "b\n");
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();
        var item = tab.WorkingTree.Unstaged.Single();
        File.WriteAllText(Path.Combine(repo.Path, ".git", "index.lock"), ""); // another git command is "running"

        await tab.WorkingTree.ToggleStageCommand.ExecuteAsync(item);

        var error = Assert.Single(_notifier.Errors);
        Assert.Equal("Impossible d'indexer le fichier", error.Title);
        Assert.False(tab.WorkingTree.Actions.IsBusy);
    }

    private static int IndexOf(IReadOnlyList<DiffLine> lines, DiffLineKind kind, string text) =>
        lines.Select((l, i) => (l, i)).Single(x => x.l.Kind == kind && x.l.Text == text).i;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(20, Ct);
        }
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
        public List<(string Title, string? Message)> Errors { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null)
        {
            if (severity == NotificationSeverity.Error) Errors.Add((title, message));
        }
    }
}
