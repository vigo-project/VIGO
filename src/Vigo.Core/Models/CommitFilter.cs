using System.Text;

namespace Vigo.Core.Models;

/// <summary>
/// What the commit search box asks <c>git log</c> for: free words in the message, authors, one commit id (or prefix)
/// and file path fragments. Every kind narrows the result (a commit must satisfy all of them).
/// </summary>
/// <param name="Terms">Words (or quoted phrases) the message must contain, case-insensitively.</param>
/// <param name="Authors">Name or email fragments; a commit matching any of them is kept.</param>
/// <param name="Sha">Hexadecimal commit id or prefix; the result is that commit alone.</param>
/// <param name="Files">Path fragments; a commit that touches a path containing any of them is kept.</param>
public sealed record CommitFilter(
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> Authors,
    string? Sha,
    IReadOnlyList<string> Files)
{
    public const int MinShaLength = 4;

    public bool IsEmpty => Terms.Count == 0 && Authors.Count == 0 && Sha is null && Files.Count == 0;

    /// <summary>
    /// Reads "fix crash author:alice sha:1a2b3c file:README" (quotes keep spaces: <c>author:"Alice Martin"</c>);
    /// null when the text holds nothing to search. A <c>sha:</c> value that is not hexadecimal, or is too short to
    /// be useful, is searched as a word instead.
    /// </summary>
    public static CommitFilter? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        List<string> terms = [];
        List<string> authors = [];
        List<string> files = [];
        string? sha = null;
        foreach (var token in Tokenize(text))
        {
            var colon = token.IndexOf(':');
            var key = colon > 0 ? token[..colon] : string.Empty;
            var value = colon > 0 ? token[(colon + 1)..] : token;
            switch (key.ToLowerInvariant())
            {
                case "author" or "auteur" when value.Length > 0:
                    authors.Add(value);
                    break;
                case "file" or "fichier" when value.Length > 0:
                    files.Add(value);
                    break;
                case "sha" or "hash" when IsShaPrefix(value):
                    sha = value.ToLowerInvariant();
                    break;
                case "author" or "auteur" or "file" or "fichier" or "sha" or "hash" when value.Length == 0:
                    // "author:" typed so far: nothing to search for yet.
                    break;
                default:
                    // A "sha:" value that is no id is searched as a word; "foo:bar" is a word as typed.
                    terms.Add(key.ToLowerInvariant() is "sha" or "hash" ? value : token);
                    break;
            }
        }

        var filter = new CommitFilter(terms, authors, sha, files);
        return filter.IsEmpty ? null : filter;
    }

    private static bool IsShaPrefix(string value) =>
        value.Length is >= MinShaLength and <= 64 && value.All(Uri.IsHexDigit);

    // Splits on white space; a double-quoted run (also after "key:") is one token without its quotes.
    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();
        var quoted = false;
        var hasToken = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (hasToken) yield return token.ToString();
                token.Clear();
                hasToken = false;
            }
            else
            {
                token.Append(c);
                hasToken = true;
            }
        }
        if (hasToken) yield return token.ToString();
    }
}
