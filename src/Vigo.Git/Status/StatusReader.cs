using System.Globalization;
using Vigo.Core.Models;

namespace Vigo.Git.Status;

/// <summary>Parses <c>git status --porcelain=v2 -z --branch</c>.</summary>
internal static class StatusReader
{
    /// <summary>
    /// <c>--no-optional-locks</c>: status neither takes <c>index.lock</c> nor rewrites the index, so it never collides
    /// with a git command run by the user and does not wake the index watcher up again.
    /// </summary>
    public static readonly string[] Arguments =
    [
        "--no-optional-locks", "-c", "core.quotepath=false",
        "status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all", "--find-renames", "--ignored=no",
    ];

    public static WorkingTreeStatus Parse(string output)
    {
        GitSha? head = null;
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var entries = new List<StatusEntry>();

        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 2) continue;

            switch (token[0])
            {
                case '#':
                    ParseHeader(token, ref head, ref branch, ref upstream, ref ahead, ref behind);
                    break;

                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var fields = Split(token, 9);
                    entries.Add(Changed(fields[1], fields[8], oldPath: null));
                    break;
                }

                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path, then the original path as the next token.
                    var fields = Split(token, 10);
                    var oldPath = i + 1 < tokens.Length ? tokens[++i] : null;
                    entries.Add(Changed(fields[1], fields[9], oldPath));
                    break;
                }

                case 'u':
                {
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var fields = Split(token, 11);
                    entries.Add(new StatusEntry(fields[10], null, null, null, Conflict: ConflictOf(fields[1])));
                    break;
                }

                case '?':
                    entries.Add(new StatusEntry(token[2..], null, null, FileChangeKind.Added, IsUntracked: true));
                    break;
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new WorkingTreeStatus(head, branch, upstream, ahead, behind, entries);
    }

    private static void ParseHeader(string line, ref GitSha? head, ref string? branch, ref string? upstream, ref int ahead, ref int behind)
    {
        var parts = line.Split(' ');
        if (parts.Length < 3) return;
        switch (parts[1])
        {
            case "branch.oid":
                head = GitSha.TryParse(parts[2], out var sha) ? sha : null; // "(initial)" before the first commit
                break;
            case "branch.head":
                branch = parts[2] == "(detached)" ? null : line[(line.IndexOf("branch.head ", StringComparison.Ordinal) + 12)..];
                break;
            case "branch.upstream":
                upstream = parts[2];
                break;
            case "branch.ab" when parts.Length >= 4:
                ahead = Math.Abs(int.Parse(parts[2], CultureInfo.InvariantCulture));
                behind = Math.Abs(int.Parse(parts[3], CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string[] Split(string token, int count)
    {
        var fields = token.Split(' ', count);
        if (fields.Length < count || fields[1].Length != 2) throw new FormatException($"Unexpected status entry: {token}");
        return fields;
    }

    private static StatusEntry Changed(string xy, string path, string? oldPath) =>
        new(path, oldPath, KindOf(xy[0]), KindOf(xy[1]));

    private static FileChangeKind? KindOf(char code) => code switch
    {
        'M' => FileChangeKind.Modified,
        'A' => FileChangeKind.Added,
        'D' => FileChangeKind.Deleted,
        'R' => FileChangeKind.Renamed,
        'C' => FileChangeKind.Copied,
        'T' => FileChangeKind.TypeChanged,
        _ => null,
    };

    private static ConflictKind ConflictOf(string xy) => xy switch
    {
        "DD" => ConflictKind.BothDeleted,
        "AU" => ConflictKind.AddedByUs,
        "UD" => ConflictKind.DeletedByThem,
        "UA" => ConflictKind.AddedByThem,
        "DU" => ConflictKind.DeletedByUs,
        "AA" => ConflictKind.BothAdded,
        _ => ConflictKind.BothModified,
    };
}
