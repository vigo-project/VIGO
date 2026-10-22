using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>Diff view model fed to the code editor: document lines, side-by-side layout (ADR-038).</summary>
public sealed class DiffLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly InMemoryPreferencesStore _store = new();

    private RepositoryTabViewModel CreateTab(TestRepoBuilder repo, Preferences? preferences = null) => new(
        new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false),
        _git, new FakeTimeProvider(Now), NullLogger.Instance, preferences: preferences);

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

    [Fact]
    public async Task Side_by_side_reads_the_whole_index_and_disk_versions_and_is_remembered()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", Lines("one", "two", "three")));
        repo.WriteFile("a.txt", Lines("one", "TWO", "three"));
        using var preferences = new Preferences(_store, new UserPreferences());
        using var tab = CreateTab(repo, preferences);
        await tab.WorkingTree.StartAsync();
        var diff = tab.Diff;

        await diff.OpenWorkingAsync(tab.WorkingTree.Unstaged.Single().Entry, WorkingDiffSide.Unstaged);
        Assert.Null(diff.Sides);
        Assert.False(diff.IsSideBySide);

        await diff.ToggleLayoutCommand.ExecuteAsync(null);

        Assert.True(diff.IsSideBySide);
        Assert.Equal(new DiffSides("a.txt", "a.txt", Lines("one", "two", "three"), Lines("one", "TWO", "three")), diff.Sides);
        Assert.Equal(DiffLayout.SideBySide, _store.Saved?.DiffLayout);

        // A diff opened later reads its sides at once; closing forgets them.
        using var other = CreateTab(repo, preferences);
        await other.WorkingTree.StartAsync();
        await other.Diff.OpenWorkingAsync(other.WorkingTree.Unstaged.Single().Entry, WorkingDiffSide.Unstaged);
        Assert.NotNull(other.Diff.Sides);
        other.Diff.CloseCommand.Execute(null);
        Assert.Null(other.Diff.Sides);
    }

    [Fact]
    public async Task Side_by_side_of_a_commit_compares_with_its_first_parent_and_handles_added_files()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "old\n"));
        var sha = repo.Commit("change", ("a.txt", "new\n"), ("b.txt", "added\n"));
        using var tab = CreateTab(repo);
        var details = Assert.IsType<Vigo.Core.Results.Success<CommitDetails>>(
            (await _git.GetCommitAsync(repo.Path, GitSha.Parse(sha), TestContext.Current.CancellationToken)).Value).Value;
        var diff = tab.Diff;
        await diff.ToggleLayoutCommand.ExecuteAsync(null);

        await diff.OpenAsync(details, details.Files.Single(f => f.Path == "a.txt"));
        Assert.Equal(new DiffSides("a.txt", "a.txt", "old\n", "new\n"), diff.Sides);

        await diff.OpenAsync(details, details.Files.Single(f => f.Path == "b.txt"));
        Assert.Equal(new DiffSides("b.txt", "b.txt", "", "added\n"), diff.Sides);
    }

    [Fact]
    public async Task Lines_toggled_from_the_editor_are_staged()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", Lines("one", "two", "three")));
        repo.WriteFile("a.txt", Lines("one", "TWO", "three", "four"));
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();
        var diff = tab.Diff;
        await diff.OpenWorkingAsync(tab.WorkingTree.Unstaged.Single().Entry, WorkingDiffSide.Unstaged);

        var document = Assert.IsType<DiffDocument>(diff.Document);
        Assert.Equal("cdaca", document.Kinds);

        // "four" (document line 5) only; a context line is ignored.
        diff.ToggleDocumentLine(5, extend: false);
        diff.ToggleDocumentLine(1, extend: false);
        Assert.Equal([5], diff.SelectedDocumentLines);

        await diff.ApplySelectionCommand.ExecuteAsync(null);

        var staged = await _git.ReadFileTextAsync(repo.Path, FileVersion.InIndex("a.txt"), TestContext.Current.CancellationToken);
        Assert.Equal(Lines("one", "two", "three", "four"), Assert.IsType<Vigo.Core.Results.Success<string?>>(staged.Value).Value);
    }
}
