using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Vigo.Core.Models;

namespace Vigo.Git.Stats;

/// <summary>
/// Parses <c>git log -z</c> with a format of four fields (author time, name, e-mail, ISO author date with its offset), read
/// from the pipe: NUL ends a commit, the unit separator (0x1F) ends a field. Each commit goes straight to a
/// <see cref="CommitStatsBuilder"/>.
/// </summary>
internal sealed class CommitStatsReader(IProgress<int>? progress)
{
    private const int ProgressEvery = 5_000;

    private readonly CommitStatsBuilder _builder = new();

    /// <summary>Arguments for git.exe: every commit reachable from HEAD, names and addresses through the mailmap.</summary>
    public static IReadOnlyList<string> BuildArguments() =>
        ["-c", "log.showSignature=false", "log", "-z", "--no-color", "--encoding=UTF-8", "--use-mailmap", "--format=%at%x1f%aN%x1f%aE%x1f%ai", "HEAD", "--"];

    public RepositoryStats Build() => _builder.Build();

    public async Task ReadAsync(PipeReader output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(output.AsStream(), new UTF8Encoding(false), false, 64 * 1024);
        var buffer = new char[64 * 1024];
        var record = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var end = Array.IndexOf(buffer, '\0', start, read - start);
                if (end < 0)
                {
                    record.Append(buffer, start, read - start);
                    break;
                }
                record.Append(buffer, start, end - start);
                Add(record.ToString());
                record.Clear();
                start = end + 1;
            }
        }
        if (record.Length > 0) Add(record.ToString());
        progress?.Report(_builder.Count);
    }

    private void Add(string line)
    {
        var text = line.TrimStart('\n');
        if (text.Length == 0) return;
        var fields = text.Split('\u001f');
        if (fields.Length < 4) throw new FormatException("Truncated commit line.");
        if (!long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) throw new FormatException("Invalid author time.");
        if (!TryParseLocal(fields[3], out var weekday, out var hour)) throw new FormatException("Invalid author date.");

        _builder.Add(DateTimeOffset.FromUnixTimeSeconds(seconds), fields[1], fields[2], weekday, hour);
        if (_builder.Count % ProgressEvery == 0) progress?.Report(_builder.Count);
    }

    /// <summary>"2025-03-14 09:12:33 +0100": the weekday and the hour on the author's wall clock.</summary>
    internal static bool TryParseLocal(string isoDate, out DayOfWeek weekday, out int hour)
    {
        weekday = default;
        hour = 0;
        if (isoDate.Length < 13 || isoDate[10] != ' ') return false;
        if (!int.TryParse(isoDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(isoDate.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(isoDate.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(isoDate.AsSpan(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour)
            || year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23)
        {
            return false;
        }
        weekday = new DateOnly(year, month, day).DayOfWeek;
        return true;
    }
}
