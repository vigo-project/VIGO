namespace Vigo.Core.Models;

/// <summary>Kind of a line in a unified diff.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Deleted,

    /// <summary>"\ No newline at end of file" marker, attached to the line above.</summary>
    NoNewline,
}

/// <summary>One line of a hunk.</summary>
/// <param name="OldNumber">Line number in the old file; null for an added line.</param>
/// <param name="NewNumber">Line number in the new file; null for a deleted line.</param>
/// <param name="Text">Content without the leading sign nor the line ending.</param>
/// <param name="EndsWithCarriageReturn">The line ends with CRLF in the diff (not displayed; kept to rebuild patches).</param>
public sealed record DiffLine(DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text, bool EndsWithCarriageReturn = false)
{
    public bool IsChange => Kind is DiffLineKind.Added or DiffLineKind.Deleted;
}

/// <summary>Hunk of a unified diff (<c>@@ -OldStart,OldCount +NewStart,NewCount @@ Section</c>).</summary>
/// <param name="Header">The whole <c>@@ … @@</c> line, section heading included.</param>
public sealed record DiffHunk(string Header, int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines);

/// <summary>Whole old and new texts of a changed file (side-by-side diff); empty for a side where it does not exist.</summary>
/// <param name="OldPath">Path of the old side (differs from <paramref name="Path"/> for a rename).</param>
public sealed record DiffSides(string Path, string OldPath, string Original, string Modified)
{
    /// <summary>
    /// Both sides rebuilt from the hunks alone, for a diff whose whole files are not at hand (a pull request on a hosting service).
    /// What lies between two hunks is unchanged, so it is the same number of empty lines on both sides: the lines keep their real numbers.
    /// </summary>
    public static DiffSides FromHunks(FileDiff diff)
    {
        List<string> original = [], modified = [];
        foreach (var hunk in diff.Hunks)
        {
            // "-5,0" / "+5,0" (nothing on that side) names the line before the insertion point.
            var oldStart = hunk.OldCount == 0 ? hunk.OldStart + 1 : hunk.OldStart;
            var newStart = hunk.NewCount == 0 ? hunk.NewStart + 1 : hunk.NewStart;
            while (original.Count + 1 < oldStart) original.Add(string.Empty);
            while (modified.Count + 1 < newStart) modified.Add(string.Empty);
            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case DiffLineKind.Context:
                        original.Add(line.Text);
                        modified.Add(line.Text);
                        break;
                    case DiffLineKind.Deleted:
                        original.Add(line.Text);
                        break;
                    case DiffLineKind.Added:
                        modified.Add(line.Text);
                        break;
                }
            }
        }
        return new DiffSides(diff.Path, diff.OldPath ?? diff.Path, string.Join('\n', original), string.Join('\n', modified));
    }
}

/// <summary>Unified diff of one file between a commit and its first parent.</summary>
/// <param name="IsBinary">git reported "Binary files differ": no hunk.</param>
public sealed record FileDiff(string Path, string? OldPath, bool IsBinary, IReadOnlyList<DiffHunk> Hunks)
{
    /// <summary>The file is not UTF-8 and was decoded with the ANSI code page: a patch rebuilt from this text would corrupt it.</summary>
    public bool IsLossy { get; init; }

    public int Additions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));

    public int Deletions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Deleted));
}
