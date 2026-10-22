using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.Git.Refs;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class StashTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo;

    public StashTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Commit("init", ("a.txt", "a\n"), ("b.txt", "b\n"));
    }

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private string Read(string path) => File.ReadAllText(Path.Combine(_repo.Path, path));

    private bool Exists(string path) => File.Exists(Path.Combine(_repo.Path, path));

    private string[] StashList() =>
        _repo.Git("stash", "list", "--format=%gs").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private async Task<GitSha> StashAsync(StashRequest request)
    {
        var result = await _git.StashAsync(_repo.Path, request, Ct);
        return Assert.IsType<Success<GitSha>>(result.Value, exactMatch: false).Value;
    }

    [Fact]
    public async Task Stash_puts_tracked_and_untracked_changes_aside_with_a_message()
    {
        _repo.WriteFile("a.txt", "changed\n").WriteFile("new.txt", "new\n");

        var stash = await StashAsync(new StashRequest("  try paging  ", IncludeUntracked: true));

        Assert.Equal(_repo.Git("rev-parse", "stash@{0}").Trim(), stash.ToString());
        Assert.Equal(["On main: try paging"], StashList());
        Assert.Equal("a\n", Read("a.txt"));
        Assert.False(Exists("new.txt"));
        Assert.Equal(string.Empty, _repo.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task Description_stays_in_the_commit_while_the_refs_show_the_title_only()
    {
        _repo.WriteFile("a.txt", "changed\n");

        var stash = await StashAsync(new StashRequest("try paging", IncludeUntracked: true, Description: "line one\r\nline two  "));

        Assert.Equal("On main: try paging\n\nline one\nline two", _repo.Git("log", "-1", "--format=%B", "refs/stash").Trim());
        var refs = Assert.IsType<Success<RepositoryRefs>>((await _git.GetRefsAsync(_repo.Path, Ct)).Value, exactMatch: false).Value;
        Assert.Equal("On main: try paging", Assert.Single(refs.Refs, r => r.Kind == GitRefKind.Stash).Message);
        Assert.Equal("On main: try paging", RefReader.FromCli("", _repo.Git("stash", "list", RefReader.StashListFormat), null, null)
            .Refs.Single(r => r.Kind == GitRefKind.Stash).Message);
    }

    [Fact]
    public async Task Without_untracked_files_they_stay_and_git_names_the_stash()
    {
        _repo.WriteFile("a.txt", "changed\n").WriteFile("new.txt", "new\n");

        await StashAsync(new StashRequest(null, IncludeUntracked: false));

        Assert.StartsWith("WIP on main:", Assert.Single(StashList()), StringComparison.Ordinal);
        Assert.True(Exists("new.txt"));
        Assert.Equal("a\n", Read("a.txt"));
    }

    [Fact]
    public async Task Nothing_to_stash_is_a_failure()
    {
        var result = await _git.StashAsync(_repo.Path, new StashRequest(null, IncludeUntracked: true), Ct);

        Assert.Equal("Aucune modification à mettre de côté.", Assert.IsType<Failure>(result.Value).Message);
        Assert.Empty(StashList());
    }

    [Fact]
    public async Task Apply_keeps_the_stash_and_pop_drops_it()
    {
        _repo.WriteFile("a.txt", "changed\n");
        var stash = await StashAsync(new StashRequest("one", IncludeUntracked: true));

        AssertDone(await _git.ApplyStashAsync(_repo.Path, stash, drop: false, Ct));
        Assert.Equal("changed\n", Read("a.txt"));
        Assert.Single(StashList());

        _repo.Git("checkout", "--", "a.txt");
        AssertDone(await _git.ApplyStashAsync(_repo.Path, stash, drop: true, Ct));
        Assert.Equal("changed\n", Read("a.txt"));
        Assert.Empty(StashList());
    }

    [Fact]
    public async Task A_stash_is_found_by_its_commit_after_the_indexes_shifted()
    {
        _repo.WriteFile("a.txt", "first\n");
        var first = await StashAsync(new StashRequest("first", IncludeUntracked: true));
        _repo.WriteFile("b.txt", "second\n");
        await StashAsync(new StashRequest("second", IncludeUntracked: true));

        // "first" is now stash@{1}.
        AssertDone(await _git.DropStashAsync(_repo.Path, first, Ct));

        Assert.Equal(["On main: second"], StashList());
        var gone = await _git.ApplyStashAsync(_repo.Path, first, drop: false, Ct);
        Assert.StartsWith("Ce stash n'existe plus", Assert.IsType<Failure>(gone.Value).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conflicting_pop_reports_the_paths_and_keeps_the_stash()
    {
        _repo.WriteFile("a.txt", "stashed\n");
        var stash = await StashAsync(new StashRequest("conflict", IncludeUntracked: true));
        _repo.Commit("other", ("a.txt", "committed\n"));

        var result = await _git.ApplyStashAsync(_repo.Path, stash, drop: true, Ct);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Equal(["a.txt"], conflict.Paths);
        Assert.Single(StashList());
    }

    [Fact]
    public async Task Applying_over_conflicting_local_changes_is_refused()
    {
        _repo.WriteFile("a.txt", "stashed\n");
        var stash = await StashAsync(new StashRequest("x", IncludeUntracked: true));
        _repo.WriteFile("a.txt", "local\n");

        var result = await _git.ApplyStashAsync(_repo.Path, stash, drop: true, Ct);

        var failure = Assert.IsType<Failure>(result.Value);
        Assert.StartsWith("Des modifications locales seraient écrasées", failure.Message, StringComparison.Ordinal);
        Assert.Equal(FailureReason.LocalChangesWouldBeOverwritten, failure.Reason);
        Assert.Equal("local\n", Read("a.txt"));
        Assert.Single(StashList());
    }

    private const string Eight = "1\n2\n3\n4\n5\n6\n7\n8\n";

    private async Task<GitSha> StashFilesAsync()
    {
        _repo.Commit("lines", ("a.txt", Eight), ("b.txt", "b\n"));
        _repo.WriteFile("a.txt", Eight.Replace("1\n", "one\n", StringComparison.Ordinal)).WriteFile("b.txt", "b stashed\n");
        return await StashAsync(new StashRequest("two files", IncludeUntracked: true));
    }

    [Fact]
    public async Task Applying_one_file_of_a_stash_merges_it_with_local_changes_and_keeps_the_stash()
    {
        var stash = await StashFilesAsync();
        // A local change to the same file, far from the stashed one, and a staged change that must stay staged.
        _repo.WriteFile("a.txt", Eight.Replace("8\n", "eight\n", StringComparison.Ordinal));
        _repo.WriteFile("b.txt", "b local\n");
        _repo.Git("add", "b.txt");

        var result = await _git.ApplyStashFilesAsync(_repo.Path, stash, ["a.txt"], Ct);

        AssertDone(result);
        Assert.Equal("one\n2\n3\n4\n5\n6\n7\neight\n", Read("a.txt"));
        Assert.Equal("b local\n", Read("b.txt"));
        Assert.Single(StashList());
        // a.txt is back as an unstaged change; b.txt keeps its staged state.
        Assert.Equal([" M a.txt", "M  b.txt"], _repo.Git("status", "--porcelain").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Applying_files_with_a_path_list_too_long_for_the_command_line_still_picks_only_those_files()
    {
        var stash = await StashFilesAsync();
        // 600 paths of 60 characters: over the 24,000 characters kept on a command line.
        List<string> paths = ["b.txt"];
        paths.AddRange(Enumerable.Range(0, 600).Select(i => $"missing/{i:D4}/" + new string('x', 48)));

        AssertDone(await _git.ApplyStashFilesAsync(_repo.Path, stash, paths, Ct));

        Assert.Equal("b stashed\n", Read("b.txt"));
        Assert.Equal(Eight, Read("a.txt"));
    }

    private static readonly string[] WantedFiles = ["c.txt", "new name.txt"];

    [Fact]
    public void A_patch_is_filtered_by_the_files_of_its_sections()
    {
        const string patch = "diff --git a/a.txt b/a.txt\nindex 1..2 100644\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-1\n+2\n" +
            "diff --git a/old name.txt b/new name.txt\nrename from old name.txt\nrename to new name.txt\n" +
            "diff --git a/c.txt b/c.txt\nindex 3..4 100644\n--- a/c.txt\n+++ b/c.txt\n@@ -1 +1 @@\n-x\n+y\n";

        var filtered = GitService.FilterPatch(patch, WantedFiles);

        Assert.DoesNotContain("a.txt", filtered, StringComparison.Ordinal);
        Assert.Contains("diff --git a/old name.txt b/new name.txt", filtered, StringComparison.Ordinal);
        Assert.EndsWith("-x\n+y\n", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_the_files_of_a_clean_working_directory_restores_only_those_files()
    {
        var stash = await StashFilesAsync();

        AssertDone(await _git.ApplyStashFilesAsync(_repo.Path, stash, ["b.txt"], Ct));

        Assert.Equal("b stashed\n", Read("b.txt"));
        Assert.Equal(Eight, Read("a.txt"));
        Assert.Equal([" M b.txt"], _repo.Git("status", "--porcelain").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Applying_a_file_over_a_conflicting_local_change_leaves_the_conflict_in_the_index()
    {
        var stash = await StashFilesAsync();
        _repo.WriteFile("a.txt", Eight.Replace("1\n", "uno\n", StringComparison.Ordinal));

        var result = await _git.ApplyStashFilesAsync(_repo.Path, stash, ["a.txt"], Ct);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Equal(["a.txt"], conflict.Paths);
        Assert.Contains("<<<<<<<", Read("a.txt"), StringComparison.Ordinal);
        // Unmerged in the real index (base, local and stash versions), which is what the conflict editor reads.
        Assert.Equal(3, _repo.Git("ls-files", "-u").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Single(StashList());
    }

    [Fact]
    public async Task A_stash_that_does_not_touch_the_files_says_so_and_changes_nothing()
    {
        var stash = await StashFilesAsync();
        _repo.Commit("other", ("c.txt", "c\n"));

        var result = await _git.ApplyStashFilesAsync(_repo.Path, stash, ["c.txt"], Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.Equal(string.Empty, _repo.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task A_message_starting_with_a_dash_stays_a_message()
    {
        _repo.WriteFile("a.txt", "changed\n");

        await StashAsync(new StashRequest("--all", IncludeUntracked: false));

        Assert.Equal(["On main: --all"], StashList());
    }
}
