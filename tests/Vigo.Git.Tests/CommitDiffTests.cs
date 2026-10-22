using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.Git.Commits;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class CommitDiffTests
{
    private readonly GitService _sut = new(new GitCli());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<CommitDetails> Details(TestRepoBuilder repo, string sha) =>
        Assert.IsType<Success<CommitDetails>>((await _sut.GetCommitAsync(repo.Path, GitSha.Parse(sha), Ct)).Value).Value;

    private async Task<FileDiff> Diff(TestRepoBuilder repo, CommitDetails details, string path)
    {
        var file = Assert.Single(details.Files, f => f.Path == path);
        GitSha? parent = details.Parents.Count > 0 ? details.Parents[0] : null;
        var result = await _sut.GetFileDiffAsync(repo.Path, details.Sha, parent, file, Ct);
        return Assert.IsType<Success<FileDiff>>(result.Value).Value;
    }

    private static string Lines(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"line {i}\n"));

    /// <summary>Modified, added, deleted, renamed and binary files in one commit.</summary>
    private static (TestRepoBuilder Repo, string Sha) CreateChanges()
    {
        var repo = TestRepoBuilder.Create();
        repo.Commit("base",
            ("code.txt", Lines(1, 20)),
            ("old/name.txt", "same content for rename detection\nline 2\nline 3\n"),
            ("gone.txt", "a\nb\n"));
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        Directory.CreateDirectory(Path.Combine(repo.Path, "new"));
        File.Move(Path.Combine(repo.Path, "old", "name.txt"), Path.Combine(repo.Path, "new", "name.txt"));
        File.WriteAllBytes(Path.Combine(repo.Path, "image.bin"), [0, 1, 2, 0, 255, 0, 7]);
        // Line 2 replaced and line 21 appended: far enough apart for two hunks.
        var code = Lines(1, 20).Replace("line 2\n", "LINE 2\n", StringComparison.Ordinal) + "line 21\n";
        var sha = repo.Commit("changes", ("code.txt", code), ("added.txt", "x\ny\n"));
        return (repo, sha);
    }

    [Fact]
    public async Task Commit_files_carry_line_counts_and_binary_flag()
    {
        var (repo, sha) = CreateChanges();
        using var _ = repo;

        var files = (await Details(repo, sha)).Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        Assert.Equal(
            [
                new ChangedFile("added.txt", null, FileChangeKind.Added, 2, 0),
                new ChangedFile("code.txt", null, FileChangeKind.Modified, 2, 1),
                new ChangedFile("gone.txt", null, FileChangeKind.Deleted, 0, 2),
                new ChangedFile("image.bin", null, FileChangeKind.Added, 0, 0, IsBinary: true),
                new ChangedFile("new/name.txt", "old/name.txt", FileChangeKind.Renamed),
            ],
            files);
    }

    [Fact]
    public async Task Modified_file_diff_has_numbered_hunk_lines()
    {
        var (repo, sha) = CreateChanges();
        using var _ = repo;

        var diff = await Diff(repo, await Details(repo, sha), "code.txt");

        Assert.False(diff.IsBinary);
        var hunks = diff.Hunks;
        Assert.Equal(2, hunks.Count);
        Assert.StartsWith("@@ -1,5 +1,5 @@", hunks[0].Header, StringComparison.Ordinal);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Deleted, 2, null, "line 2"),
                new DiffLine(DiffLineKind.Added, null, 2, "LINE 2"),
            ],
            hunks[0].Lines.Where(l => l.Kind != DiffLineKind.Context));
        Assert.Equal(new DiffLine(DiffLineKind.Context, 1, 1, "line 1"), hunks[0].Lines[0]);
        Assert.StartsWith("@@ -18,3 +18,4 @@", hunks[1].Header, StringComparison.Ordinal);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 21, "line 21"), hunks[1].Lines[^1]);
        Assert.Equal(2, diff.Additions);
        Assert.Equal(1, diff.Deletions);
    }

    [Fact]
    public async Task Added_renamed_and_binary_files_have_their_own_shape()
    {
        var (repo, sha) = CreateChanges();
        using var _ = repo;
        var details = await Details(repo, sha);

        var added = await Diff(repo, details, "added.txt");
        Assert.Equal([new DiffLine(DiffLineKind.Added, null, 1, "x"), new DiffLine(DiffLineKind.Added, null, 2, "y")], Assert.Single(added.Hunks).Lines);

        var renamed = await Diff(repo, details, "new/name.txt");
        Assert.Equal("old/name.txt", renamed.OldPath);
        Assert.Empty(renamed.Hunks);

        var binary = await Diff(repo, details, "image.bin");
        Assert.True(binary.IsBinary);
        Assert.Empty(binary.Hunks);
    }

    [Fact]
    public async Task Root_and_merge_commits_are_compared_with_the_empty_tree_and_the_first_parent()
    {
        using var repo = TestRepoBuilder.Create();
        var root = repo.Commit("root", ("a.txt", "1\n"));
        repo.Branch("side");
        repo.Commit("side", ("b.txt", "side\n"));
        repo.Checkout("main");
        repo.Commit("main", ("a.txt", "2\n"));
        var merge = repo.Merge("side");

        var rootDiff = await Diff(repo, await Details(repo, root), "a.txt");
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 1, "1"), Assert.Single(Assert.Single(rootDiff.Hunks).Lines));

        // Against the first parent, the merge brings b.txt only.
        var mergeDetails = await Details(repo, merge);
        Assert.Equal("b.txt", Assert.Single(mergeDetails.Files).Path);
        var mergeDiff = await Diff(repo, mergeDetails, "b.txt");
        Assert.Equal("side", Assert.Single(Assert.Single(mergeDiff.Hunks).Lines).Text);
    }

    [Fact]
    public async Task Diff_of_a_file_with_glob_characters_is_not_taken_for_a_pattern()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("1.txt", "a\n"), ("[1].txt", "a\n"));
        var sha = repo.Commit("change", ("1.txt", "b\n"), ("[1].txt", "c\n"));

        var diff = await Diff(repo, await Details(repo, sha), "[1].txt");

        Assert.Equal("c", Assert.Single(diff.Hunks).Lines.Single(l => l.Kind == DiffLineKind.Added).Text);
    }

    [Fact]
    public async Task Blank_context_lines_survive_suppressBlankEmpty()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Git("config", "diff.suppressBlankEmpty", "true");
        repo.Commit("base", ("a.txt", "one\n\ntwo\n"));
        var sha = repo.Commit("change", ("a.txt", "one\n\nTWO\n"));

        var hunk = Assert.Single((await Diff(repo, await Details(repo, sha), "a.txt")).Hunks);

        Assert.Equal(new DiffLine(DiffLineKind.Context, 2, 2, ""), hunk.Lines[1]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, null, 3, "TWO"), hunk.Lines[^1]);
    }

    [Fact]
    public async Task Diff_of_a_file_that_is_not_utf8_is_lossy_and_utf8_is_not()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("utf8.txt", "café\n"));
        File.WriteAllBytes(Path.Combine(repo.Path, "latin.txt"), [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n']);
        repo.Git("add", "latin.txt");
        repo.Git("commit", "-m", "latin");
        var latin = await Details(repo, repo.Head());
        repo.Commit("utf8", ("utf8.txt", "café!\n"));

        Assert.True((await Diff(repo, latin, "latin.txt")).IsLossy);
        Assert.False((await Diff(repo, await Details(repo, repo.Head()), "utf8.txt")).IsLossy);
    }

    [Fact]
    public void Parser_handles_crlf_missing_newline_and_single_line_ranges()
    {
        const string output =
            "diff --git a/f.txt b/f.txt\nindex 1..2 100644\n--- a/f.txt\n+++ b/f.txt\n" +
            "@@ -1 +1,2 @@ class Section\n-old\r\n\\ No newline at end of file\n+new\r\n+\r\n";

        var diff = UnifiedDiffParser.Parse(output, "f.txt", null);

        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal((1, 1, 1, 2), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.Equal("@@ -1 +1,2 @@ class Section", hunk.Header);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Deleted, 1, null, "old", EndsWithCarriageReturn: true),
                new DiffLine(DiffLineKind.NoNewline, null, null, "No newline at end of file"),
                new DiffLine(DiffLineKind.Added, null, 1, "new", EndsWithCarriageReturn: true),
                new DiffLine(DiffLineKind.Added, null, 2, "", EndsWithCarriageReturn: true),
            ],
            hunk.Lines);
    }

    [Fact]
    public void Parser_rejects_a_malformed_hunk_header()
    {
        Assert.Throws<FormatException>(() => UnifiedDiffParser.Parse("@@ nonsense @@\n", "f.txt", null));
    }
}
