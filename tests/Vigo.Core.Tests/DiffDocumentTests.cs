using Vigo.Core.Git;
using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class DiffDocumentTests
{
    private static readonly FileDiff Sample = new("a.txt", null, false,
    [
        new DiffHunk("@@ -1,3 +1,3 @@", 1, 3, 1, 3,
        [
            new DiffLine(DiffLineKind.Context, 1, 1, "one"),
            new DiffLine(DiffLineKind.Deleted, 2, null, "two"),
            new DiffLine(DiffLineKind.Added, null, 2, "TWO"),
            new DiffLine(DiffLineKind.Context, 3, 3, "three"),
        ]),
        new DiffHunk("@@ -10,1 +10,2 @@ section", 10, 1, 10, 2,
        [
            new DiffLine(DiffLineKind.Context, 10, 10, "ten"),
            new DiffLine(DiffLineKind.Added, null, 11, "eleven"),
            new DiffLine(DiffLineKind.NoNewline, null, null, "No newline at end of file"),
        ]),
    ]);

    [Fact]
    public void Flattens_the_hunks_into_one_text_with_kinds_and_numbers()
    {
        var document = DiffDocument.Build(Sample);

        Assert.Equal("one\ntwo\nTWO\nthree\nten\neleven\nNo newline at end of file", document.Text);
        Assert.Equal("cdaccan", document.Kinds);
        Assert.Equal([1, 2, 0, 3, 10, 0, 0], document.OldNumbers);
        Assert.Equal([1, 0, 2, 3, 10, 11, 0], document.NewNumbers);
        Assert.Equal([new DiffDocumentHunk(1, "@@ -1,3 +1,3 @@"), new DiffDocumentHunk(5, "@@ -10,1 +10,2 @@ section")], document.Hunks);
        Assert.Equal(7, document.LineCount);
    }

    [Fact]
    public void Maps_document_lines_to_diff_lines_and_back()
    {
        var document = DiffDocument.Build(Sample);

        Assert.Equal(new DiffLineRef(0, 2), document.LineAt(3));
        Assert.Equal(new DiffLineRef(1, 1), document.LineAt(6));
        Assert.Null(document.LineAt(0));
        Assert.Null(document.LineAt(8));

        Assert.Equal(3, document.DocumentLine(new DiffLineRef(0, 2)));
        Assert.Equal(6, document.DocumentLine(new DiffLineRef(1, 1)));
        Assert.Equal(0, document.DocumentLine(new DiffLineRef(0, 4)));
        Assert.Equal(0, document.DocumentLine(new DiffLineRef(2, 0)));
    }

    [Fact]
    public void A_carriage_return_inside_a_line_does_not_split_it()
    {
        var diff = new FileDiff("a.txt", null, false,
            [new DiffHunk("@@ -1 +1 @@", 1, 1, 1, 1, [new DiffLine(DiffLineKind.Added, null, 1, "a\rb")])]);

        var document = DiffDocument.Build(diff);

        Assert.Equal("a␍b", document.Text);
        Assert.Equal(1, document.LineCount);
    }

    [Fact]
    public void An_empty_diff_gives_an_empty_document()
    {
        var document = DiffDocument.Build(new FileDiff("a.bin", null, true, []));

        Assert.Equal("", document.Text);
        Assert.Equal(0, document.LineCount);
        Assert.Empty(document.Hunks);
    }
}
