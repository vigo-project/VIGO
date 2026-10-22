using Vigo.Core.Git;

namespace Vigo.Core.Models;

/// <summary>Hunk of a <see cref="DiffDocument"/>: its header is shown above <paramref name="FirstLine"/>.</summary>
/// <param name="FirstLine">First document line of the hunk (1-based, as in the code editor).</param>
public sealed record DiffDocumentHunk(int FirstLine, string Header);

/// <summary>
/// Unified diff flattened into a single text for the code editor (ADR-038): one document line per diff line, hunk
/// headers kept aside (drawn between the lines). Maps document lines back to the diff lines that staging works on.
/// </summary>
public sealed class DiffDocument
{
    /// <summary>Kind letters of <see cref="Kinds"/>.</summary>
    public const char Context = 'c', Added = 'a', Deleted = 'd', Note = 'n';

    private readonly DiffLineRef[] _refs;
    private readonly int[] _hunkStarts;

    private DiffDocument(string text, string kinds, int[] oldNumbers, int[] newNumbers, DiffDocumentHunk[] hunks, DiffLineRef[] refs)
    {
        Text = text;
        Kinds = kinds;
        OldNumbers = oldNumbers;
        NewNumbers = newNumbers;
        Hunks = hunks;
        _refs = refs;
        _hunkStarts = [.. hunks.Select(h => h.FirstLine)];
    }

    /// <summary>Lines joined by <c>\n</c>, without signs.</summary>
    public string Text { get; }

    /// <summary>One letter per document line: <see cref="Context"/>, <see cref="Added"/>, <see cref="Deleted"/>, <see cref="Note"/>.</summary>
    public string Kinds { get; }

    /// <summary>Line number in the old file per document line; 0 when there is none (added line, note).</summary>
    public IReadOnlyList<int> OldNumbers { get; }

    /// <summary>Line number in the new file per document line; 0 when there is none (deleted line, note).</summary>
    public IReadOnlyList<int> NewNumbers { get; }

    public IReadOnlyList<DiffDocumentHunk> Hunks { get; }

    public int LineCount => Kinds.Length;

    public static DiffDocument Build(FileDiff diff)
    {
        var count = diff.Hunks.Sum(h => h.Lines.Count);
        var text = new System.Text.StringBuilder();
        var kinds = new char[count];
        var oldNumbers = new int[count];
        var newNumbers = new int[count];
        var refs = new DiffLineRef[count];
        var hunks = new DiffDocumentHunk[diff.Hunks.Count];

        var index = 0;
        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            hunks[h] = new DiffDocumentHunk(index + 1, hunk.Header);
            for (var l = 0; l < hunk.Lines.Count; l++)
            {
                var line = hunk.Lines[l];
                if (index > 0) text.Append('\n');
                // A lone CR inside a line would split it in the editor and shift every line after it.
                text.Append(line.Text.Contains('\r') ? line.Text.Replace('\r', '␍') : line.Text);
                kinds[index] = line.Kind switch
                {
                    DiffLineKind.Added => Added,
                    DiffLineKind.Deleted => Deleted,
                    DiffLineKind.NoNewline => Note,
                    _ => Context,
                };
                oldNumbers[index] = line.OldNumber ?? 0;
                newNumbers[index] = line.NewNumber ?? 0;
                refs[index] = new DiffLineRef(h, l);
                index++;
            }
        }

        return new DiffDocument(text.ToString(), new string(kinds), oldNumbers, newNumbers, hunks, refs);
    }

    /// <summary>Diff line shown on a document line (1-based); null outside the document.</summary>
    public DiffLineRef? LineAt(int documentLine) =>
        documentLine >= 1 && documentLine <= _refs.Length ? _refs[documentLine - 1] : null;

    /// <summary>Document line (1-based) of a diff line; 0 when it is not in the document.</summary>
    public int DocumentLine(DiffLineRef line) =>
        line.Hunk >= 0 && line.Hunk < _hunkStarts.Length && line.Line >= 0
        && _hunkStarts[line.Hunk] + line.Line - 1 < _refs.Length
        && _refs[_hunkStarts[line.Hunk] + line.Line - 1] == line
            ? _hunkStarts[line.Hunk] + line.Line
            : 0;
}
