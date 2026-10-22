using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>
/// Git Flow (J7.8, ADR-069): the model of the git-flow extension rebuilt on git.exe, with the same <c>gitflow.*</c> config
/// keys (a repository initialized by the extension is understood, and the other way round).
/// </summary>
public sealed partial class GitService
{
    public async Task<GitResult<GitFlowConfig?>> GetGitFlowAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        // Exit code 1: no gitflow.* key at all, the repository does not use Git Flow.
        var result = await Cli.RunAsync(repositoryPath, ["config", "--get-regexp", @"^gitflow\."], 1, cancellationToken);
        return result is Success<CliOutput> s
            ? new Success<GitFlowConfig?>(ParseGitFlowConfig(s.Value.StandardOutput))
            : Propagate<GitFlowConfig?>(result);
    }

    /// <summary>
    /// Parses <c>git config --get-regexp ^gitflow\.</c> ("gitflow.branch.master main", one key per line, an empty value
    /// leaving nothing after the key). Null without both the production and the development branch; the other keys fall
    /// back to the git-flow defaults.
    /// </summary>
    internal static GitFlowConfig? ParseGitFlowConfig(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var key = (space < 0 ? line : line[..space]).ToLowerInvariant();
            if (key.Length > 0) values[key] = space < 0 ? string.Empty : line[(space + 1)..];
        }

        if (!values.TryGetValue("gitflow.branch.master", out var main) || main.Length == 0
            || !values.TryGetValue("gitflow.branch.develop", out var develop) || develop.Length == 0)
        {
            return null;
        }

        var defaults = GitFlowConfig.Defaults(main);
        string Read(string key, string fallback) => values.TryGetValue(key, out var value) ? value : fallback;
        return new GitFlowConfig(main, develop,
            Read("gitflow.prefix.feature", defaults.FeaturePrefix),
            Read("gitflow.prefix.release", defaults.ReleasePrefix),
            Read("gitflow.prefix.hotfix", defaults.HotfixPrefix),
            Read("gitflow.prefix.versiontag", defaults.VersionTagPrefix));
    }

    public async Task<GitResult> GitFlowInitAsync(string repositoryPath, GitFlowConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Validate() is { } invalid) return new Failure(invalid);
        if (!await BranchExistsAsync(repositoryPath, config.Main, cancellationToken))
            return new Failure(Loc.Format("GitError.FlowMainMissing", config.Main));

        if (!await BranchExistsAsync(repositoryPath, config.Develop, cancellationToken))
        {
            var created = ToResult(await Cli.RunAsync(repositoryPath, ["branch", "--no-track", config.Develop, config.Main], cancellationToken));
            if (created.Value is not Done) return created;
        }

        return await WriteGitFlowKeysAsync(repositoryPath, config, cancellationToken);
    }

    public async Task<string?> GitFlowProblemAsync(string repositoryPath, GitFlowConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Validate() is { } invalid) return invalid;
        if (!await BranchExistsAsync(repositoryPath, config.Main, cancellationToken)) return Loc.Format("GitError.FlowMainMissing", config.Main);
        return await BranchExistsAsync(repositoryPath, config.Develop, cancellationToken) ? null : Loc.Format("GitError.BranchMissing", config.Develop);
    }

    public async Task<GitResult> SetGitFlowAsync(string repositoryPath, GitFlowConfig config, CancellationToken cancellationToken = default) =>
        await GitFlowProblemAsync(repositoryPath, config, cancellationToken) is { } problem
            ? new Failure(problem)
            : await WriteGitFlowKeysAsync(repositoryPath, config, cancellationToken);

    private async Task<GitResult> WriteGitFlowKeysAsync(string repositoryPath, GitFlowConfig config, CancellationToken cancellationToken)
    {
        (string Key, string Value)[] entries =
        [
            ("gitflow.branch.master", config.Main),
            ("gitflow.branch.develop", config.Develop),
            ("gitflow.prefix.feature", config.FeaturePrefix),
            ("gitflow.prefix.release", config.ReleasePrefix),
            ("gitflow.prefix.hotfix", config.HotfixPrefix),
            ("gitflow.prefix.versiontag", config.VersionTagPrefix),
        ];
        foreach (var (key, value) in entries)
        {
            var written = ToResult(await Cli.RunAsync(repositoryPath, ["config", key, value], cancellationToken));
            if (written.Value is not Done) return written;
        }
        return Done.Instance;
    }

    public async Task<GitResult> GitFlowStartAsync(
        string repositoryPath, GitFlowConfig config, GitFlowStartRequest request, CancellationToken cancellationToken = default)
    {
        if (RefNames.Check(request.Name) is { } invalid) return new Failure(invalid);
        var branch = config.BranchOf(request.Kind, request.Name);
        var start = config.BaseOf(request.Kind);

        if (!await BranchExistsAsync(repositoryPath, start, cancellationToken)) return new Failure(Loc.Format("GitError.BranchMissing", start));
        if (await BranchExistsAsync(repositoryPath, branch, cancellationToken)) return new Failure(Loc.Format("GitError.BranchExists", branch));
        if (request.Kind != GitFlowKind.Feature && await TagExistsAsync(repositoryPath, config.TagOf(request.Name), cancellationToken))
            return new Failure(Loc.Format("GitError.TagExistsChoose", config.TagOf(request.Name)));

        // `switch --create`: when git refuses to switch (local changes in the way) the branch is not created either.
        var result = ToResult(await Cli.RunAsync(repositoryPath, ["switch", "--no-track", "--create", branch, start], cancellationToken));
        return ExplainOperationFailure(result, null);
    }

    public async Task<GitResult<GitFlowFinished>> GitFlowFinishAsync(
        string repositoryPath, GitFlowConfig config, GitFlowFinishRequest request, CancellationToken cancellationToken = default)
    {
        var branch = config.BranchOf(request.Kind, request.Name);
        var tag = request.Kind == GitFlowKind.Feature ? null : config.TagOf(request.Name);

        foreach (var needed in new[] { config.Main, config.Develop, branch })
        {
            if (!await BranchExistsAsync(repositoryPath, needed, cancellationToken)) return new Failure(Loc.Format("GitError.BranchMissing", needed));
        }
        if (tag is not null && await TagExistsAsync(repositoryPath, tag, cancellationToken))
            return new Failure(Loc.Format("GitError.TagExistsDelete", tag));

        List<string> merged = [];
        GitResult<GitFlowFinished> Stop(GitResult result) => result switch
        {
            Done => new Failure(Loc.Get("Common.UnexpectedResult")),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
        };

        if (tag is null)
        {
            var development = await MergeNoFastForwardAsync(repositoryPath, config.Develop, branch, cancellationToken);
            if (development.Value is not Done) return Stop(development);
            merged.Add(config.Develop);
        }
        else
        {
            var production = await MergeNoFastForwardAsync(repositoryPath, config.Main, branch, cancellationToken);
            if (production.Value is not Done) return Stop(production);
            merged.Add(config.Main);

            var message = request.TagMessage is { Length: > 0 } text ? text : $"{(request.Kind == GitFlowKind.Release ? "Release" : "Hotfix")} {request.Name}";
            var tagged = ToResult(await Cli.RunAsync(repositoryPath, ["tag", "--annotate", "--message", message, tag], cancellationToken));
            if (tagged.Value is not Done) return Stop(tagged);

            // The tag, as git-flow does: the development branch gets the production merge commit too.
            var backMerge = await MergeNoFastForwardAsync(repositoryPath, config.Develop, "refs/tags/" + tag, cancellationToken);
            if (backMerge.Value is not Done) return Stop(backMerge);
            merged.Add(config.Develop);
        }

        var deleted = false;
        if (!request.KeepBranch)
        {
            // Safe delete: refuses a branch that is not merged into the checked-out one, which would mean a merge went wrong.
            var removal = ToResult(await Cli.RunAsync(repositoryPath, ["branch", "--delete", branch], cancellationToken));
            if (removal.Value is not Done) return Stop(removal);
            deleted = true;
        }
        return new Success<GitFlowFinished>(new GitFlowFinished(merged, tag, deleted));
    }

    /// <summary>Switches to <paramref name="target"/> and merges <paramref name="source"/> into it with a merge commit.</summary>
    private async Task<GitResult> MergeNoFastForwardAsync(string repositoryPath, string target, string source, CancellationToken cancellationToken)
    {
        var switched = ExplainOperationFailure(ToResult(await Cli.RunAsync(repositoryPath, ["switch", target], cancellationToken)), null);
        if (switched.Value is not Done) return switched;

        var merged = ToResult(await Cli.RunAsync(repositoryPath, ["merge", "--no-ff", "--no-edit", source], cancellationToken));
        return merged.Value is Done ? merged : await StoppedOnConflictsAsync(repositoryPath, merged, DisplayRevision(source), "merge");
    }

    private Task<bool> BranchExistsAsync(string repositoryPath, string name, CancellationToken cancellationToken) =>
        RefExistsAsync(repositoryPath, "refs/heads/" + name, cancellationToken);

    private Task<bool> TagExistsAsync(string repositoryPath, string name, CancellationToken cancellationToken) =>
        RefExistsAsync(repositoryPath, "refs/tags/" + name, cancellationToken);

    // `rev-parse --verify --quiet` exits with 1 when the reference does not exist: not an error.
    private async Task<bool> RefExistsAsync(string repositoryPath, string canonicalName, CancellationToken cancellationToken)
    {
        var result = await Cli.RunAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", canonicalName], 1, cancellationToken);
        return result is Success<CliOutput> { Value.ExitCode: 0 };
    }
}
