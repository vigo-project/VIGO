namespace Vigo.TestUtilities;

/// <summary>
/// Deterministic random commit DAG (branches, merges, a few octopus merges), for graph tests and benchmarks.
/// Commits are identified by their creation index; rows list them newest first, which is a topological order.
/// </summary>
public sealed class SyntheticHistory
{
    private readonly int[] _parentStart;
    private readonly int[] _parents;

    private SyntheticHistory(int[] parentStart, int[] parents)
    {
        _parentStart = parentStart;
        _parents = parents;
    }

    public int Count => _parentStart.Length - 1;

    /// <summary>Id of the commit shown on a row (row 0 = newest).</summary>
    public int IdAt(int row) => Count - 1 - row;

    /// <summary>Parent ids of the commit shown on a row, first parent first.</summary>
    public ReadOnlySpan<int> ParentsAt(int row)
    {
        var id = IdAt(row);
        return _parents.AsSpan(_parentStart[id], _parentStart[id + 1] - _parentStart[id]);
    }

    /// <param name="count">Number of commits.</param>
    /// <param name="maxBranches">Maximum number of branches alive at the same time.</param>
    public static SyntheticHistory Generate(int count, int maxBranches = 30, int seed = 42)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var random = new Random(seed);
        var parentStart = new int[count + 1];
        var parents = new List<int>(count * 2);
        List<int> heads = [0];

        // Commit 0 is the root.
        parentStart[1] = 0;
        for (var id = 1; id < count; id++)
        {
            var roll = random.NextDouble();
            if (roll < 0.005 && heads.Count > 3)
            {
                // Octopus merge of three branches into the main one.
                var a = random.Next(1, heads.Count);
                var b = random.Next(1, heads.Count);
                if (a == b)
                {
                    b = a == 1 ? 2 : 1;
                }

                parents.AddRange([heads[0], heads[a], heads[b]]);
                heads[0] = id;
                heads.RemoveAt(Math.Max(a, b));
                heads.RemoveAt(Math.Min(a, b));
            }
            else if (roll < 0.08 && heads.Count > 1)
            {
                // Merge, mostly into the main branch; the merged branch usually goes away.
                var target = random.NextDouble() < 0.7 ? 0 : random.Next(heads.Count);
                var source = random.Next(heads.Count - 1);
                if (source >= target)
                {
                    source++;
                }

                parents.AddRange([heads[target], heads[source]]);
                heads[target] = id;
                if (source != 0 && random.NextDouble() < 0.8)
                {
                    heads.RemoveAt(source);
                }
            }
            else if (roll < 0.15 && heads.Count < maxBranches)
            {
                // New branch forked from an existing head.
                parents.Add(heads[random.Next(heads.Count)]);
                heads.Add(id);
            }
            else
            {
                var branch = random.Next(heads.Count);
                parents.Add(heads[branch]);
                heads[branch] = id;
            }

            parentStart[id + 1] = parents.Count;
        }

        return new SyntheticHistory(parentStart, [.. parents]);
    }
}
