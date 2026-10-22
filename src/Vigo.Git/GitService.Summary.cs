using System.Globalization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Data of the "Résumé" page (J7.13, ADR-080).</summary>
public sealed partial class GitService
{
    private const char FieldSeparator = '\u001f';

    public async Task<GitResult<RepositorySummary>> GetSummaryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var size = RepositorySummary.ListSize;
        var branches = Cli.RunAsync(repositoryPath, [
            "for-each-ref", "--sort=-committerdate", $"--count={size}", "refs/heads",
            "--format=%(refname:short)%1f%(objectname)%1f%(committerdate:unix)%1f%(authorname)%1f%(upstream)%1f%(upstream:track,nobracket)%1f%(contents:subject)"],
            cancellationToken);
        var tags = Cli.RunAsync(repositoryPath, [
            "for-each-ref", "--sort=-creatordate", $"--count={size}", "refs/tags",
            "--format=%(refname:short)%1f%(if)%(*objectname)%(then)%(*objectname)%(else)%(objectname)%(end)%1f%(creatordate:unix)"],
            cancellationToken);
        var branchNames = Cli.RunAsync(repositoryPath, ["for-each-ref", "--format=%(refname)", "refs/heads"], cancellationToken);
        var tagNames = Cli.RunAsync(repositoryPath, ["for-each-ref", "--format=%(refname)", "refs/tags"], cancellationToken);
        var head = ReadOptionalAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
        var readme = Task.Run(() => ReadReadme(repositoryPath));
        await Task.WhenAll(branches, tags, branchNames, tagNames, head, readme);

        if (cancellationToken.IsCancellationRequested) return Canceled.Instance;
        if (head.Result.Value is not Success<string?> headResult)
        {
            return head.Result.Value switch
            {
                Canceled c => c,
                Failure f => f,
                _ => new Failure(Loc.Get("Common.UnexpectedResult")),
            };
        }
        if (branches.Result is not Success<CliOutput> branchOutput) return Propagate<RepositorySummary>(branches.Result);
        if (tags.Result is not Success<CliOutput> tagOutput) return Propagate<RepositorySummary>(tags.Result);
        if (branchNames.Result is not Success<CliOutput> branchNameOutput) return Propagate<RepositorySummary>(branchNames.Result);
        if (tagNames.Result is not Success<CliOutput> tagNameOutput) return Propagate<RepositorySummary>(tagNames.Result);

        // An unborn HEAD has neither a count nor a log: git would fail on them.
        var commitCount = 0;
        IReadOnlyList<SummaryCommit> commits = [];
        if (headResult.Value is not null)
        {
            var count = await Cli.RunAsync(repositoryPath, ["rev-list", "--count", "HEAD", "--"], cancellationToken);
            if (count is not Success<CliOutput> counted) return Propagate<RepositorySummary>(count);
            commitCount = int.TryParse(counted.Value.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

            var log = await Cli.RunAsync(repositoryPath, ["log", $"-n{size}", "--format=%H%x1f%an%x1f%at%x1f%s", "HEAD", "--"], cancellationToken);
            if (log is not Success<CliOutput> logged) return Propagate<RepositorySummary>(log);
            commits = ParseSummaryCommits(logged.Value.StandardOutput);
        }

        return new Success<RepositorySummary>(new RepositorySummary(
            commitCount,
            CountLines(branchNameOutput.Value.StandardOutput),
            CountLines(tagNameOutput.Value.StandardOutput),
            ParseSummaryBranches(branchOutput.Value.StandardOutput),
            ParseSummaryTags(tagOutput.Value.StandardOutput),
            commits,
            readme.Result));
    }

    private static int CountLines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static IEnumerable<string[]> Records(string output, int fields) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split(FieldSeparator))
            .Where(parts => parts.Length >= fields);

    internal static List<SummaryBranch> ParseSummaryBranches(string output)
    {
        List<SummaryBranch> branches = [];
        foreach (var parts in Records(output, 7))
        {
            if (!GitSha.TryParse(parts[1], out var sha)) continue;
            var (ahead, behind) = ParseTrack(parts[4], parts[5]);
            branches.Add(new SummaryBranch(parts[0], sha, ParseUnix(parts[2]), parts[6], parts[3], ahead, behind));
        }
        return branches;
    }

    /// <summary>"ahead 2, behind 1" of <c>upstream:track,nobracket</c>; empty = in sync; "gone" or no upstream = unknown.</summary>
    internal static (int? Ahead, int? Behind) ParseTrack(string upstream, string track)
    {
        if (upstream.Length == 0 || track == "gone") return (null, null);
        int ahead = 0, behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = part.Split(' ');
            if (words.Length != 2 || !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) continue;
            if (words[0] == "ahead") ahead = number;
            else if (words[0] == "behind") behind = number;
        }
        return (ahead, behind);
    }

    internal static List<SummaryTag> ParseSummaryTags(string output)
    {
        List<SummaryTag> tags = [];
        foreach (var parts in Records(output, 3))
        {
            if (GitSha.TryParse(parts[1], out var target)) tags.Add(new SummaryTag(parts[0], target, ParseUnix(parts[2])));
        }
        return tags;
    }

    internal static List<SummaryCommit> ParseSummaryCommits(string output)
    {
        List<SummaryCommit> commits = [];
        foreach (var parts in Records(output, 4))
        {
            if (GitSha.TryParse(parts[0], out var sha)) commits.Add(new SummaryCommit(sha, parts[3], parts[1], ParseUnix(parts[2])));
        }
        return commits;
    }

    private static DateTimeOffset ParseUnix(string seconds) =>
        long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? DateTimeOffset.FromUnixTimeSeconds(value) : DateTimeOffset.UnixEpoch;

    // First README of the root of the working directory (no case); a bare repository has no working directory.
    private static SummaryReadme? ReadReadme(string repositoryPath)
    {
        try
        {
            if (!Directory.Exists(repositoryPath) || (!Directory.Exists(Path.Combine(repositoryPath, ".git")) && !File.Exists(Path.Combine(repositoryPath, ".git"))))
                return null;

            var files = Directory.EnumerateFiles(repositoryPath).Select(Path.GetFileName).OfType<string>().ToList();
            foreach (var wanted in RepositorySummary.ReadmeNames)
            {
                var name = files.FirstOrDefault(file => string.Equals(file, wanted, StringComparison.OrdinalIgnoreCase));
                if (name is null) continue;

                using var stream = new FileStream(Path.Combine(repositoryPath, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[RepositorySummary.MaxReadmeBytes + 1];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                var truncated = read > RepositorySummary.MaxReadmeBytes;
                var length = truncated ? RepositorySummary.MaxReadmeBytes : read;
                // A BOM is not part of the text.
                var offset = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
                return new SummaryReadme(name, System.Text.Encoding.UTF8.GetString(buffer, offset, length - offset), truncated);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable README is no README: the page still shows the rest.
        }
        return null;
    }
}
