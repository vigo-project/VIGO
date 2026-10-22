namespace Vigo.Core.Models;

/// <summary>A recently updated local branch of the summary page (J7.13).</summary>
/// <param name="Ahead">Commits ahead of the upstream; null without an upstream (or when it is gone).</param>
public sealed record SummaryBranch(string Name, GitSha Sha, DateTimeOffset Date, string Subject, string Author, int? Ahead, int? Behind);

/// <summary>A recent tag of the summary page (J7.13); <see cref="Target"/> is the commit it points to (annotated tags peeled).</summary>
public sealed record SummaryTag(string Name, GitSha Target, DateTimeOffset Date);

/// <summary>A recent commit of HEAD on the summary page (J7.13).</summary>
public sealed record SummaryCommit(GitSha Sha, string Subject, string Author, DateTimeOffset Date);

/// <summary>The README found at the root of the working directory (J7.13).</summary>
/// <param name="IsTruncated">The file is larger than <see cref="RepositorySummary.MaxReadmeBytes"/>: only its beginning is kept.</param>
public sealed record SummaryReadme(string FileName, string Text, bool IsTruncated);

/// <summary>
/// What the "Résumé" page shows (J7.13, ADR-080): totals, the five most recent branches, tags and commits, and the README.
/// An empty repository (no commit) has zero commits and empty lists.
/// </summary>
public sealed record RepositorySummary(
    int CommitCount,
    int BranchCount,
    int TagCount,
    IReadOnlyList<SummaryBranch> Branches,
    IReadOnlyList<SummaryTag> Tags,
    IReadOnlyList<SummaryCommit> Commits,
    SummaryReadme? Readme)
{
    /// <summary>Entries of each list.</summary>
    public const int ListSize = 5;

    /// <summary>Largest README read (512 KB).</summary>
    public const int MaxReadmeBytes = 512 * 1024;

    /// <summary>README names looked for at the root of the working directory, in this order, ignoring the case.</summary>
    public static readonly string[] ReadmeNames = ["README.md", "README.markdown", "README"];
}
