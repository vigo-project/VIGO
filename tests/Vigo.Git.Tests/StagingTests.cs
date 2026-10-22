using System.Text;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class StagingTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<WorkingTreeStatus> StatusAsync(TestRepoBuilder repo)
    {
        var result = await _git.GetStatusAsync(repo.Path, Ct);
        return Assert.IsType<Success<WorkingTreeStatus>>(result.Value).Value;
    }

    private async Task<StatusEntry> EntryAsync(TestRepoBuilder repo, string path) =>
        Assert.Single((await StatusAsync(repo)).Entries, e => e.Path == path);

    private async Task<FileDiff> DiffAsync(TestRepoBuilder repo, string path, WorkingDiffSide side)
    {
        var result = await _git.GetWorkingFileDiffAsync(repo.Path, await EntryAsync(repo, path), side, Ct);
        return Assert.IsType<Success<FileDiff>>(result.Value).Value;
    }

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Message : result.Value?.ToString());

    [Fact]
    public async Task Stage_and_unstage_modified_deleted_and_new_files()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"), ("gone.txt", "g\n"));
        repo.WriteFile("a.txt", "a2\n").DeleteFile("gone.txt").WriteFile("dir/new [1] é.txt", "n\n");
        string[] paths = ["a.txt", "gone.txt", "dir/new [1] é.txt"];

        AssertDone(await _git.StageAsync(repo.Path, paths, Ct));
        var staged = await StatusAsync(repo);
        Assert.All(staged.Entries, e => Assert.True(e is { IsStaged: true, IsUnstaged: false }));
        Assert.Equal(FileChangeKind.Deleted, staged.Entries.Single(e => e.Path == "gone.txt").IndexChange);

        AssertDone(await _git.UnstageAsync(repo.Path, paths, Ct));
        var unstaged = await StatusAsync(repo);
        Assert.All(unstaged.Entries, e => Assert.True(e is { IsStaged: false, IsUnstaged: true }));
        Assert.True(unstaged.Entries.Single(e => e.Path == "dir/new [1] é.txt").IsUntracked);
    }

    [Fact]
    public async Task Stage_all_and_unstage_before_the_first_commit()
    {
        using var repo = TestRepoBuilder.Create();
        repo.WriteFile("a.txt", "a\n").WriteFile("sub/b.txt", "b\n");

        AssertDone(await _git.StageAllAsync(Path.Combine(repo.Path, "sub"), Ct));
        Assert.All((await StatusAsync(repo)).Entries, e => Assert.Equal(FileChangeKind.Added, e.IndexChange));

        AssertDone(await _git.UnstageAsync(repo.Path, ["a.txt", "sub/b.txt"], Ct));
        Assert.All((await StatusAsync(repo)).Entries, e => Assert.True(e.IsUntracked));
    }

    [Fact]
    public async Task Discard_restores_tracked_files_from_the_index_and_deletes_untracked_ones()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n"), ("b.txt", "b\n"));
        repo.WriteFile("a.txt", "2\n").Stage("a.txt").WriteFile("a.txt", "3\n"); // staged 2, unstaged 3
        repo.DeleteFile("b.txt");
        repo.WriteFile("new/deep/c.txt", "c\n");
        var entries = (await StatusAsync(repo)).Entries;

        AssertDone(await _git.DiscardAsync(repo.Path, entries, Ct));

        Assert.Equal("2\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(repo.Path, "new")));
        var a = Assert.Single((await StatusAsync(repo)).Entries);
        Assert.Equal(("a.txt", FileChangeKind.Modified, (FileChangeKind?)null), (a.Path, a.IndexChange, a.WorkTreeChange));
    }

    [Fact]
    public async Task Discarding_a_nested_repository_fails_instead_of_reporting_success()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n"));
        repo.Git("init", "sub");
        repo.WriteFile("sub/x.txt", "x\n");
        var entries = (await StatusAsync(repo)).Entries;
        Assert.Contains(entries, e => e.IsUntracked && e.Path.StartsWith("sub", StringComparison.Ordinal));

        Assert.IsType<Failure>((await _git.DiscardAsync(repo.Path, entries, Ct)).Value);
        Assert.True(File.Exists(Path.Combine(repo.Path, "sub", "x.txt")));
    }

    [Fact]
    public async Task Selected_lines_are_staged_alone()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n2\n3\n4\n5\n6\n7\n8\n9\n"));
        repo.WriteFile("a.txt", "1\nTWO\n3\n4\n5\n6\n7\nEIGHT\n9\n");
        var diff = await DiffAsync(repo, "a.txt", WorkingDiffSide.Unstaged);
        var hunk = Assert.Single(diff.Hunks);
        var added = hunk.Lines.ToList().FindIndex(l => l is { Kind: DiffLineKind.Added, Text: "TWO" });
        var deleted = hunk.Lines.ToList().FindIndex(l => l is { Kind: DiffLineKind.Deleted, Text: "2" });

        var patch = PatchBuilder.Build(diff, new HashSet<DiffLineRef> { new(0, added), new(0, deleted) }, reverse: false);
        AssertDone(await _git.ApplyPatchAsync(repo.Path, patch!, PatchTarget.Index, reverse: false, Ct));

        Assert.Equal("1\nTWO\n3\n4\n5\n6\n7\n8\n9\n", repo.Git("show", ":a.txt"));
        Assert.True((await EntryAsync(repo, "a.txt")).IsPartiallyStaged);
    }

    [Fact]
    public async Task Staging_only_an_added_line_after_a_last_line_without_newline_keeps_the_lines_apart()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "last"));
        repo.WriteFile("a.txt", "last\nnew\n");
        var diff = await DiffAsync(repo, "a.txt", WorkingDiffSide.Unstaged);
        var added = Assert.Single(diff.Hunks).Lines.ToList().FindIndex(l => l is { Kind: DiffLineKind.Added, Text: "new" });

        var patch = PatchBuilder.Build(diff, new HashSet<DiffLineRef> { new(0, added) }, reverse: false);
        AssertDone(await _git.ApplyPatchAsync(repo.Path, patch!, PatchTarget.Index, reverse: false, Ct));

        Assert.Equal("last\nnew\n", repo.Git("show", ":a.txt"));
    }

    [Fact]
    public async Task Selected_staged_lines_are_unstaged_and_unstaged_lines_discarded()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n2\n3\n4\n5\n6\n7\n8\n9\n"));
        repo.WriteFile("a.txt", "1\nTWO\n3\n4\n5\n6\n7\nEIGHT\n9\n").Stage("a.txt");

        // Unstage the first change only (reverse patch on the index).
        var staged = await DiffAsync(repo, "a.txt", WorkingDiffSide.Staged);
        var first = PatchBuilder.ChangedLines(staged, 0).Where(r => staged.Hunks[0].Lines[r.Line].Text is "2" or "TWO").ToHashSet();
        AssertDone(await _git.ApplyPatchAsync(repo.Path, PatchBuilder.Build(staged, first, reverse: true)!, PatchTarget.Index, reverse: true, Ct));
        Assert.Equal("1\n2\n3\n4\n5\n6\n7\nEIGHT\n9\n", repo.Git("show", ":a.txt"));

        // Discard that change from the working tree (reverse patch of the unstaged diff).
        var unstaged = await DiffAsync(repo, "a.txt", WorkingDiffSide.Unstaged);
        var all = PatchBuilder.ChangedLines(unstaged, 0).ToHashSet();
        AssertDone(await _git.ApplyPatchAsync(repo.Path, PatchBuilder.Build(unstaged, all, reverse: true)!, PatchTarget.WorkingTree, reverse: true, Ct));
        Assert.Equal("1\n2\n3\n4\n5\n6\n7\nEIGHT\n9\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [Fact]
    public async Task One_hunk_of_several_is_staged()
    {
        using var repo = TestRepoBuilder.Create();
        var lines = Enumerable.Range(1, 30).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        repo.Commit("init", ("a.txt", string.Join('\n', lines) + "\n"));
        lines[1] = "two";
        lines[25] = "twenty-six";
        repo.WriteFile("a.txt", string.Join('\n', lines) + "\n");
        var diff = await DiffAsync(repo, "a.txt", WorkingDiffSide.Unstaged);
        Assert.Equal(2, diff.Hunks.Count);

        var patch = PatchBuilder.Build(diff, PatchBuilder.ChangedLines(diff, 1).ToHashSet(), reverse: false);
        AssertDone(await _git.ApplyPatchAsync(repo.Path, patch!, PatchTarget.Index, reverse: false, Ct));

        var index = repo.Git("show", ":a.txt");
        Assert.Contains("twenty-six", index, StringComparison.Ordinal);
        Assert.DoesNotContain("two\n", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crlf_file_lines_are_staged_byte_for_byte()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\r\n2\r\n3\r\n4\r\n"));
        repo.WriteFile("a.txt", "1\r\nTWO\r\n3\r\nFOUR\r\n");
        var diff = await DiffAsync(repo, "a.txt", WorkingDiffSide.Unstaged);
        var selected = PatchBuilder.ChangedLines(diff, 0).Where(r => diff.Hunks[0].Lines[r.Line].Text is "2" or "TWO").ToHashSet();

        AssertDone(await _git.ApplyPatchAsync(repo.Path, PatchBuilder.Build(diff, selected, reverse: false)!, PatchTarget.Index, reverse: false, Ct));

        Assert.Equal("1\r\nTWO\r\n3\r\n4\r\n", repo.Git("show", ":a.txt"));
    }

    [Fact]
    public async Task Lines_are_discarded_with_autocrlf_conversion()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Git("config", "core.autocrlf", "true");
        repo.Commit("init", ("a.txt", "1\n2\n3\n4\n"));
        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "1\r\nTWO\r\n3\r\nFOUR\r\n", new UTF8Encoding(false));
        var diff = await DiffAsync(repo, "a.txt", WorkingDiffSide.Unstaged);
        var selected = PatchBuilder.ChangedLines(diff, 0).Where(r => diff.Hunks[0].Lines[r.Line].Text is "4" or "FOUR").ToHashSet();

        AssertDone(await _git.ApplyPatchAsync(repo.Path, PatchBuilder.Build(diff, selected, reverse: true)!, PatchTarget.WorkingTree, reverse: true, Ct));

        Assert.Equal("1\r\nTWO\r\n3\r\n4\r\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [Fact]
    public async Task Invalid_patch_is_a_failure()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));

        var result = await _git.ApplyPatchAsync(repo.Path, "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-zzz\n+yyy\n",
            PatchTarget.Index, reverse: false, Ct);

        Assert.IsType<Failure>(result.Value);
    }
}
