using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class BranchPickerTests
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

    private static TestRepoBuilder CreateRepo()
    {
        var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "1"));
        repo.Branch("feature/login");
        repo.Branch("fix-typo");
        repo.Checkout("main");
        repo.Tag("v1.0", "Release");
        return repo;
    }

    [Fact]
    public async Task Lists_the_current_branch_first_then_the_other_branches_and_the_tags()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);

        tab.BranchPicker.Refresh();

        var items = tab.BranchPicker.Items;
        Assert.Equal(["main", "feature/login", "fix-typo", "v1.0"], items.Select(i => i.Name));
        Assert.True(items[0].IsCurrent);
        Assert.Equal("courante", items[0].Detail);
        Assert.Equal("Branches locales", items[0].GroupTitle);
        Assert.Null(items[1].GroupTitle);
        Assert.Equal("Tags", items[3].GroupTitle);
    }

    [Fact]
    public async Task The_filter_matches_names_without_regard_to_case()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);
        tab.BranchPicker.Refresh();

        tab.BranchPicker.Filter = "FIX";

        Assert.Equal(["fix-typo"], tab.BranchPicker.Items.Select(i => i.Name));
        Assert.True(tab.BranchPicker.HasItems);

        tab.BranchPicker.Filter = "zzz";

        Assert.False(tab.BranchPicker.HasItems);

        tab.BranchPicker.Refresh();

        Assert.Equal(string.Empty, tab.BranchPicker.Filter);
        Assert.Equal(4, tab.BranchPicker.Items.Count);
    }

    [Fact]
    public async Task Choosing_a_branch_checks_it_out_and_the_current_one_does_nothing()
    {
        using var repo = CreateRepo();
        using var tab = await OpenAsync(repo);
        tab.BranchPicker.Refresh();

        await tab.BranchPicker.ChooseCommand.ExecuteAsync(tab.BranchPicker.Items[0]);
        Assert.Equal("main", tab.Graph.Refs?.CurrentBranch);

        var feature = tab.BranchPicker.Items.First(i => i.Name == "fix-typo");
        await tab.BranchPicker.ChooseCommand.ExecuteAsync(feature);
        await tab.Graph.RefreshAsync();
        await tab.Graph.WhenReloadedAsync();

        Assert.Equal("fix-typo", tab.Graph.Refs?.CurrentBranch);
    }
}
