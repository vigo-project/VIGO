using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;
using Vigo.Git.Commits;

namespace Vigo.Git;

/// <summary>Branch comparison: A and B classified through their merge base.</summary>
public sealed partial class GitService
{
    public async Task<GitResult<BranchComparison>> CompareBranchesAsync(
        string repositoryPath, GitSha a, GitSha b, CancellationToken cancellationToken = default)
    {
        var bases = await ReadOptionalAsync(repositoryPath, ["merge-base", a.ToString(), b.ToString()], cancellationToken);
        if (bases.Value is not Success<string?> { Value: { } text } || !GitSha.TryParse(text, out var mergeBase)) return Fail<BranchComparison>(bases, "GitError.NoMergeBase");

        // The four reads run together; an early failure cancels the others (the dry-run merge is the costly one).
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = running.Token;
        var between = NameStatusAsync(repositoryPath, a, b, token);
        var sinceA = NameStatusAsync(repositoryPath, mergeBase, a, token);
        var sinceB = NameStatusAsync(repositoryPath, mergeBase, b, token);
        var predicted = PredictConflictsAsync(repositoryPath, a, b, token);
        try
        {
            return await CompleteAsync();
        }
        finally
        {
            await running.CancelAsync();
        }

        async Task<GitResult<BranchComparison>> CompleteAsync()
        {
        if (await between is not Success<List<ChangedFile>> { Value: var ab }) return Retype<BranchComparison>(await between);
        if (await sinceA is not Success<List<ChangedFile>> { Value: var baseToA }) return Retype<BranchComparison>(await sinceA);
        if (await sinceB is not Success<List<ChangedFile>> { Value: var baseToB }) return Retype<BranchComparison>(await sinceB);

        // A failed dry run (old git) leaves the conflicts unknown; a cancellation stops everything.
        var prediction = await predicted;
        if (prediction.Value is Canceled canceled) return canceled;
        var conflicts = prediction.Value is Success<IReadOnlyList<string>> { Value: var paths } ? paths.ToHashSet(StringComparer.Ordinal) : null;

        return new Success<BranchComparison>(new BranchComparison(mergeBase, a, b, Classify(ab, baseToA, baseToB, conflicts), conflicts is not null));
        }
    }

    private async Task<GitResult<List<ChangedFile>>> NameStatusAsync(string repositoryPath, GitSha from, GitSha to, CancellationToken cancellationToken)
    {
        var diff = await Cli.RunAsync(repositoryPath, ["diff", "--name-status", "-z", "-M", from.ToString(), to.ToString(), "--"], cancellationToken);
        return diff is Success<CliOutput> s
            ? new Success<List<ChangedFile>>(CommitCliReader.ParseFiles(s.Value.StandardOutput, string.Empty))
            : Propagate<List<ChangedFile>>(diff);
    }

    // Same failure under another result type (a Success is never passed in).
    private static GitResult<T> Retype<T>(GitResult<List<ChangedFile>> result) => result.Value switch
    {
        Conflict c => c,
        AuthenticationFailed a => a,
        Canceled c => c,
        Failure f => f,
        _ => throw new InvalidOperationException("A success has no failure to pass on."),
    };

    /// <summary>
    /// A file changed on both sides is a conflict or an automatic merge (<paramref name="conflicts"/>); changed on both sides
    /// with no A↔B difference left, it is identical. Otherwise the side that changed it since the merge base tells who is ahead,
    /// except for a file that only one branch has.
    /// </summary>
    internal static List<BranchCompareEntry> Classify(
        IReadOnlyList<ChangedFile> between, IReadOnlyList<ChangedFile> baseToA, IReadOnlyList<ChangedFile> baseToB, HashSet<string>? conflicts)
    {
        static HashSet<string> Touched(IReadOnlyList<ChangedFile> files) =>
            [.. files.SelectMany(f => f.OldPath is null ? [f.Path] : new[] { f.Path, f.OldPath })];

        var touchedA = Touched(baseToA);
        var touchedB = Touched(baseToB);
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<BranchCompareEntry>();

        // A↔B reads "from A to B": an added file is only in B, a deleted one only in A (the path is then A's).
        foreach (var file in between)
        {
            var inA = touchedA.Contains(file.Path) || file.OldPath is { } o1 && touchedA.Contains(o1);
            var inB = touchedB.Contains(file.Path) || file.OldPath is { } o2 && touchedB.Contains(o2);
            shown.Add(file.Path);
            if (file.OldPath is not null) shown.Add(file.OldPath);

            var category = inA && inB ? conflicts?.Contains(file.Path) == true || file.OldPath is { } o3 && conflicts?.Contains(o3) == true
                    ? BranchCompareCategory.ModifiedBothConflicts
                    : BranchCompareCategory.ModifiedBothMerges
                : file.Kind == FileChangeKind.Deleted ? BranchCompareCategory.OnlyInA
                : file.Kind == FileChangeKind.Added ? BranchCompareCategory.OnlyInB
                : inA ? BranchCompareCategory.ModifiedInA
                : BranchCompareCategory.ModifiedInB;
            entries.Add(new BranchCompareEntry(file.Path, file.OldPath, category, file.Kind));
        }

        // Changed on both sides, nothing left between A and B: the same change was made twice.
        foreach (var file in baseToA)
        {
            if (shown.Contains(file.Path) || !touchedB.Contains(file.Path)) continue;
            entries.Add(new BranchCompareEntry(file.Path, null, BranchCompareCategory.Identical, null));
        }
        return entries;
    }
}
