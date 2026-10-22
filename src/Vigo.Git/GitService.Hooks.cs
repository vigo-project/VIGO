using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Hooks of the repository (J7.31): hooks folder, enabled / disabled hooks, and the "skip hooks" switch for commits and pushes made by VIGO.</summary>
public sealed partial class GitService
{
    private const string DisabledSuffix = ".disabled";
    private const string SkipHooksKey = "vigo.skiphooks";

    public async Task<GitResult<RepositoryHooks>> GetHooksAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        // Exit code 1: the key is not set in the local config.
        var path = await Cli.RunAsync(repositoryPath, ["config", "--local", "--get", "core.hooksPath"], 1, cancellationToken);
        if (path is not Success<CliOutput> p) return Propagate<RepositoryHooks>(path);

        var folder = await HooksFolderAsync(repositoryPath, cancellationToken);
        if (folder is null) return new Failure(Loc.Get("GitError.HooksFolderUnknown"));

        var hooks = new List<HookEntry>();
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase))
            {
                if (file.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)) continue;
                hooks.Add(file.EndsWith(DisabledSuffix, StringComparison.Ordinal)
                    ? new HookEntry(file[..^DisabledSuffix.Length], false)
                    : new HookEntry(file, true));
            }
        }
        return new Success<RepositoryHooks>(new RepositoryHooks(
            NullIfEmpty(p.Value.ExitCode == 0 ? p.Value.StandardOutput.Trim() : string.Empty), hooks, await SkipHooksAsync(repositoryPath, cancellationToken)));
    }

    public async Task<GitResult> SetHooksPathAsync(string repositoryPath, string? path, CancellationToken cancellationToken = default)
    {
        // Exit code 5: nothing to remove.
        var result = string.IsNullOrWhiteSpace(path)
            ? await Cli.RunAsync(repositoryPath, ["config", "--local", "--unset", "core.hooksPath"], 5, cancellationToken)
            : await Cli.RunAsync(repositoryPath, ["config", "--local", "core.hooksPath", path.Trim()], cancellationToken);
        return ToResult(result);
    }

    public async Task<GitResult> SetHookEnabledAsync(string repositoryPath, string name, bool enabled, CancellationToken cancellationToken = default)
    {
        // A name from the list is a plain file name: anything else would leave the hooks folder.
        if (name.Length == 0 || name != Path.GetFileName(name)) return new Failure(Loc.Format("GitError.InvalidHookName", name));

        var folder = await HooksFolderAsync(repositoryPath, cancellationToken);
        if (folder is null) return new Failure(Loc.Get("GitError.HooksFolderUnknown"));

        var active = Path.Combine(folder, name);
        var disabled = active + DisabledSuffix;
        var (from, to) = enabled ? (disabled, active) : (active, disabled);
        if (!File.Exists(from)) return new Failure(Loc.Format("GitError.HookMissing", name));
        if (File.Exists(to)) return new Failure(Loc.Format("GitError.HookBothExist", name));

        try
        {
            File.Move(from, to);
            return Done.Instance;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Failure(Loc.Format("GitError.HookRenameFailed", name), Details: e.Message);
        }
    }

    public async Task<GitResult> SetSkipHooksAsync(string repositoryPath, bool skip, CancellationToken cancellationToken = default) =>
        ToResult(skip
            ? await Cli.RunAsync(repositoryPath, ["config", "--local", "--type=bool", SkipHooksKey, "true"], cancellationToken)
            : await Cli.RunAsync(repositoryPath, ["config", "--local", "--unset", SkipHooksKey], 5, cancellationToken));

    /// <summary>The switch read at each commit or push: the config is edited by the preferences, or by hand.</summary>
    private async Task<bool> SkipHooksAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await Cli.RunAsync(repositoryPath, ["config", "--local", "--type=bool", "--get", SkipHooksKey], 1, cancellationToken);
        return result is Success<CliOutput> { Value: { ExitCode: 0 } o } && o.StandardOutput.Trim() == "true";
    }

    private async Task<string?> HooksFolderAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        // Run from the top level: a relative core.hooksPath is resolved from there.
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        var result = await Cli.RunAsync(top, ["rev-parse", "--git-path", "hooks"], cancellationToken);
        return result is Success<CliOutput> s && s.Value.StandardOutput.Trim() is { Length: > 0 } folder ? Path.GetFullPath(folder, top) : null;
    }
}
