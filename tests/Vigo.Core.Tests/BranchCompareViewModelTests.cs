using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class BranchCompareViewModelTests
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

    /// <summary>main (checked out) has src/d.txt; feature has b.txt; both changed a.txt on distinct lines.</summary>
    private static TestRepoBuilder CreateRepo()
    {
        var repo = TestRepoBuilder.Create();
        var lines = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));
        repo.Commit("root", ("a.txt", lines));
        repo.Branch("feature");
        repo.Commit("on feature", ("b.txt", "b\n"), ("a.txt", lines.Replace("line 10", "B10")));
        repo.Checkout("main");
        repo.Commit("on main", ("src/d.txt", "d\n"), ("a.txt", lines.Replace("line 1\n", "A1\n")));
        return repo;
    }

    private static GitRef Feature(RepositoryTabViewModel tab) => tab.Graph.Refs!.OfKind(GitRefKind.LocalBranch).Single(r => r.Name == "feature");

    [Fact]
    public async Task Opening_from_a_branch_compares_it_with_the_current_one_in_place_of_the_graph()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);

        await tab.ShowCompareAsync(Feature(tab));

        Assert.True(tab.Compare.IsOpen);
        Assert.False(tab.ShowGraph);
        Assert.Equal(("main", "feature"), (tab.Compare.NameA, tab.Compare.NameB));
        Assert.Equal(["a.txt", "d.txt", "b.txt"], tab.Compare.Rows.Select(r => r.Entry.Path[(r.Entry.Path.LastIndexOf('/') + 1)..]));
        Assert.Equal(BranchCompareCategory.ModifiedBothMerges, tab.Compare.Rows[0].Entry.Category);
        Assert.True(tab.Compare.Rows[0].HasGroupTitle);
        Assert.Equal(3, tab.Compare.Chips.Count);
    }

    [Fact]
    public async Task A_branch_dropped_on_another_one_is_side_A_against_it()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);
        var main = tab.Graph.Refs!.OfKind(GitRefKind.LocalBranch).Single(r => r.Name == "main");

        await tab.ShowCompareOfAsync(Feature(tab), main);

        Assert.True(tab.Compare.IsOpen);
        Assert.Equal(("feature", "main"), (tab.Compare.NameA, tab.Compare.NameB));
    }

    [Fact]
    public async Task Swapping_exchanges_the_sides_and_compares_again()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);
        await tab.ShowCompareAsync(Feature(tab));

        await tab.Compare.SwapCommand.ExecuteAsync(null);

        Assert.Equal(("feature", "main"), (tab.Compare.NameA, tab.Compare.NameB));
        Assert.Equal(BranchCompareCategory.OnlyInB, Assert.Single(tab.Compare.Rows, r => r.Entry.Path == "src/d.txt").Entry.Category);
    }

    [Fact]
    public async Task A_category_chip_and_the_text_filter_narrow_the_list()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);
        await tab.ShowCompareAsync(Feature(tab));

        tab.Compare.ToggleChipCommand.Execute(tab.Compare.Chips.Single(c => c.Category == BranchCompareCategory.OnlyInB));

        Assert.Equal(["b.txt"], tab.Compare.Rows.Select(r => r.Entry.Path));

        tab.Compare.ToggleChipCommand.Execute(tab.Compare.Chips.Single(c => c.IsSelected));
        tab.Compare.Filter = "SRC/";

        Assert.Equal(["src/d.txt"], tab.Compare.Rows.Select(r => r.Entry.Path));

        tab.Compare.Filter = "zzz";

        Assert.Empty(tab.Compare.Rows);
        Assert.True(tab.Compare.HasEmptyMessage);
    }

    [Fact]
    public async Task A_file_opens_its_diff_above_the_list_and_closing_it_comes_back_to_the_list()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);
        await tab.ShowCompareAsync(Feature(tab));

        tab.Compare.OpenFileCommand.Execute(tab.Compare.Rows.Single(r => r.Entry.Path == "b.txt"));
        await Task.Delay(1000, Ct);

        Assert.True(tab.Diff.IsOpen);
        Assert.True(tab.Compare.IsOpen);
        Assert.Equal("b.txt", tab.Diff.File?.Path);
        Assert.NotNull(tab.Diff.Diff);

        tab.Diff.CloseCommand.Execute(null);

        Assert.True(tab.Compare.IsOpen);
        Assert.False(tab.ShowGraph);
    }

    [Fact]
    public async Task A_file_changed_on_both_sides_tells_whose_change_each_line_is()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);
        await tab.ShowCompareAsync(Feature(tab));

        tab.Compare.OpenFileCommand.Execute(tab.Compare.Rows.Single(r => r.Entry.Path == "a.txt"));
        await Task.Delay(1500, Ct);

        var document = Assert.IsType<DiffDocument>(tab.Diff.Document);
        Assert.NotNull(document.Origins);
        var changed = Enumerable.Range(0, document.LineCount).Where(i => document.Kinds[i] is DiffDocument.Added or DiffDocument.Deleted);
        Assert.Equal(["A:A1", "A:line 1", "B:line 10", "B:B10"],
            changed.Select(i => $"{document.Origins![i]}:{document.Text.Split((char)10)[i]}"));
        Assert.Equal(("main", "feature"), (tab.Diff.CompareNameA, tab.Diff.CompareNameB));

        await tab.Diff.ToggleLayoutCommand.ExecuteAsync(null);

        // Left side = A, right side = B: A's change (line 1) is coloured on the left only, B's (line 10) on the right only.
        Assert.Equal([(true, 1, 'A'), (false, 10, 'B')], tab.Diff.Sides!.Origins);
    }

    [Fact]
    public async Task A_line_both_branches_changed_is_coloured_on_both_sides()
    {
        using var repo = TestRepoBuilder.Create();
        string File(string needs) => $"top\nneeds: {needs}\nbottom\n";
        repo.Commit("root", ("a.txt", File("[x64]")));
        repo.Branch("feature");
        repo.Commit("B", ("a.txt", File("[x64, arm64]")));
        repo.Checkout("main");
        repo.Commit("A", ("a.txt", File("[x64, arm64, unix]")));
        using var tab = await OpenAsync(repo);
        await tab.ShowCompareAsync(Feature(tab));

        tab.Compare.OpenFileCommand.Execute(tab.Compare.Rows.Single(r => r.Entry.Path == "a.txt"));
        await Task.Delay(1500, Ct);
        await tab.Diff.ToggleLayoutCommand.ExecuteAsync(null);

        Assert.Equal([(true, 2, 'A'), (false, 2, 'B')], tab.Diff.Sides!.Origins);
    }

    [Fact]
    public async Task A_line_one_branch_deleted_stays_coloured_with_that_branchs_colour()
    {
        using var repo = TestRepoBuilder.Create();
        var lines = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));
        repo.Commit("root", ("a.txt", lines));
        repo.Branch("feature");
        repo.Commit("B deletes 8", ("a.txt", lines.Replace("line 8\n", string.Empty)));
        repo.Checkout("main");
        repo.Commit("A deletes 2, edits 5", ("a.txt", lines.Replace("line 2\n", string.Empty).Replace("line 5", "A5")));
        using var tab = await OpenAsync(repo);
        await tab.ShowCompareAsync(Feature(tab));

        tab.Compare.OpenFileCommand.Execute(tab.Compare.Rows.Single(r => r.Entry.Path == "a.txt"));
        await Task.Delay(1500, Ct);
        await tab.Diff.ToggleLayoutCommand.ExecuteAsync(null);

        // Left (A's file): A5 is A's edit; line 8 is there only because A kept it, B deleted it. Right (B's file): line 2 is
        // there because A deleted it; "line 5" is the base text A edited, so it stays plain on the right.
        var origins = tab.Diff.Sides!.Origins!;
        Assert.Contains((true, 7, 'B'), origins); // A's file has no line 2, so its "line 8" is line 7
        Assert.Contains((false, 2, 'A'), origins);
        Assert.Contains((true, 4, 'A'), origins); // A5
        Assert.DoesNotContain(origins, o => o is (false, 5, _));
    }

    [Fact]
    public async Task A_line_both_branches_added_the_same_way_is_marked_as_common()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "top\nbottom\n"));
        repo.Branch("feature");
        repo.Commit("B", ("a.txt", "top\nshared\nonly B\nbottom\n"));
        repo.Checkout("main");
        repo.Commit("A", ("a.txt", "top\nshared\nbottom\n"));
        using var tab = await OpenAsync(repo);
        await tab.ShowCompareAsync(Feature(tab));

        tab.Compare.OpenFileCommand.Execute(tab.Compare.Rows.Single(r => r.Entry.Path == "a.txt"));
        await Task.Delay(1500, Ct);
        await tab.Diff.ToggleLayoutCommand.ExecuteAsync(null);

        Assert.Contains((true, 2, 'C'), tab.Diff.Sides!.Origins!);
        Assert.Contains((false, 2, 'C'), tab.Diff.Sides!.Origins!);
        Assert.Contains((false, 3, 'B'), tab.Diff.Sides!.Origins!);
    }

    [Fact]
    public async Task Without_a_second_branch_the_user_is_asked_to_choose()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "1"));
        using var tab = await OpenAsync(repo);

        await tab.ShowCompareAsync();

        Assert.True(tab.Compare.IsOpen);
        Assert.True(tab.Compare.HasEmptyMessage);
        Assert.Empty(tab.Compare.Rows);
    }
}
