using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Submodules (J7.2, ADR-050): list with their state, initialize / update, deinitialize, add.</summary>
public sealed partial class GitService
{
    public async Task<GitResult<IReadOnlyList<Submodule>>> GetSubmodulesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        // No .gitmodules (the common case, and a bare repository): nothing to ask git.
        if (!File.Exists(Path.Combine(repositoryPath, ".gitmodules")))
            return new Success<IReadOnlyList<Submodule>>([]);

        var config = await Cli.RunAsync(repositoryPath, ["config", "--file", ".gitmodules", "--list", "-z"], cancellationToken);
        if (config is not Success<CliOutput> configured) return Propagate<IReadOnlyList<Submodule>>(config);
        var status = await Cli.RunAsync(repositoryPath, ["submodule", "status"], cancellationToken);
        if (status is not Success<CliOutput> listed) return Propagate<IReadOnlyList<Submodule>>(status);

        return new Success<IReadOnlyList<Submodule>>(ParseSubmodules(listed.Value.StandardOutput, ParseSubmoduleConfig(configured.Value.StandardOutput)));
    }

    /// <summary>The <c>.gitmodules</c> entry of a submodule.</summary>
    internal sealed record SubmoduleConfig(string Name, string? Url, string? Branch);

    /// <summary>
    /// Parses <c>git config --file .gitmodules --list -z</c> ("submodule.&lt;name&gt;.path\nvalue" fields ended by NUL; the name
    /// may contain dots) into the entries by path.
    /// </summary>
    internal static Dictionary<string, SubmoduleConfig> ParseSubmoduleConfig(string output)
    {
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var field in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = field.IndexOf('\n', StringComparison.Ordinal);
            var key = newline < 0 ? field : field[..newline];
            var value = newline < 0 ? string.Empty : field[(newline + 1)..];
            if (!key.StartsWith("submodule.", StringComparison.OrdinalIgnoreCase)) continue;
            var dot = key.LastIndexOf('.');
            if (dot <= "submodule.".Length) continue;
            var name = key["submodule.".Length..dot];
            var variable = key[(dot + 1)..].ToLowerInvariant();
            if (!values.TryGetValue(name, out var entry)) values[name] = entry = new Dictionary<string, string>(StringComparer.Ordinal);
            entry[variable] = value;
        }

        var byPath = new Dictionary<string, SubmoduleConfig>(StringComparer.Ordinal);
        foreach (var (name, entry) in values)
        {
            if (!entry.TryGetValue("path", out var path)) continue;
            entry.TryGetValue("url", out var url);
            entry.TryGetValue("branch", out var branch);
            byPath[path.Replace('\\', '/')] = new SubmoduleConfig(name, url, branch);
        }
        return byPath;
    }

    /// <summary>
    /// Parses <c>git submodule status</c>: one "&lt;c&gt;&lt;sha&gt; &lt;path&gt;[ (&lt;describe&gt;)]" line per submodule, where
    /// &lt;c&gt; is " " (in sync), "-" (not initialized), "+" (other commit) or "U" (conflict).
    /// </summary>
    internal static List<Submodule> ParseSubmodules(string statusOutput, IReadOnlyDictionary<string, SubmoduleConfig> byPath)
    {
        var submodules = new List<Submodule>();
        foreach (var raw in statusOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3) continue;
            var state = line[0] switch
            {
                '-' => SubmoduleState.NotInitialized,
                '+' => SubmoduleState.OutOfSync,
                'U' => SubmoduleState.Conflict,
                _ => SubmoduleState.UpToDate,
            };
            var space = line.IndexOf(' ', 1);
            if (space < 0) continue;
            var shaText = line[1..space];
            var rest = line[(space + 1)..];

            // "path (describe)": the suffix is only stripped when the whole text is not itself a known path.
            var path = rest;
            if (!byPath.ContainsKey(rest) && rest.EndsWith(')') && rest.LastIndexOf(" (", StringComparison.Ordinal) is > 0 and var cut)
                path = rest[..cut];

            // All zeros: the submodule is in conflict and has no single commit.
            GitSha? head = GitSha.TryParse(shaText, out var sha) && !shaText.All(c => c == '0') ? sha : null;
            byPath.TryGetValue(path, out var config);
            submodules.Add(new Submodule(config?.Name ?? path, path, config?.Url, config?.Branch, state, head));
        }
        return submodules;
    }

    public async Task<GitResult> UpdateSubmodulesAsync(string repositoryPath, string? path, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["submodule", "update", "--init", "--recursive"];
        if (path is not null)
        {
            if (path.Length == 0 || path.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidSubmodulePath"));
            arguments.AddRange(["--", path]);
        }
        return ExplainSubmoduleFailure(ToResult(await Cli.RunAsync(repositoryPath, arguments, cancellationToken)));
    }

    public async Task<GitResult> DeinitSubmoduleAsync(string repositoryPath, string path, bool force, CancellationToken cancellationToken = default)
    {
        if (path.Length == 0 || path.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidSubmodulePath"));
        List<string> arguments = ["submodule", "deinit"];
        if (force) arguments.Add("--force");
        arguments.AddRange(["--", path]);
        return ExplainSubmoduleFailure(ToResult(await Cli.RunAsync(repositoryPath, arguments, cancellationToken)));
    }

    public async Task<GitResult> AddSubmoduleAsync(string repositoryPath, string url, string? path, CancellationToken cancellationToken = default)
    {
        if (url.Length == 0) return new Failure(Loc.Get("GitError.SubmoduleUrlRequired"));
        if (url.StartsWith('-') || path is not null && (path.Length == 0 || path.StartsWith('-'))) return new Failure(Loc.Get("GitError.InvalidSubmoduleUrlOrPath"));
        List<string> arguments = ["submodule", "add", "--", url];
        if (path is not null) arguments.Add(path);
        return ExplainSubmoduleFailure(ToResult(await Cli.RunAsync(repositoryPath, arguments, cancellationToken)));
    }

    /// <summary>Rewrites the usual <c>git submodule</c> refusals in French.</summary>
    private static GitResult ExplainSubmoduleFailure(GitResult result)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        var needsForce = text.Contains("contains local modifications", StringComparison.Ordinal);
        var message = text switch
        {
            _ when needsForce && text.Contains("changes staged in the index", StringComparison.Ordinal) =>
                Loc.Get("GitError.SubmoduleJustAdded"),
            _ when needsForce => Loc.Get("GitError.SubmoduleLocalChanges"),
            _ when text.Contains("already exists in the index", StringComparison.Ordinal) => Loc.Get("GitError.PathAlreadyTracked"),
            _ when text.Contains("already exists and is not a valid git repo", StringComparison.Ordinal) =>
                Loc.Get("GitError.FolderExistsNotRepo"),
            _ when text.Contains("is ignored by one of your .gitignore", StringComparison.Ordinal) => Loc.Get("GitError.PathIgnored"),
            _ when text.Contains("transport 'file' not allowed", StringComparison.Ordinal) =>
                Loc.Get("GitError.FileProtocolRefused"),
            _ when text.Contains("does not appear to be a git repository", StringComparison.Ordinal)
                   || text.Contains("Repository not found", StringComparison.Ordinal) => Loc.Get("GitError.RepoNotFoundCheckUrl"),
            _ when text.Contains("did not match any file", StringComparison.Ordinal) => Loc.Get("GitError.SubmoduleNotFound"),
            _ => null,
        };
        if (message is null) return result;
        return failure with { Message = message, Details = text, Reason = needsForce ? FailureReason.SubmoduleNeedsForce : FailureReason.Unknown };
    }
}
