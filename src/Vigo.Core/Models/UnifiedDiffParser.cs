using System.Globalization;

namespace Vigo.Core.Models;

/// <summary>
/// Parses the unified diff of a single file printed by <c>git diff</c> / <c>git diff-tree -p</c>, or the bare hunks a hosting
/// service gives as the patch of a file.
/// </summary>
public static class UnifiedDiffParser
{
    public static FileDiff Parse(string output, string path, string? oldPath)
    {
        var hunks = new List<DiffHunk>();
        var isBinary = false;
        List<DiffLine>? lines = null;
        (string Header, int OldStart, int OldCount, int NewStart, int NewCount) hunk = (string.Empty, 0, 0, 0, 0);
        int oldLine = 0, newLine = 0;

        foreach (var raw in output.Split('\n'))
        {
            // CRLF files: git keeps the \r inside the line; it is not part of the displayed text.
            // Kept on the line (not displayed) so that partial patches can be rebuilt byte for byte.
            var cr = raw.EndsWith('\r');
            var line = cr ? raw[..^1] : raw;

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                hunk = ParseHeader(line);
                oldLine = hunk.OldStart;
                newLine = hunk.NewStart;
                lines = [];
                continue;
            }

            if (lines is null)
            {
                // File header (diff --git, index, ---, +++, rename from…).
                if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                    isBinary = true;
                continue;
            }

            if (line.Length == 0)
            {
                // Only the trailing newline of the output; an empty context line still has its leading space.
                continue;
            }

            switch (line[0])
            {
                case ' ':
                    lines.Add(new DiffLine(DiffLineKind.Context, oldLine++, newLine++, line[1..], cr));
                    break;
                case '+':
                    lines.Add(new DiffLine(DiffLineKind.Added, null, newLine++, line[1..], cr));
                    break;
                case '-':
                    lines.Add(new DiffLine(DiffLineKind.Deleted, oldLine++, null, line[1..], cr));
                    break;
                case '\\':
                    lines.Add(new DiffLine(DiffLineKind.NoNewline, null, null, line[1..].TrimStart()));
                    break;
                default:
                    // Next file header: only one file is expected, stop here.
                    Flush();
                    return new FileDiff(path, oldPath, isBinary, hunks);
            }
        }

        Flush();
        return new FileDiff(path, oldPath, isBinary, hunks);

        void Flush()
        {
            if (lines is null) return;
            hunks.Add(new DiffHunk(hunk.Header, hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount, lines));
            lines = null;
        }
    }

    /// <summary>Parses <c>@@ -a[,b] +c[,d] @@ section</c>; a missing count means 1.</summary>
    private static (string Header, int OldStart, int OldCount, int NewStart, int NewCount) ParseHeader(string line)
    {
        var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
        if (end < 0) throw new FormatException($"Invalid hunk header: '{line}'.");

        var ranges = line[2..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ranges.Length != 2 || ranges[0][0] != '-' || ranges[1][0] != '+')
            throw new FormatException($"Invalid hunk header: '{line}'.");

        var (oldStart, oldCount) = ParseRange(ranges[0][1..]);
        var (newStart, newCount) = ParseRange(ranges[1][1..]);
        return (line, oldStart, oldCount, newStart, newCount);
    }

    private static (int Start, int Count) ParseRange(string range)
    {
        var comma = range.IndexOf(',');
        return comma < 0
            ? (int.Parse(range, CultureInfo.InvariantCulture), 1)
            : (int.Parse(range[..comma], CultureInfo.InvariantCulture), int.Parse(range[(comma + 1)..], CultureInfo.InvariantCulture));
    }
}
