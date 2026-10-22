using Vigo.Core.Models;

namespace Vigo.Git.Conflicts;

/// <summary>One index stage of an unmerged path (<c>git ls-files -u -z</c>).</summary>
/// <param name="Stage">1: merge base, 2: ours, 3: theirs.</param>
/// <param name="Mode">Octal file mode ("100644", "120000" for a symbolic link, "160000" for a submodule).</param>
internal sealed record IndexStage(int Stage, string Mode, string ObjectId)
{
    /// <summary>A regular file (not a symbolic link or a submodule): its content can be merged line by line.</summary>
    public bool IsRegularFile => Mode is "100644" or "100755";
}

/// <summary>Parses what git prints about a conflicted file: its index stages and the text merged by <c>git merge-file</c>.</summary>
internal static class ConflictReader
{
    /// <summary>
    /// Length of the conflict markers asked of <c>git merge-file</c>: longer than git's 7, so that a line of the file
    /// that looks like a marker ("=======" under a Markdown title) is never taken for one.
    /// </summary>
    public const int MarkerSize = 31;

    /// <summary>Stages of <paramref name="path"/> in <c>git ls-files -u -z</c> output: "&lt;mode&gt; &lt;oid&gt; &lt;stage&gt;\t&lt;path&gt;\0".</summary>
    public static IReadOnlyList<IndexStage> ParseStages(string output, string path)
    {
        var stages = new List<IndexStage>(3);
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0 || record[(tab + 1)..] != path) continue;
            var fields = record[..tab].Split(' ');
            if (fields.Length != 3 || !int.TryParse(fields[2], System.Globalization.CultureInfo.InvariantCulture, out var stage)) continue;
            stages.Add(new IndexStage(stage, fields[0], fields[1]));
        }
        return stages;
    }

    /// <summary>
    /// Splits the output of <c>git merge-file -p --zdiff3 --marker-size=<see cref="MarkerSize"/></c> into common lines
    /// and conflicts. Lines are returned without their line ending ('\r' included).
    /// </summary>
    /// <exception cref="FormatException">A conflict is not closed by its end marker.</exception>
    public static (IReadOnlyList<ConflictSegment> Segments, bool EndsWithNewline, bool HasCrLf) Parse(string merged)
    {
        var endsWithNewline = merged.Length == 0 || merged.EndsWith('\n');
        var lines = merged.Split('\n');
        var count = endsWithNewline ? lines.Length - 1 : lines.Length;
        var crlf = 0;
        for (var i = 0; i < count; i++)
        {
            // The last line has no '\n' after it: its '\r' (if any) is content, not a line ending.
            if (lines[i].EndsWith('\r') && (i < count - 1 || endsWithNewline))
            {
                lines[i] = lines[i][..^1];
                crlf++;
            }
        }

        var segments = new List<ConflictSegment>();
        var common = new List<string>();
        List<string>? ours = null, @base = null, theirs = null;
        var current = (List<string>?)null;
        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            switch (MarkerOf(line))
            {
                case '<' when ours is null:
                    if (common.Count > 0) segments.Add(new CommonLines([.. common]));
                    common.Clear();
                    current = ours = [];
                    break;
                case '|' when ours is not null && @base is null && theirs is null:
                    current = @base = [];
                    break;
                case '=' when ours is not null && theirs is null:
                    current = theirs = [];
                    break;
                case '>' when theirs is not null:
                    segments.Add(new ConflictHunk(ours!, (IReadOnlyList<string>?)@base ?? [], theirs));
                    ours = @base = theirs = current = null;
                    break;
                default:
                    // Not a marker, or one out of sequence: content that happens to look like a marker.
                    (current ?? common).Add(line);
                    break;
            }
        }
        if (ours is not null) throw new FormatException("Unterminated conflict.");
        if (common.Count > 0) segments.Add(new CommonLines([.. common]));
        return (segments, endsWithNewline, crlf > 0 && crlf * 2 >= count);
    }

    /// <summary>'&lt;', '|', '=' or '&gt;' for a conflict marker line of <see cref="MarkerSize"/>; '\0' otherwise.</summary>
    private static char MarkerOf(string line)
    {
        if (line.Length < MarkerSize) return '\0';
        var c = line[0];
        if (c is not ('<' or '|' or '=' or '>')) return '\0';
        for (var i = 1; i < MarkerSize; i++)
        {
            if (line[i] != c) return '\0';
        }
        // "=======" stands alone; the other markers may be followed by a space and a label.
        if (line.Length == MarkerSize) return c;
        return c != '=' && line[MarkerSize] == ' ' ? c : '\0';
    }
}
