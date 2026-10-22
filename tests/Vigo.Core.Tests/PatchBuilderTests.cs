using Vigo.Core.Git;
using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class PatchBuilderTests
{
    // @@ -1,4 +1,4 @@ : 1, -2, +TWO, 3, -4, +FOUR
    private static FileDiff Sample(bool crlf = false, string path = "a.txt") => new(path, null, false,
    [
        new DiffHunk("@@ -1,4 +1,4 @@", 1, 4, 1, 4,
        [
            new DiffLine(DiffLineKind.Context, 1, 1, "1", crlf),
            new DiffLine(DiffLineKind.Deleted, 2, null, "2", crlf),
            new DiffLine(DiffLineKind.Added, null, 2, "TWO", crlf),
            new DiffLine(DiffLineKind.Context, 3, 3, "3", crlf),
            new DiffLine(DiffLineKind.Deleted, 4, null, "4", crlf),
            new DiffLine(DiffLineKind.Added, null, 4, "FOUR", crlf),
        ]),
    ]);

    [Fact]
    public void Forward_patch_drops_unselected_additions_and_keeps_unselected_deletions_as_context()
    {
        var patch = PatchBuilder.Build(Sample(), new HashSet<DiffLineRef> { new(0, 1), new(0, 2) }, reverse: false);

        Assert.Equal(
            "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n" +
            "@@ -1,4 +1,4 @@\n 1\n-2\n+TWO\n 3\n 4\n",
            patch);
    }

    [Fact]
    public void Reverse_patch_drops_unselected_deletions_and_keeps_unselected_additions_as_context()
    {
        var patch = PatchBuilder.Build(Sample(), new HashSet<DiffLineRef> { new(0, 4), new(0, 5) }, reverse: true);

        Assert.Equal(
            "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n" +
            "@@ -1,4 +1,4 @@\n 1\n TWO\n 3\n-4\n+FOUR\n",
            patch);
    }

    [Fact]
    public void Carriage_returns_are_written_back()
    {
        var patch = PatchBuilder.Build(Sample(crlf: true), new HashSet<DiffLineRef> { new(0, 2) }, reverse: false);

        Assert.Contains("+TWO\r\n", patch, StringComparison.Ordinal);
        Assert.Contains(" 2\r\n", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void No_change_selected_gives_no_patch()
    {
        Assert.Null(PatchBuilder.Build(Sample(), new HashSet<DiffLineRef> { new(0, 0) }, reverse: false));
        Assert.Null(PatchBuilder.Build(Sample(), new HashSet<DiffLineRef>(), reverse: true));
    }

    [Fact]
    public void Missing_newline_marker_follows_its_line_only()
    {
        var diff = new FileDiff("a.txt", null, false,
        [
            new DiffHunk("@@ -1 +1 @@", 1, 1, 1, 1,
            [
                new DiffLine(DiffLineKind.Deleted, 1, null, "old"),
                new DiffLine(DiffLineKind.NoNewline, null, null, "No newline at end of file"),
                new DiffLine(DiffLineKind.Added, null, 1, "new"),
            ]),
        ]);

        // Forward, only the addition: the deletion is selected with it (as git add -p does), else "old" and "new" would be glued together.
        Assert.EndsWith("-old\n\\ No newline at end of file\n+new\n",PatchBuilder.Build(diff, new HashSet<DiffLineRef> { new(0, 2) }, reverse: false));
        // Reverse, only the addition: the deletion is dropped with its marker.
        Assert.EndsWith("@@ -1,0 +1,1 @@\n+new\n", PatchBuilder.Build(diff, new HashSet<DiffLineRef> { new(0, 2) }, reverse: true));
    }

    [Fact]
    public void Later_hunks_get_shifted_start_lines()
    {
        var diff = new FileDiff("a.txt", null, false,
        [
            new DiffHunk("@@ -1,2 +1,3 @@", 1, 2, 1, 3,
                [new DiffLine(DiffLineKind.Context, 1, 1, "a"), new DiffLine(DiffLineKind.Added, null, 2, "b"), new DiffLine(DiffLineKind.Context, 2, 3, "c")]),
            new DiffHunk("@@ -10,2 +11,2 @@", 10, 2, 11, 2,
                [new DiffLine(DiffLineKind.Deleted, 10, null, "x"), new DiffLine(DiffLineKind.Added, null, 11, "y"), new DiffLine(DiffLineKind.Context, 11, 12, "z")]),
        ]);
        HashSet<DiffLineRef> all = [.. PatchBuilder.ChangedLines(diff, 0), .. PatchBuilder.ChangedLines(diff, 1)];

        var forward = PatchBuilder.Build(diff, all, reverse: false);

        Assert.Contains("@@ -1,2 +1,3 @@", forward, StringComparison.Ordinal);
        Assert.Contains("@@ -10,2 +11,2 @@", forward, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_with_quotes_are_c_quoted()
    {
        var patch = PatchBuilder.Build(Sample(path: "we\"ird.txt"), new HashSet<DiffLineRef> { new(0, 2) }, reverse: false);

        Assert.StartsWith("diff --git \"a/we\\\"ird.txt\" \"b/we\\\"ird.txt\"\n--- \"a/we\\\"ird.txt\"\n", patch);
    }
}
