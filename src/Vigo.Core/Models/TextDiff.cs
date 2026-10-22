using System.Globalization;
using System.Text;

namespace Vigo.Core.Models;

/// <summary>
/// Line diff of two whole texts (Myers, O(ND)) printed as the bare hunks of a unified diff, for a hosting service that gives
/// the versions of a file but no patch. The result goes through <see cref="UnifiedDiffParser"/> like the patch of any other service.
/// </summary>
public static class TextDiff
{
    private const int ContextLines = 3;

    // Beyond this edit distance the diff is not worth its memory (the trace costs O(D²)): the caller shows no diff.
    private const int MaxEditDistance = 4_000;

    private enum Op
    {
        Equal,
        Delete,
        Insert,
    }

    /// <summary>Counts of added and deleted lines, and the hunks; null when the texts are too different to diff.</summary>
    public sealed record Result(string Patch, int Additions, int Deletions);

    public static Result? Compare(string original, string modified)
    {
        var a = Lines(original);
        var b = Lines(modified);

        // Trimming the common head and tail keeps the search to what changed.
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;

        var middle = Edits(a[head..^tail], b[head..^tail]);
        if (middle is null) return null;

        var ops = new List<(Op Op, string Text)>(head + middle.Count + tail);
        for (var i = 0; i < head; i++) ops.Add((Op.Equal, a[i]));
        ops.AddRange(middle);
        for (var i = a.Length - tail; i < a.Length; i++) ops.Add((Op.Equal, a[i]));

        return Print(ops);
    }

    // A trailing line break ends the last line: it does not start an empty one.
    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Split('\n');
        var count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var result = new string[count];
        for (var i = 0; i < count; i++) result[i] = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
        return result;
    }

    private static List<(Op Op, string Text)>? Edits(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 && m == 0) return [];

        var max = n + m;
        var offset = max + 1;
        var v = new int[(2 * max) + 3];
        var trace = new List<int[]>();

        for (var d = 0; d <= Math.Min(max, MaxEditDistance); d++)
        {
            // State before this round, for the walk back: diagonals -d-1 … d+1.
            var snapshot = new int[(2 * d) + 3];
            Array.Copy(v, offset - d - 1, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);

            for (var k = -d; k <= d; k += 2)
            {
                var down = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]);
                var x = down ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(a, b, trace);
            }
        }
        return null;
    }

    private static List<(Op Op, string Text)> Backtrack(string[] a, string[] b, List<int[]> trace)
    {
        var edits = new List<(Op, string)>();
        int x = a.Length, y = b.Length;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var before = trace[d];
            int At(int k) => before[k + d + 1];

            var k = x - y;
            var down = k == -d || (k != d && At(k - 1) < At(k + 1));
            var previousK = down ? k + 1 : k - 1;
            var previousX = At(previousK);
            var previousY = previousX - previousK;

            while (x > previousX && y > previousY)
            {
                edits.Add((Op.Equal, a[x - 1]));
                x--;
                y--;
            }
            if (d == 0) break;
            if (x == previousX)
            {
                edits.Add((Op.Insert, b[y - 1]));
                y--;
            }
            else
            {
                edits.Add((Op.Delete, a[x - 1]));
                x--;
            }
        }
        edits.Reverse();
        return edits;
    }

    private static Result Print(List<(Op Op, string Text)> ops)
    {
        var text = new StringBuilder();
        int additions = 0, deletions = 0;

        // Position (0-based line counts consumed so far) at the start of each operation.
        var oldAt = new int[ops.Count + 1];
        var newAt = new int[ops.Count + 1];
        for (var i = 0; i < ops.Count; i++)
        {
            oldAt[i + 1] = oldAt[i] + (ops[i].Op == Op.Insert ? 0 : 1);
            newAt[i + 1] = newAt[i] + (ops[i].Op == Op.Delete ? 0 : 1);
            if (ops[i].Op == Op.Insert) additions++;
            else if (ops[i].Op == Op.Delete) deletions++;
        }

        var index = 0;
        while (index < ops.Count)
        {
            while (index < ops.Count && ops[index].Op == Op.Equal) index++;
            if (index == ops.Count) break;

            // One hunk: from the context before the first change, through changes closer than twice the context.
            var start = Math.Max(0, index - ContextLines);
            var end = index;
            while (true)
            {
                while (end < ops.Count && ops[end].Op != Op.Equal) end++;
                var nextChange = end;
                while (nextChange < ops.Count && ops[nextChange].Op == Op.Equal) nextChange++;
                if (nextChange < ops.Count && nextChange - end <= 2 * ContextLines) end = nextChange;
                else break;
            }
            var stop = Math.Min(ops.Count, end + ContextLines);

            var oldCount = oldAt[stop] - oldAt[start];
            var newCount = newAt[stop] - newAt[start];
            // "-5,0" / "+5,0" (nothing on that side) names the line before the insertion point, as git does.
            var oldStart = oldCount == 0 ? oldAt[start] : oldAt[start] + 1;
            var newStart = newCount == 0 ? newAt[start] : newAt[start] + 1;
            text.Append(CultureInfo.InvariantCulture, $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
            for (var i = start; i < stop; i++)
            {
                text.Append(ops[i].Op switch { Op.Delete => '-', Op.Insert => '+', _ => ' ' }).Append(ops[i].Text).Append('\n');
            }
            index = stop;
        }
        return new Result(text.ToString(), additions, deletions);
    }
}
