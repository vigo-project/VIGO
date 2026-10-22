using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Vigo.Core.Models;

namespace Vigo.Git.History;

/// <summary>
/// Parses <c>git log -z --raw --numstat</c> limited to one file, read from the pipe: each record starts with RS
/// (0x1E) and six NUL-terminated header fields (SHA, parents, author name, email, date, subject), then the raw entry of
/// the file (<c>\n:mode mode sha sha STATUS</c>, its path, and the new one for a rename or copy) and its numstat entry
/// (<c>added\tdeleted\tpath</c>, or <c>added\tdeleted\t</c> followed by both paths for a rename).
/// </summary>
internal sealed class FileHistoryReader
{
    private const string Format = "--format=%x1e%H%x00%P%x00%an%x00%ae%x00%at%x00%s";

    /// <summary>
    /// Arguments for git.exe: history of <paramref name="path"/> from <paramref name="revision"/>, renames followed
    /// when <paramref name="follow"/> (<c>--follow</c>, one file only). Merge commits are left out: their changes are
    /// those of the commits they bring, listed on their own.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string revision, string path, bool follow)
    {
        List<string> args =
        [
            "--literal-pathspecs", "-c", "log.showSignature=false", "log", "-z", "--no-color", "--encoding=UTF-8",
            "--no-merges", "-M", Format, "--raw", "--numstat", "--no-abbrev",
        ];
        if (follow) args.Add("--follow");
        args.AddRange([revision, "--", path]);
        return args;
    }

    private readonly int _pageSize;
    private readonly FileHistoryPageHandler _onPage;
    private List<FileHistoryEntry> _page;

    public FileHistoryReader(int pageSize, FileHistoryPageHandler onPage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
        _onPage = onPage;
        _page = new List<FileHistoryEntry>(pageSize);
    }

    public async Task ReadAsync(PipeReader output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(output.AsStream(), new UTF8Encoding(false), false, 16 * 1024);
        var fields = new Fields(reader);
        Record? record = null;
        while (await fields.NextAsync(cancellationToken) is { } field)
        {
            var entry = field.TrimStart('\n');
            if (entry.StartsWith('\x1e'))
            {
                await AddAsync(record, cancellationToken);
                record = await ReadHeaderAsync(entry[1..], fields, cancellationToken);
                continue;
            }
            if (record is null) throw new FormatException("git log output does not start with a commit.");

            if (entry.StartsWith(':'))
            {
                // Raw entry: ":old-mode new-mode old-sha new-sha STATUS", then one path, or two for a rename / copy.
                var status = entry[(entry.LastIndexOf(' ') + 1)..];
                var first = await fields.NextAsync(cancellationToken) ?? throw new FormatException("Truncated raw entry.");
                var second = status is ['R' or 'C', ..] ? await fields.NextAsync(cancellationToken) : null;
                // --follow may list another path of the same commit (rare): the first entry is the followed file.
                record.Raw ??= (status, second is null ? first : second, second is null ? null : first);
            }
            else if (entry.Length > 0)
            {
                // Numstat entry: "added\tdeleted\tpath", or "added\tdeleted\t" then the old and new paths.
                var parts = entry.Split('\t');
                if (parts.Length < 3) throw new FormatException("Invalid numstat entry.");
                if (parts[2].Length == 0)
                {
                    _ = await fields.NextAsync(cancellationToken);
                    _ = await fields.NextAsync(cancellationToken);
                }
                if (record.Stats is null)
                {
                    var binary = parts[0] == "-";
                    record.Stats = (binary ? 0 : Number(parts[0]), binary ? 0 : Number(parts[1]), binary);
                }
            }
        }
        await AddAsync(record, cancellationToken);
        if (_page.Count > 0) await FlushAsync(cancellationToken);
    }

    private static async Task<Record> ReadHeaderAsync(string sha, Fields fields, CancellationToken cancellationToken)
    {
        var parents = await fields.NextAsync(cancellationToken);
        var name = await fields.NextAsync(cancellationToken);
        var email = await fields.NextAsync(cancellationToken);
        var date = await fields.NextAsync(cancellationToken);
        var subject = await fields.NextAsync(cancellationToken);
        if (subject is null || !GitSha.TryParse(sha, out var id)) throw new FormatException("Truncated commit header.");

        var parentIds = new List<GitSha>(1);
        foreach (var hex in parents!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            parentIds.Add(GitSha.TryParse(hex, out var parent) ? parent : throw new FormatException("Invalid parent id."));
        var when = long.TryParse(date, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : default;
        return new Record(new CommitInfo(id, [.. parentIds], name!, email!, when, subject));
    }

    private async ValueTask AddAsync(Record? record, CancellationToken cancellationToken)
    {
        // A commit without a raw entry did not change the file (cannot happen with a path limit; kept out anyway).
        if (record?.Raw is not var (status, path, oldPath)) return;
        var kind = status[0] switch
        {
            'A' => FileChangeKind.Added,
            'D' => FileChangeKind.Deleted,
            'R' => FileChangeKind.Renamed,
            'C' => FileChangeKind.Copied,
            'T' => FileChangeKind.TypeChanged,
            _ => FileChangeKind.Modified,
        };
        var (added, deleted, binary) = record.Stats ?? (0, 0, false);
        _page.Add(new FileHistoryEntry(record.Commit, new ChangedFile(path, oldPath, kind, added, deleted, binary)));
        if (_page.Count >= _pageSize) await FlushAsync(cancellationToken);
    }

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        var page = _page;
        _page = new List<FileHistoryEntry>(_pageSize);
        await _onPage(page, cancellationToken);
    }

    private static int Number(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private sealed class Record(CommitInfo commit)
    {
        public CommitInfo Commit { get; } = commit;

        public (string Status, string Path, string? OldPath)? Raw { get; set; }

        public (int Added, int Deleted, bool Binary)? Stats { get; set; }
    }

    /// <summary>NUL-terminated fields of a text stream; the last one may lack its terminator.</summary>
    private sealed class Fields(StreamReader reader)
    {
        private readonly char[] _buffer = new char[16 * 1024];
        private readonly StringBuilder _field = new();
        private int _position;
        private int _length;

        public async ValueTask<string?> NextAsync(CancellationToken cancellationToken)
        {
            _field.Clear();
            while (true)
            {
                if (_position == _length)
                {
                    _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
                    _position = 0;
                    if (_length == 0) return _field.Length > 0 ? _field.ToString() : null;
                }
                var span = _buffer.AsSpan(_position, _length - _position);
                var end = span.IndexOf('\0');
                if (end < 0)
                {
                    _field.Append(span);
                    _position = _length;
                    continue;
                }
                _field.Append(span[..end]);
                _position += end + 1;
                return _field.ToString();
            }
        }
    }
}
