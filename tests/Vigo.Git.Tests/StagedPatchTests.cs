using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class StagedPatchTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<StagedPatch> PatchAsync(TestRepoBuilder repo, int maxChars = 60_000) =>
        Assert.IsType<Success<StagedPatch>>((await _git.GetStagedPatchAsync(repo.Path, maxChars, Ct)).Value).Value;

    [Fact]
    public async Task Nothing_staged_gives_an_empty_patch()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "changed\n");

        Assert.Equal(string.Empty, (await PatchAsync(repo)).Text);
    }

    [Fact]
    public async Task Staged_changes_give_stat_patch_and_recent_subjects()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("first subject", ("a.txt", "a\n"));
        repo.Commit("second subject", ("b.txt", "b\n"));
        repo.WriteFile("a.txt", "a2\n").WriteFile("new é.txt", "n\n").Stage("a.txt", "new é.txt");

        var patch = await PatchAsync(repo);

        Assert.Contains("a.txt", patch.Text, StringComparison.Ordinal);
        Assert.Contains("+a2", patch.Text, StringComparison.Ordinal);
        Assert.Contains("new é.txt", patch.Text, StringComparison.Ordinal);
        Assert.False(patch.Truncated);
        Assert.Equal(["second subject", "first subject"], patch.RecentSubjects);
    }

    [Fact]
    public async Task Lock_files_stay_in_the_stat_but_out_of_the_patch()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").WriteFile("deps.lock", "SECRETLOCKCONTENT\n").Stage("a.txt", "deps.lock");

        var patch = await PatchAsync(repo);

        Assert.Contains("deps.lock", patch.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETLOCKCONTENT", patch.Text, StringComparison.Ordinal);
        Assert.Contains("+a2", patch.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_patch_is_cut_at_the_limit()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("big.txt", string.Concat(Enumerable.Repeat("some line of text\n", 500))).Stage("big.txt");

        var patch = await PatchAsync(repo, maxChars: 1_000);

        Assert.True(patch.Truncated);
        Assert.True(patch.Text.Length <= 1_000);
    }

    [Fact]
    public async Task First_commit_has_a_patch_and_no_subjects()
    {
        using var repo = TestRepoBuilder.Create();
        repo.WriteFile("a.txt", "a\n").Stage("a.txt");

        var patch = await PatchAsync(repo);

        Assert.Contains("+a", patch.Text, StringComparison.Ordinal);
        Assert.Empty(patch.RecentSubjects);
    }
}
