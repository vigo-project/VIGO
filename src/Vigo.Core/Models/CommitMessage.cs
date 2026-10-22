namespace Vigo.Core.Models;

/// <summary>What <see cref="Git.IGitService.CommitAsync"/> records.</summary>
/// <param name="Message">Full message (see <see cref="CommitMessage.Compose"/>); git strips its surrounding whitespace.</param>
/// <param name="Amend">Replaces the HEAD commit (its parents are kept) instead of creating a child.</param>
/// <param name="SkipHooks">Skips the pre-commit and commit-msg hooks (<c>--no-verify</c>).</param>
/// <param name="CoAuthors">Added as <c>Co-authored-by</c> trailers (an existing identical trailer is not repeated).</param>
public sealed record CommitRequest(
    string Message,
    bool Amend = false,
    bool SkipHooks = false,
    IReadOnlyList<GitIdentity>? CoAuthors = null);

/// <summary>Commit message conventions: summary line, blank line, description.</summary>
public static class CommitMessage
{
    /// <summary>Recommended summary length (git documentation, GitHub truncation).</summary>
    public const int RecommendedSummaryLength = 50;

    /// <summary>Hard summary limit of the usual conventions; longer summaries are allowed but flagged.</summary>
    public const int MaxSummaryLength = 72;

    /// <summary>
    /// Builds the message from a one-line summary and an optional description: line breaks of the summary become
    /// spaces, CRLF become LF, trailing whitespace is removed, a blank line separates both parts.
    /// </summary>
    public static string Compose(string summary, string? description)
    {
        var subject = string.Join(' ', summary.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var body = (description ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
        body = body.TrimStart('\n');
        return body.Length == 0 ? subject + "\n" : $"{subject}\n\n{body}\n";
    }

    /// <summary>Splits a message into its first line and the rest (leading blank lines and trailing whitespace removed).</summary>
    public static (string Summary, string Description) Split(string message)
    {
        var text = message.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n', ' ', '\t');
        var end = text.IndexOf('\n', StringComparison.Ordinal);
        return end < 0
            ? (text.TrimEnd(), string.Empty)
            : (text[..end].TrimEnd(), text[(end + 1)..].TrimStart('\n').TrimEnd());
    }
}
