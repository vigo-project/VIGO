using System.Text.RegularExpressions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Git LFS (J7.5, ADR-055): tracked patterns and files, track / untrack, download of the missing objects.</summary>
public sealed partial class GitService
{
    public async Task<GitResult<LfsInfo>> GetLfsAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        // Not routed through the LFS filter (the common case, and a bare repository): nothing to ask git.
        if (!await UsesLfsAsync(repositoryPath, cancellationToken)) return new Success<LfsInfo>(LfsInfo.NotUsed);

        var version = await Cli.RunAsync(repositoryPath, ["lfs", "version"], cancellationToken);
        if (version is Canceled canceled) return canceled;
        // "git: 'lfs' is not a git command": the repository asks for LFS but this machine cannot serve it.
        if (version is not Success<CliOutput>) return new Success<LfsInfo>(new LfsInfo(true, false, [], []));

        var track = await Cli.RunAsync(repositoryPath, ["lfs", "track"], cancellationToken);
        if (track is not Success<CliOutput> tracked) return Propagate<LfsInfo>(track);

        // An empty repository has no HEAD to list files from: no files, not an error.
        var listed = await Cli.RunAsync(repositoryPath, ["lfs", "ls-files", "--long", "--size"], cancellationToken);
        if (listed is Canceled stopped) return stopped;
        var files = listed is Success<CliOutput> output ? ParseLfsFiles(output.Value.StandardOutput) : [];

        return new Success<LfsInfo>(new LfsInfo(true, true, ParseLfsPatterns(tracked.Value.StandardOutput), files));
    }

    /// <summary>The attributes of the repository (<c>.gitattributes</c> at its root, <c>.git/info/attributes</c>) name the LFS filter.</summary>
    private static async Task<bool> UsesLfsAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        foreach (var file in new[] { Path.Combine(repositoryPath, ".gitattributes"), Path.Combine(repositoryPath, ".git", "info", "attributes") })
        {
            try
            {
                if (File.Exists(file) && (await File.ReadAllTextAsync(file, cancellationToken)).Contains("filter=lfs", StringComparison.Ordinal))
                    return true;
            }
            catch (IOException)
            {
                // Unreadable (being rewritten by an editor): treated as absent until the next refresh.
            }
        }
        return false;
    }

    /// <summary>
    /// Parses <c>git lfs track</c>: "Listing tracked patterns", then one "    pattern (source)" line per pattern, then
    /// "Listing excluded patterns" (not listed here).
    /// </summary>
    internal static List<LfsPattern> ParseLfsPatterns(string output)
    {
        var patterns = new List<LfsPattern>();
        var tracked = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Listing tracked patterns", StringComparison.Ordinal)) { tracked = true; continue; }
            if (line.StartsWith("Listing ", StringComparison.Ordinal)) { tracked = false; continue; }
            if (!tracked || line.Length == 0 || !char.IsWhiteSpace(line[0])) continue;

            var text = line.Trim();
            var cut = text.LastIndexOf(" (", StringComparison.Ordinal);
            if (cut <= 0 || !text.EndsWith(')')) continue;
            patterns.Add(new LfsPattern(text[..cut], text[(cut + 2)..^1]));
        }
        return patterns;
    }

    /// <summary>
    /// Parses <c>git lfs ls-files --long --size</c>: "&lt;oid&gt; &lt;*|-&gt; &lt;path&gt; (&lt;size&gt;)", where "*" means the
    /// content is in the working directory and "-" only the pointer.
    /// </summary>
    internal static List<LfsFile> ParseLfsFiles(string output)
    {
        var files = new List<LfsFile>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var first = line.IndexOf(' ');
            if (first <= 0 || line.Length < first + 4 || line[first + 1] is not ('*' or '-') || line[first + 2] != ' ') continue;

            var oid = line[..first];
            var rest = line[(first + 3)..];
            string? size = null;
            if (LfsSizeSuffix().Match(rest) is { Success: true } match)
            {
                rest = match.Groups[1].Value;
                size = match.Groups[2].Value;
            }
            files.Add(new LfsFile(rest.Replace('\\', '/'), oid.Length > 10 ? oid[..10] : oid, line[first + 1] == '*', size));
        }
        return files;
    }

    [GeneratedRegex(@"^(.*) \((\d+(?:\.\d+)? [A-Za-z]+)\)$")]
    private static partial Regex LfsSizeSuffix();

    public async Task<GitResult> LfsTrackAsync(string repositoryPath, string pattern, CancellationToken cancellationToken = default)
    {
        if (CheckLfsPattern(pattern) is { } refusal) return new Failure(refusal);
        return ExplainLfsFailure(ToResult(await Cli.RunAsync(repositoryPath, ["lfs", "track", "--", pattern], cancellationToken)));
    }

    public async Task<GitResult> LfsUntrackAsync(string repositoryPath, string pattern, CancellationToken cancellationToken = default)
    {
        if (CheckLfsPattern(pattern) is { } refusal) return new Failure(refusal);
        return ExplainLfsFailure(ToResult(await Cli.RunAsync(repositoryPath, ["lfs", "untrack", "--", pattern], cancellationToken)));
    }

    public async Task<GitResult> LfsPullAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        ExplainLfsFailure(ToResult(await Cli.RunAsync(repositoryPath, ["lfs", "pull"], cancellationToken)));

    private static string? CheckLfsPattern(string pattern) =>
        pattern.Length == 0 ? Loc.Get("GitError.LfsPatternRequired")
        : pattern.StartsWith('-') ? Loc.Get("GitError.LfsPatternDash")
        : pattern.Any(char.IsControl) ? Loc.Get("GitError.LfsPatternControl")
        : null;

    /// <summary>Rewrites the usual <c>git lfs</c> refusals in French.</summary>
    private static GitResult ExplainLfsFailure(GitResult result)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        var message = text switch
        {
            _ when text.Contains("'lfs' is not a git command", StringComparison.Ordinal) =>
                Loc.Get("GitError.LfsNotInstalled"),
            _ when text.Contains("Not in a Git repository", StringComparison.Ordinal) => Loc.Get("GitError.FolderNotRepository"),
            _ when text.Contains("Repository or object not found", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Object does not exist on the server", StringComparison.Ordinal) =>
                Loc.Get("GitError.LfsObjectsMissing"),
            _ when text.Contains("Client error", StringComparison.Ordinal) && text.Contains("401", StringComparison.Ordinal) =>
                Loc.Get("GitError.LfsCredentialsRefused"),
            _ => null,
        };
        return message is null ? result : failure with { Message = message, Details = text };
    }
}
