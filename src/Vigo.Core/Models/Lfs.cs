namespace Vigo.Core.Models;

/// <summary>A file pattern the repository stores with Git LFS (<c>git lfs track</c>).</summary>
/// <param name="Pattern">Pattern as written in the attributes file ("*.psd", "assets/**").</param>
/// <param name="Source">Attributes file that declares it (".gitattributes").</param>
public sealed record LfsPattern(string Pattern, string Source);

/// <summary>A file stored with Git LFS in the checked-out commit (<c>git lfs ls-files</c>).</summary>
/// <param name="Path">Path relative to the working directory, with forward slashes.</param>
/// <param name="ObjectId">Short object id of the LFS object.</param>
/// <param name="IsDownloaded">The content is in the working directory; false while only the pointer file is there.</param>
/// <param name="Size">Size as printed by git-lfs ("1.2 MB"); null when not shown.</param>
public sealed record LfsFile(string Path, string ObjectId, bool IsDownloaded, string? Size);

/// <summary>What a repository stores with Git LFS (J7.5, ADR-055).</summary>
/// <param name="IsUsed">An attributes file routes some patterns through the LFS filter.</param>
/// <param name="IsInstalled">The <c>git lfs</c> command is available on this machine.</param>
public sealed record LfsInfo(bool IsUsed, bool IsInstalled, IReadOnlyList<LfsPattern> Patterns, IReadOnlyList<LfsFile> Files)
{
    /// <summary>A repository that does not use LFS.</summary>
    public static LfsInfo NotUsed { get; } = new(false, true, [], []);

    /// <summary>Files whose content is missing (pointer only): <c>git lfs pull</c> downloads them.</summary>
    public int MissingCount => Files.Count(f => !f.IsDownloaded);

    public bool SameAs(LfsInfo other) =>
        IsUsed == other.IsUsed && IsInstalled == other.IsInstalled && Patterns.SequenceEqual(other.Patterns) && Files.SequenceEqual(other.Files);
}
