namespace Vigo.Core.Models;

/// <summary>Lightweight snapshot of an open repository.</summary>
/// <param name="WorkingDirectory">Working directory root (null for a bare repository).</param>
/// <param name="GitDirectory">Path of the .git directory.</param>
/// <param name="Name">Display name (root folder).</param>
/// <param name="HeadName">Current branch, or short SHA when HEAD is detached; null for an empty repository.</param>
/// <param name="HeadSha">Full HEAD SHA; null for an empty repository.</param>
public sealed record RepositoryInfo(
    string? WorkingDirectory,
    string GitDirectory,
    string Name,
    string? HeadName,
    string? HeadSha,
    bool IsBare,
    bool IsHeadDetached,
    bool IsEmpty);

/// <summary>Git identity (user.name / user.email) resolved from the config.</summary>
public sealed record GitIdentity(string? Name, string? Email)
{
    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "?";
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 1
                ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant()
                : string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0]));
        }
    }
}
