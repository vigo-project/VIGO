using System.Collections.Immutable;

using Vigo.Core.Services;

namespace Vigo.Core.Models;

/// <summary>
/// Git identity VIGO applies to the repositories it matches (J6.7, ADR-051): name and e-mail, optional signing, folder
/// rules. Hosting accounts and the reopened session belong to a profile too (<see cref="Id"/>).
/// </summary>
/// <param name="Id">Stable key of the profile ("default" for the first one, which adopts the accounts and the session that
/// existed before profiles).</param>
/// <param name="Label">Name shown in the list ("Travail", "Personnel").</param>
/// <param name="Name"><c>user.name</c>; empty: the Git configuration decides.</param>
/// <param name="Email"><c>user.email</c>; empty: the Git configuration decides.</param>
/// <param name="SigningKey"><c>user.signingkey</c>; empty: the Git configuration decides.</param>
/// <param name="SignCommits">Sign commits and tags with <paramref name="SigningKey"/>.</param>
/// <param name="FolderRules">Folder patterns (<c>C:\dev\atlas\**</c>) of the repositories the profile applies to.</param>
/// <param name="SigningFormat"><c>gpg.format</c> (<see cref="SigningFormats"/>); null: the Git configuration decides.</param>
public sealed record GitProfile(
    string Id,
    string Label,
    string Name,
    string Email,
    string? SigningKey = null,
    bool SignCommits = false,
    ImmutableArray<string> FolderRules = default,
    string? SigningFormat = null)
{
    public ImmutableArray<string> FolderRules { get; init; } = FolderRules.IsDefault ? [] : FolderRules;

    public string Initials => new GitIdentity(string.IsNullOrWhiteSpace(Name) ? Label : Name, null).Initials;

    /// <summary>"Nom · e-mail" line of the list.</summary>
    public string Summary => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Email)
        ? Loc.Get("Profile.Summary.Config")
        : string.Join(" · ", new[] { Name, Email }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// <c>-c key=value</c> settings that make git.exe use this profile: only the non-empty fields, so that an unfilled
    /// one leaves the user's configuration in charge.
    /// </summary>
    public IReadOnlyList<string> ConfigOverrides()
    {
        List<string> overrides = [];
        if (!string.IsNullOrWhiteSpace(Name)) overrides.Add($"user.name={Name.Trim()}");
        if (!string.IsNullOrWhiteSpace(Email)) overrides.Add($"user.email={Email.Trim()}");
        if (SigningFormats.Normalize(SigningFormat) is { } format) overrides.Add($"gpg.format={format}");
        if (!string.IsNullOrWhiteSpace(SigningKey))
        {
            overrides.Add($"user.signingkey={SigningKey.Trim()}");
            if (SignCommits)
            {
                overrides.Add("commit.gpgsign=true");
                overrides.Add("tag.gpgsign=true");
            }
        }
        return overrides;
    }

    /// <summary>Whether a folder rule of this profile matches the repository.</summary>
    public bool Matches(string repositoryPath) => FolderRules.Any(rule => FolderRule.Matches(rule, repositoryPath));
}

/// <summary>Folder patterns of a profile: <c>*</c> inside a folder name, <c>**</c> across folders, case-insensitive.</summary>
public static class FolderRule
{
    /// <summary>
    /// <c>C:\dev\atlas\**</c> matches <c>C:\dev\atlas</c> and everything below it; a pattern without wildcard matches that
    /// folder and what it contains.
    /// </summary>
    public static bool Matches(string rule, string path)
    {
        if (string.IsNullOrWhiteSpace(rule) || string.IsNullOrWhiteSpace(path)) return false;
        var pattern = Normalize(rule);
        var candidate = Normalize(path);
        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
            pattern += "/**";
        // "folder/**" also matches the folder itself.
        if (pattern.EndsWith("/**", StringComparison.Ordinal) && string.Equals(candidate, pattern[..^3], StringComparison.OrdinalIgnoreCase))
            return true;
        return Match(pattern, 0, candidate, 0);
    }

    private static string Normalize(string value) => value.Trim().Replace('\\', '/').TrimEnd('/');

    private static bool Match(string pattern, int p, string text, int t)
    {
        while (p < pattern.Length)
        {
            var c = pattern[p];
            if (c == '*')
            {
                var deep = p + 1 < pattern.Length && pattern[p + 1] == '*';
                var next = p + (deep ? 2 : 1);
                // "**/" may match nothing at all.
                if (deep && next < pattern.Length && pattern[next] == '/' && Match(pattern, next + 1, text, t)) return true;
                for (var end = t; end <= text.Length; end++)
                {
                    if (Match(pattern, next, text, end)) return true;
                    if (end < text.Length && !deep && text[end] == '/') break;
                }
                return false;
            }
            if (t >= text.Length) return false;
            if (c == '?' ? text[t] == '/' : char.ToUpperInvariant(c) != char.ToUpperInvariant(text[t])) return false;
            p++;
            t++;
        }
        return t == text.Length;
    }
}
