using System.Globalization;
using Vigo.Core.Models;

namespace Vigo.Git.Commits;

/// <summary>Builds <see cref="CommitDetails"/> from git.exe output (fallback when libgit2 cannot read the repository).</summary>
internal static class CommitCliReader
{
    /// <summary><c>git show -s</c> format: id, parents, author and committer, then the raw message (NUL separated).</summary>
    public const string ShowFormat = "--format=%H%x00%P%x00%an%x00%ae%x00%at%x00%ai%x00%cn%x00%ce%x00%ct%x00%ci%x00%G?%x00%GS%x00%GK%x00%B";

    /// <summary>
    /// Arguments of a <c>git diff-tree</c> comparing the commit with its first parent (or the empty tree for a root
    /// commit), with rename detection; <paramref name="mode"/> is <c>--name-status</c> or <c>--numstat</c>.
    /// </summary>
    public static string[] DiffTreeArguments(GitSha sha, GitSha? firstParent, string mode) => firstParent is { } parent
        ? ["diff-tree", "-r", "-M", "-z", "--no-commit-id", mode, parent.ToString(), sha.ToString()]
        : ["diff-tree", "-r", "-M", "-z", "--no-commit-id", "--root", mode, sha.ToString()];

    /// <summary>Same as <see cref="DiffTreeArguments"/> against <c>&lt;sha&gt;^</c>, which does not exist for a root commit.</summary>
    public static string[] DiffTreeAgainstFirstParent(GitSha sha, string mode) =>
        ["diff-tree", "-r", "-M", "-z", "--no-commit-id", mode, $"{sha}^", sha.ToString()];

    /// <summary>Parses the <c>git show -s</c> output; returns the parents too, needed before the diff-tree calls.</summary>
    public static (GitSha Sha, GitSha[] Parents, GitSignature Author, GitSignature Committer, string Message, CommitSignature Signature) ParseShow(string output)
    {
        var fields = output.Split('\0', 14);
        if (fields.Length < 14) throw new FormatException("Unexpected git show output.");

        var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => GitSha.Parse(p)).ToArray();
        return (GitSha.Parse(fields[0].Trim()), parents,
            Signature(fields[2], fields[3], fields[4], fields[5]),
            Signature(fields[6], fields[7], fields[8], fields[9]),
            fields[13].TrimEnd(),
            ParseSignature(fields[10], fields[11], fields[12]));
    }

    /// <summary>Maps the <c>%G?</c> letter (plus <c>%GS</c> and <c>%GK</c>) to a <see cref="CommitSignature"/>.</summary>
    public static CommitSignature ParseSignature(string code, string signer, string key)
    {
        var status = code.Trim() switch
        {
            "G" => SignatureStatus.Good,
            "B" => SignatureStatus.Bad,
            "U" => SignatureStatus.UnknownValidity,
            "X" => SignatureStatus.Expired,
            "Y" => SignatureStatus.ExpiredKey,
            "R" => SignatureStatus.Revoked,
            "E" => SignatureStatus.MissingKey,
            "N" or "" => SignatureStatus.None,
            _ => SignatureStatus.Error,
        };
        return status == SignatureStatus.None ? CommitSignature.NotSigned : new CommitSignature(status, signer.Trim(), key.Trim());
    }

    /// <summary>Joins the <c>--name-status -z</c> and <c>--numstat -z</c> outputs by path.</summary>
    public static List<ChangedFile> ParseFiles(string nameStatus, string numstat)
    {
        var stats = new Dictionary<string, (int Additions, int Deletions, bool IsBinary)>(StringComparer.Ordinal);
        var tokens = numstat.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            // "add\tdel\tpath" or, for a rename/copy, "add\tdel\t" followed by the old and new paths.
            var parts = tokens[i].Split('\t');
            if (parts.Length < 3) continue;
            var path = parts[2].Length > 0 ? parts[2] : i + 2 < tokens.Length ? tokens[i += 2] : string.Empty;
            var binary = parts[0] == "-";
            stats[path] = (binary ? 0 : Count(parts[0]), binary ? 0 : Count(parts[1]), binary);
        }

        var files = new List<ChangedFile>();
        var status = nameStatus.Split('\0');
        for (var i = 0; i + 1 < status.Length; i++)
        {
            var code = status[i];
            if (code.Length == 0) continue;
            FileChangeKind? kind = code[0] switch
            {
                'A' => FileChangeKind.Added,
                'D' => FileChangeKind.Deleted,
                'M' => FileChangeKind.Modified,
                'R' => FileChangeKind.Renamed,
                'C' => FileChangeKind.Copied,
                'T' => FileChangeKind.TypeChanged,
                _ => null,
            };
            string? oldPath = null;
            if (kind is FileChangeKind.Renamed or FileChangeKind.Copied)
                oldPath = status[++i];
            var path = status[++i];
            if (kind is null) continue;

            var (additions, deletions, isBinary) = stats.GetValueOrDefault(path);
            files.Add(new ChangedFile(path, oldPath, kind.Value, additions, deletions, isBinary));
        }

        return files;
    }

    private static int Count(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    private static GitSignature Signature(string name, string email, string unixTime, string isoDate)
    {
        // %ai gives the original offset ("2026-10-05 14:12:00 +0200"); %at the exact instant.
        var instant = DateTimeOffset.FromUnixTimeSeconds(long.Parse(unixTime, CultureInfo.InvariantCulture));
        var offset = isoDate.Length >= 5 && TimeSpan.TryParseExact(isoDate[^4..], "hhmm", CultureInfo.InvariantCulture, out var o)
            ? isoDate[^5] == '-' ? -o : o
            : TimeSpan.Zero;
        return new GitSignature(name, email, instant.ToOffset(offset));
    }
}
