using System.Globalization;
using System.Text.RegularExpressions;
using Vigo.Core.Models;

namespace Vigo.Git.Cli;

/// <summary>
/// Parses the progress lines git writes on stderr with <c>--progress</c> (LC_ALL=C, so English phase names):
/// <c>Receiving objects:  42% (123/456), 1.20 MiB | 4.20 MiB/s</c>, <c>remote: Counting objects: 100% (4/4), done.</c>
/// </summary>
internal static partial class ProgressParser
{
    [GeneratedRegex(@"^(?<phase>[A-Za-z][A-Za-z ]*?):\s+(?<pct>\d{1,3})% \((?<cur>\d+)/(?<tot>\d+)\)(?:,\s*(?<size>[^|]+?)\s*\|\s*(?<rate>[^,]+?/s))?", RegexOptions.CultureInvariant)]
    private static partial Regex PercentLine();

    /// <summary>Parses one line (a segment ended by '\r' or '\n'); null for an empty one.</summary>
    public static GitProgress? Parse(ReadOnlySpan<char> line)
    {
        var text = line.Trim().ToString();
        if (text.Length == 0) return null;

        var isRemote = text.StartsWith("remote:", StringComparison.Ordinal);
        if (isRemote) text = text[7..].Trim();
        if (text.Length == 0) return null;

        var match = PercentLine().Match(text);
        if (!match.Success) return new GitProgress(PhaseOf(text), text, IsRemote: isRemote);

        var rate = match.Groups["rate"];
        return new GitProgress(
            PhaseOf(match.Groups["phase"].Value),
            text,
            Math.Clamp(int.Parse(match.Groups["pct"].ValueSpan, CultureInfo.InvariantCulture), 0, 100),
            long.Parse(match.Groups["cur"].ValueSpan, CultureInfo.InvariantCulture),
            long.Parse(match.Groups["tot"].ValueSpan, CultureInfo.InvariantCulture),
            rate.Success ? rate.Value.Trim() : null,
            isRemote);
    }

    private static GitProgressPhase PhaseOf(string text) => text switch
    {
        _ when text.StartsWith("Enumerating objects", StringComparison.Ordinal) => GitProgressPhase.Enumerating,
        _ when text.StartsWith("Counting objects", StringComparison.Ordinal) => GitProgressPhase.Counting,
        _ when text.StartsWith("Compressing objects", StringComparison.Ordinal) => GitProgressPhase.Compressing,
        _ when text.StartsWith("Receiving objects", StringComparison.Ordinal) => GitProgressPhase.Receiving,
        _ when text.StartsWith("Unpacking objects", StringComparison.Ordinal) => GitProgressPhase.Unpacking,
        _ when text.StartsWith("Resolving deltas", StringComparison.Ordinal) => GitProgressPhase.Resolving,
        _ when text.StartsWith("Writing objects", StringComparison.Ordinal) => GitProgressPhase.Writing,
        _ when text.StartsWith("Updating files", StringComparison.Ordinal) => GitProgressPhase.CheckingOut,
        _ => GitProgressPhase.Other,
    };
}
