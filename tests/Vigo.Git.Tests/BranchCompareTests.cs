using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Branch comparison: A (main) and B (feature) classified through their merge base.</summary>
public sealed class BranchCompareTests : IDisposable
{
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));

    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitSha Sha(string hex) => GitSha.Parse(hex.Trim());

    /// <summary>main = A, feature = B; each changes files the other leaves alone, and some in common.</summary>
    private async Task<BranchComparison> CompareDivergedAsync()
    {
        _repo.Commit("base", ("a_mod.txt", Ten), ("b_mod.txt", Ten), ("both_auto.txt", Ten), ("both_conflict.txt", Ten),
            ("both_same.txt", Ten), ("old_name.txt", Ten), ("same.txt", Ten));
        _repo.Branch("feature");
        var b = _repo.Commit("on feature",
            ("b_mod.txt", Ten.Replace("line 5", "B5")), ("both_auto.txt", Ten.Replace("line 9", "B9")),
            ("both_conflict.txt", Ten.Replace("line 1\n", "B1\n")), ("both_same.txt", Ten.Replace("line 3", "same")), ("only_b.txt", "b\n"));
        _repo.Git("mv", "old_name.txt", "new_name.txt");
        b = _repo.Commit("rename");
        _repo.Checkout("main");
        var a = _repo.Commit("on main",
            ("a_mod.txt", Ten.Replace("line 5", "A5")), ("both_auto.txt", Ten.Replace("line 1\n", "A1\n")),
            ("both_conflict.txt", Ten.Replace("line 1\n", "A1\n")), ("both_same.txt", Ten.Replace("line 3", "same")), ("only_a.txt", "a\n"));

        return Assert.IsType<Success<BranchComparison>>((await _git.CompareBranchesAsync(_repo.Path, Sha(a), Sha(b), Ct)).Value).Value;
    }

    private static BranchCompareEntry Entry(BranchComparison comparison, string path) => Assert.Single(comparison.Entries, e => e.Path == path);

    [Fact]
    public async Task A_file_only_in_A_or_only_in_B_is_reported_on_its_side()
    {
        var comparison = await CompareDivergedAsync();

        Assert.Equal(BranchCompareCategory.OnlyInA, Entry(comparison, "only_a.txt").Category);
        Assert.Equal(FileChangeKind.Deleted, Entry(comparison, "only_a.txt").Kind);
        Assert.Equal(BranchCompareCategory.OnlyInB, Entry(comparison, "only_b.txt").Category);
        Assert.Equal(FileChangeKind.Added, Entry(comparison, "only_b.txt").Kind);
    }

    [Fact]
    public async Task A_file_changed_on_one_side_tells_which_branch_is_ahead()
    {
        var comparison = await CompareDivergedAsync();

        Assert.Equal(BranchCompareCategory.ModifiedInA, Entry(comparison, "a_mod.txt").Category);
        Assert.Equal(BranchCompareCategory.ModifiedInB, Entry(comparison, "b_mod.txt").Category);
    }

    [Fact]
    public async Task Changes_on_both_sides_merge_or_conflict()
    {
        var comparison = await CompareDivergedAsync();

        Assert.True(comparison.ConflictsKnown);
        Assert.Equal(BranchCompareCategory.ModifiedBothMerges, Entry(comparison, "both_auto.txt").Category);
        Assert.Equal(BranchCompareCategory.ModifiedBothConflicts, Entry(comparison, "both_conflict.txt").Category);
    }

    [Fact]
    public async Task The_same_change_on_both_sides_is_identical_and_untouched_files_are_not_listed()
    {
        var comparison = await CompareDivergedAsync();

        var same = Entry(comparison, "both_same.txt");
        Assert.Equal(BranchCompareCategory.Identical, same.Category);
        Assert.Null(same.ToChangedFile());
        Assert.DoesNotContain(comparison.Entries, e => e.Path == "same.txt");
    }

    [Fact]
    public async Task A_rename_is_followed()
    {
        var comparison = await CompareDivergedAsync();

        var renamed = Entry(comparison, "new_name.txt");
        Assert.Equal("old_name.txt", renamed.OldPath);
        Assert.Equal(FileChangeKind.Renamed, renamed.Kind);
        Assert.Equal(BranchCompareCategory.ModifiedInB, renamed.Category);
        Assert.DoesNotContain(comparison.Entries, e => e.Path == "old_name.txt");
    }

    [Fact]
    public async Task The_merge_base_is_the_common_ancestor()
    {
        var baseSha = _repo.Commit("base", ("f.txt", "1\n"));
        _repo.Branch("feature");
        var b = _repo.Commit("b", ("g.txt", "g\n"));
        _repo.Checkout("main");
        var a = _repo.Commit("a", ("h.txt", "h\n"));

        var comparison = Assert.IsType<Success<BranchComparison>>((await _git.CompareBranchesAsync(_repo.Path, Sha(a), Sha(b), Ct)).Value).Value;

        Assert.Equal(Sha(baseSha), comparison.MergeBase);
    }

    [Fact]
    public async Task Branches_without_common_ancestor_fail_cleanly()
    {
        var a = _repo.Commit("main", ("a.txt", "a\n"));
        _repo.Git("checkout", "--quiet", "--orphan", "other");
        _repo.Git("rm", "-rf", "--quiet", ".");
        var b = _repo.Commit("other", ("b.txt", "b\n"));

        var result = await _git.CompareBranchesAsync(_repo.Path, Sha(a), Sha(b), Ct);

        Assert.IsType<Failure>(result.Value);
    }
}
