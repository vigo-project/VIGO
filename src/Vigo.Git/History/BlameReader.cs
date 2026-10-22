using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Vigo.Core.Models;

namespace Vigo.Git.History;

/// <summary>
/// Parses <c>git blame --incremental</c> from the pipe: each group starts with
/// <c>&lt;sha&gt; &lt;source-line&gt; &lt;line&gt; &lt;count&gt;</c>, followed by the commit's headers the first time it
/// appears (<c>author</c>, <c>author-mail</c>, <c>author-time</c>, <c>summary</c>…), then <c>previous</c> or
/// <c>boundary</c>, and ends with <c>filename</c>. Hunks are delivered in batches while git searches, so that a long
/// blame shows its lines as they are found.
/// </summary>
internal sealed class BlameReader
{
    /// <summary>Arguments for git.exe: blame of <paramref name="path"/> at <paramref name="revision"/>, or of the file on disk when null.</summary>
    public static IReadOnlyList<string> BuildArguments(string? revision, string path) =>
        revision is null
            ? ["--literal-pathspecs", "-c", "core.quotepath=false", "blame", "--incremental", "--", path]
            : ["--literal-pathspecs", "-c", "core.quotepath=false", "blame", "--incremental", revision, "--", path];

    // A batch goes out once this many hunks are waiting, or when the previous one is this old.
    private const int BatchSize = 2000;
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(80);

    private readonly BlameHunkHandler _onHunks;
    private readonly Dictionary<GitSha, BlameCommit> _commits = [];
    private List<BlameHunk> _batch = [];
    private long _lastFlush = Stopwatch.GetTimestamp();

    public BlameReader(BlameHunkHandler onHunks) => _onHunks = onHunks;

    public async Task ReadAsync(PipeReader output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(output.AsStream(), new UTF8Encoding(false), false, 16 * 1024);
        Group? group = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (group is null)
            {
                group = ParseGroup(line);
                continue;
            }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..];
            switch (key)
            {
                case "author":
                    group.Author = value;
                    break;
                case "author-mail":
                    group.Email = value.TrimStart('<').TrimEnd('>');
                    break;
                case "author-time":
                    group.Time = long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                        : default;
                    break;
                case "summary":
                    group.Summary = value;
                    break;
                case "boundary":
                    group.IsBoundary = true;
                    break;
                case "previous":
                    // "previous <sha> <path>": the path may contain spaces.
                    var separator = value.IndexOf(' ');
                    if (separator > 0 && GitSha.TryParse(value.AsSpan(0, separator), out var previous))
                        group.Previous = new BlameOrigin(previous, value[(separator + 1)..]);
                    break;
                case "filename":
                    await AddAsync(group, value, cancellationToken);
                    group = null;
                    break;
            }
        }
        if (group is not null) throw new FormatException("Truncated git blame output.");
        if (_batch.Count > 0) await FlushAsync(cancellationToken);
    }

    private static Group ParseGroup(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 4 || !GitSha.TryParse(parts[0], out var sha)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new FormatException("Invalid git blame group header.");
        return new Group(sha, start, count);
    }

    private async ValueTask AddAsync(Group group, string path, CancellationToken cancellationToken)
    {
        // The headers come with the first group of a commit only.
        if (!_commits.TryGetValue(group.Sha, out var commit))
        {
            commit = new BlameCommit(group.Sha, group.Author, group.Email, group.Time, group.Summary, group.IsBoundary, group.Previous);
            _commits.Add(group.Sha, commit);
        }
        _batch.Add(new BlameHunk(group.Line, group.Count, commit, path));
        if (_batch.Count >= BatchSize || Stopwatch.GetElapsedTime(_lastFlush) >= BatchDelay) await FlushAsync(cancellationToken);
    }

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        var batch = _batch;
        _batch = [];
        _lastFlush = Stopwatch.GetTimestamp();
        await _onHunks(batch, cancellationToken);
    }

    private sealed class Group(GitSha sha, int line, int count)
    {
        public GitSha Sha { get; } = sha;

        public int Line { get; } = line;

        public int Count { get; } = count;

        public string Author { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public DateTimeOffset Time { get; set; }

        public string Summary { get; set; } = string.Empty;

        public bool IsBoundary { get; set; }

        public BlameOrigin? Previous { get; set; }
    }
}
