using Vigo.Core.Models;

namespace Vigo.Core.Tests;

/// <summary>Line diff of two whole texts, printed as unified hunks (J6.9g).</summary>
public sealed class TextDiffTests
{
    [Fact]
    public void Identical_texts_give_no_hunk()
    {
        var result = TextDiff.Compare("a\nb\n", "a\nb\n");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Patch);
        Assert.Equal((0, 0), (result.Additions, result.Deletions));
    }

    [Fact]
    public void A_change_is_printed_with_three_lines_of_context()
    {
        var result = TextDiff.Compare("1\n2\n3\n4\n5\n6\n7\n8\n9\n", "1\n2\n3\n4\nfive\n6\n7\n8\n9\n");

        Assert.NotNull(result);
        Assert.Equal("@@ -2,7 +2,7 @@\n 2\n 3\n 4\n-5\n+five\n 6\n 7\n 8\n", result.Patch);
        Assert.Equal((1, 1), (result.Additions, result.Deletions));
    }

    [Fact]
    public void Distant_changes_make_two_hunks_and_close_ones_make_one()
    {
        var old = string.Join('\n', Enumerable.Range(1, 30)) + "\n";
        var far = TextDiff.Compare(old, old.Replace("\n3\n", "\nx\n").Replace("\n28\n", "\ny\n"));
        var near = TextDiff.Compare(old, old.Replace("\n3\n", "\nx\n").Replace("\n8\n", "\ny\n"));

        Assert.Equal(2, far!.Patch.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Single(near!.Patch.Split('\n'), l => l.StartsWith("@@", StringComparison.Ordinal));
    }

    [Fact]
    public void An_added_file_and_a_deleted_file_are_whole_hunks()
    {
        var added = TextDiff.Compare(string.Empty, "a\nb\n");
        var deleted = TextDiff.Compare("a\nb\n", string.Empty);

        Assert.Equal("@@ -0,0 +1,2 @@\n+a\n+b\n", added!.Patch);
        Assert.Equal("@@ -1,2 +0,0 @@\n-a\n-b\n", deleted!.Patch);
    }

    [Fact]
    public void Line_endings_and_a_missing_final_one_are_not_changes()
    {
        var result = TextDiff.Compare("a\r\nb\r\n", "a\nb");

        Assert.Equal(string.Empty, result!.Patch);
    }

    [Fact]
    public void The_hunks_parse_back_and_rebuild_the_new_text()
    {
        var old = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\neleven\ntwelve\n";
        var current = "one\nTWO\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\ntwelve\nthirteen\n";

        var result = TextDiff.Compare(old, current)!;
        var diff = UnifiedDiffParser.Parse(result.Patch, "f.txt", null);

        Assert.Equal((result.Additions, result.Deletions), (diff.Additions, diff.Deletions));
        Assert.Equal(2, diff.Hunks.Count);
        Assert.Equal("TWO", diff.Hunks[0].Lines.Single(l => l.Kind == DiffLineKind.Added).Text);
        Assert.Equal(2, diff.Hunks[0].Lines.Single(l => l.Kind == DiffLineKind.Added).NewNumber);
        Assert.Equal(12, diff.Hunks[1].Lines.Single(l => l.Kind == DiffLineKind.Added).NewNumber);
    }

    [Fact]
    public void Texts_with_nothing_in_common_beyond_the_limit_give_no_diff()
    {
        var a = string.Join('\n', Enumerable.Range(0, 5_000).Select(i => "a" + i));
        var b = string.Join('\n', Enumerable.Range(0, 5_000).Select(i => "b" + i));

        Assert.Null(TextDiff.Compare(a, b));
    }
}
