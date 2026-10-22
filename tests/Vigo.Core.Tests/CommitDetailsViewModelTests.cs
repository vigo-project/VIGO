using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class CommitDetailsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<RepositoryTabViewModel> OpenAsync(TestRepoBuilder repo)
    {
        var info = new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, new GitService(new GitCli()), new FakeTimeProvider(Now), NullLogger.Instance);
        await tab.Graph.LoadAsync(Ct);
        return tab;
    }

    private static TestRepoBuilder CreateRepository(out string root, out string second)
    {
        var repo = TestRepoBuilder.Create();
        root = repo.Commit("root", ("a.txt", "1\n2\n3\n"), ("docs/readme.md", "hello\n"));
        second = repo.Commit("Second commit\n\nWith a body.", ("a.txt", "1\nTWO\n3\n4\n"), ("docs/new.md", "new\n"));
        repo.Tag("v1");
        return repo;
    }

    [Fact]
    public async Task Selecting_a_row_loads_the_commit_details()
    {
        using var repo = CreateRepository(out _, out var second);
        using var tab = await OpenAsync(repo);
        var details = tab.Details;
        Assert.False(details.HasSelection);

        tab.Graph.SelectedRow = 0;
        await details.LoadAsync(0);

        Assert.True(details.HasSelection);
        Assert.False(details.IsLoading);
        Assert.Null(details.ErrorMessage);
        Assert.Equal(second, details.FullSha);
        Assert.Equal("Second commit", details.Summary);
        Assert.Equal("With a body.", details.Body);
        Assert.Equal("identique à l'auteur", details.CommitterDisplay);
        Assert.Equal("Vigo Test", details.AuthorName);
        Assert.Contains(details.Badges, b => b.Label == "v1");
        Assert.Single(details.Parents);

        Assert.Equal(["new.md", "a.txt"], details.Files.Select(f => f.Name).OrderByDescending(n => n.Length));
        Assert.Equal("2 fichiers modifiés", details.FilesSummary);
        Assert.Equal("+3", details.AdditionsDisplay);
        Assert.Equal("−1", details.DeletionsDisplay);
        var a = Assert.Single(details.Files, f => f.Name == "a.txt");
        // Bars scaled to the largest file (3 changed lines = 40 px).
        Assert.Equal(("M", 27d, 13d), (a.StatusLetter, a.AdditionsBarWidth, a.DeletionsBarWidth));
        Assert.Equal(("A", "docs"), (details.Files.Single(f => f.Name == "new.md").StatusLetter, details.Files.Single(f => f.Name == "new.md").Directory));
    }

    [Fact]
    public async Task Changed_files_can_be_shown_as_a_tree_with_collapsible_folders()
    {
        using var repo = CreateRepository(out _, out _);
        using var tab = await OpenAsync(repo);
        var details = tab.Details;
        await details.LoadAsync(0);
        Assert.Equal(["a.txt", "docs/new.md"], details.FileRows.Select(r => r.Path));

        details.ShowAsTree = true;
        Assert.Equal(["docs", "docs/new.md", "a.txt"], details.FileRows.Select(r => r.Path));
        var docs = Assert.IsType<FolderRow>(details.FileRows[0]);

        details.ToggleFolderCommand.Execute(docs);
        Assert.Equal(["docs", "a.txt"], details.FileRows.Select(r => r.Path));
        Assert.Equal(2, details.Files.Count); // still counted in the summary

        details.ToggleFolderCommand.Execute(details.FileRows[0]);
        Assert.Equal(3, details.FileRows.Count);
    }

    [Fact]
    public async Task Parent_link_selects_the_parent_and_close_clears_the_selection()
    {
        using var repo = CreateRepository(out var root, out _);
        using var tab = await OpenAsync(repo);
        await tab.Details.LoadAsync(0);

        tab.Details.SelectParentCommand.Execute(tab.Details.Parents[0]);

        Assert.Equal(1, tab.Graph.SelectedRow);
        Assert.Equal(GitSha.Parse(root), tab.Graph.GetCommit(1).Sha);

        tab.Details.CloseCommand.Execute(null);
        await tab.Details.LoadAsync(tab.Graph.SelectedRow);

        Assert.Equal(-1, tab.Graph.SelectedRow);
        Assert.False(tab.Details.HasSelection);
        Assert.Empty(tab.Details.Files);
    }

    [Fact]
    public async Task Opening_a_file_shows_its_diff_and_a_new_selection_closes_it()
    {
        using var repo = CreateRepository(out _, out _);
        using var tab = await OpenAsync(repo);
        await tab.Details.LoadAsync(0);
        var file = tab.Details.Files.Single(f => f.Name == "a.txt").File;

        await tab.Diff.OpenAsync(tab.Details.Details!, file);

        var diff = tab.Diff;
        Assert.True(diff.IsOpen);
        Assert.False(diff.IsLoading);
        Assert.Null(diff.ErrorMessage);
        Assert.Equal("a.txt", diff.PathDisplay);
        Assert.Equal(("M", "+2", "−1"), (diff.StatusLetter, diff.AdditionsDisplay, diff.DeletionsDisplay));
        Assert.Equal("Hunk 1/1", diff.HunkDisplay);
        Assert.Null(diff.PlaceholderMessage);
        Assert.Equal(["2", "TWO", "4"], diff.Diff!.Hunks[0].Lines.Where(l => l.Kind != DiffLineKind.Context).Select(l => l.Text));

        diff.NextHunkCommand.Execute(null);
        Assert.Equal(0, diff.CurrentHunk);

        tab.Graph.SelectedRow = 1;
        Assert.False(diff.IsOpen);
        Assert.Null(diff.Diff);
    }

    [Fact]
    public async Task Root_commit_diff_compares_with_the_empty_tree()
    {
        using var repo = CreateRepository(out _, out _);
        using var tab = await OpenAsync(repo);
        await tab.Details.LoadAsync(1);
        Assert.Empty(tab.Details.Parents);

        await tab.Diff.OpenAsync(tab.Details.Details!, tab.Details.Files.Single(f => f.Name == "readme.md").File);

        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 1, "hello"), Assert.Single(Assert.Single(tab.Diff.Diff!.Hunks).Lines));
    }
}
