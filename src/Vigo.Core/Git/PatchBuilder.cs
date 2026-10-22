using System.Text;
using Vigo.Core.Models;

namespace Vigo.Core.Git;

/// <summary>Identifies a line of a <see cref="FileDiff"/>: hunk index and line index within the hunk.</summary>
public readonly record struct DiffLineRef(int Hunk, int Line);

/// <summary>
/// Builds a partial patch from a <see cref="FileDiff"/> keeping only some of its changed lines, for
/// <c>git apply --recount</c> (the same editing rules as <c>git add -p</c>).
/// </summary>
/// <remarks>
/// <para>Forward patch (stage lines of the unstaged diff, applied to the index): an unselected added line is dropped
/// and an unselected deleted line becomes context, so the patch applies to the old side as it is.</para>
/// <para>Reverse patch (unstage lines of the staged diff, or discard lines of the unstaged diff, applied with
/// <c>--reverse</c>): an unselected added line becomes context and an unselected deleted line is dropped, so the
/// patch matches the new side, which is what the target currently contains.</para>
/// <para>Line endings are written as read (<see cref="DiffLine.EndsWithCarriageReturn"/>), so CRLF files apply.</para>
/// </remarks>
public static class PatchBuilder
{
    /// <summary>Lines of <paramref name="hunk"/> that can be selected (added or deleted).</summary>
    public static IEnumerable<DiffLineRef> ChangedLines(FileDiff diff, int hunk) =>
        diff.Hunks[hunk].Lines.Select((line, index) => (line, index))
            .Where(l => l.line.IsChange)
            .Select(l => new DiffLineRef(hunk, l.index));

    /// <summary>
    /// Patch of the selected lines, or null when the selection contains no change. The file must be a plain
    /// modification (same path on both sides, not binary).
    /// </summary>
    public static string? Build(FileDiff diff, IReadOnlySet<DiffLineRef> selected, bool reverse)
    {
        var body = new StringBuilder();
        var delta = 0;
        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            var lines = new StringBuilder();
            int oldCount = 0, newCount = 0, changes = 0;
            var previousKept = false;
            if (!reverse) selected = WithNewlineRewrite(hunk, h, selected);

            for (var l = 0; l < hunk.Lines.Count; l++)
            {
                var line = hunk.Lines[l];
                char? sign = line.Kind switch
                {
                    DiffLineKind.Context => ' ',
                    DiffLineKind.Added when selected.Contains(new(h, l)) => '+',
                    DiffLineKind.Deleted when selected.Contains(new(h, l)) => '-',
                    DiffLineKind.Added => reverse ? ' ' : null,
                    DiffLineKind.Deleted => reverse ? null : ' ',
                    _ => null,
                };

                if (line.Kind == DiffLineKind.NoNewline)
                {
                    // Belongs to the line above: kept only with it.
                    if (previousKept) lines.Append("\\ ").Append(line.Text).Append('\n');
                    continue;
                }

                previousKept = sign is not null;
                if (sign is not { } s) continue;
                if (s != ' ') changes++;
                if (s != '+') oldCount++;
                if (s != '-') newCount++;
                lines.Append(s).Append(line.Text);
                if (line.EndsWithCarriageReturn) lines.Append('\r');
                lines.Append('\n');
            }

            if (changes == 0) continue;

            // Start lines are hints (git apply looks for the context); counts are recomputed by --recount anyway.
            int oldStart, newStart;
            if (reverse)
            {
                newStart = hunk.NewStart;
                oldStart = newStart - delta;
            }
            else
            {
                oldStart = hunk.OldStart;
                newStart = oldStart + delta;
            }
            if (oldCount > 0 && oldStart == 0) oldStart = 1;
            if (newCount > 0 && newStart == 0) newStart = 1;
            delta += newCount - oldCount;

            body.Append(FormattableString.Invariant($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n")).Append(lines);
        }

        if (body.Length == 0) return null;

        string a = Quote("a/" + diff.Path), b = Quote("b/" + diff.Path);
        return $"diff --git {a} {b}\n--- {a}\n+++ {b}\n{body}";
    }

    /// <summary>
    /// Forward patch only: when the old side lacks its final newline ("-last", marker, "+last") and a line is added after
    /// it, keeping "last" as context would glue the new line to it ("lastnew"). As <c>git add -p</c> does, the rewrite of
    /// that last line (deletion, marker, first added line) is then selected along with the added line.
    /// </summary>
    private static IReadOnlySet<DiffLineRef> WithNewlineRewrite(DiffHunk hunk, int h, IReadOnlySet<DiffLineRef> selected)
    {
        HashSet<DiffLineRef>? extended = null;
        for (var l = 0; l + 1 < hunk.Lines.Count; l++)
        {
            if (hunk.Lines[l].Kind != DiffLineKind.Deleted || hunk.Lines[l + 1].Kind != DiffLineKind.NoNewline) continue;
            var firstAdded = -1;
            var addedSelected = false;
            for (var n = l + 2; n < hunk.Lines.Count; n++)
            {
                if (hunk.Lines[n].Kind != DiffLineKind.Added) continue;
                if (firstAdded < 0) firstAdded = n;
                addedSelected |= selected.Contains(new(h, n));
            }
            if (firstAdded < 0 || !addedSelected) continue;
            extended ??= [.. selected];
            extended.Add(new(h, l));
            extended.Add(new(h, firstAdded));
        }
        return extended ?? selected;
    }

    /// <summary>C-style quoting of a prefixed path, as git does, for names it could not read otherwise.</summary>
    private static string Quote(string path)
    {
        if (path.IndexOfAny(['"', '\\', '\t', '\n']) < 0) return path;
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\t' => "\\t",
                '\n' => "\\n",
                _ => c.ToString(),
            });
        }
        return quoted.Append('"').ToString();
    }
}
