using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>The staged changes as prompt material for the AI commit message (J7.9, ADR-070).</summary>
public sealed partial class GitService
{
    private const int RecentSubjectCount = 10;

    /// <summary>Generated or bulky files whose content tells nothing about the intent: kept in the diffstat, left out of the patch.</summary>
    private static readonly string[] PatchExcludes =
    [
        ":(exclude,glob)**/*.lock", ":(exclude,glob)**/package-lock.json", ":(exclude,glob)**/pnpm-lock.yaml",
        ":(exclude,glob)**/packages.lock.json", ":(exclude,glob)**/*.min.js", ":(exclude,glob)**/*.min.css", ":(exclude,glob)**/*.map",
        ":(exclude,glob)**/*.svg", ":(exclude,glob)**/*.g.cs", ":(exclude,glob)**/*.snap",
    ];

    public async Task<GitResult<StagedPatch>> GetStagedPatchAsync(string repositoryPath, int maxChars = 60_000, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        // No --literal-pathspecs here: the exclusion magic of PatchExcludes is a pathspec feature.
        string[] common = ["-c", "core.quotepath=false", "diff", "--cached", "--no-color", "--no-ext-diff", "--no-textconv", "-M"];

        var stat = await Cli.RunAsync(top, [.. common, "--stat=160,120"], cancellationToken);
        if (stat is not Success<CliOutput> statOutput) return Propagate<StagedPatch>(stat);
        if (statOutput.Value.StandardOutput.Trim().Length == 0) return new Success<StagedPatch>(new StagedPatch(string.Empty, false, []));

        var patch = await Cli.RunAsync(top, [.. common, "-U1", "--", ":/", .. PatchExcludes], cancellationToken);
        if (patch is not Success<CliOutput> patchOutput) return Propagate<StagedPatch>(patch);

        // A fresh repository has no HEAD: the log fails, which only means there is no style to follow.
        var log = await Cli.RunAsync(top, ["log", $"--max-count={RecentSubjectCount}", "--format=%s"], cancellationToken);
        IReadOnlyList<string> subjects = log is Success<CliOutput> logOutput
            ? logOutput.Value.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        var builder = new StringBuilder(statOutput.Value.StandardOutput.TrimEnd()).Append("\n\n");
        var remaining = Math.Max(0, maxChars - builder.Length);
        var body = patchOutput.Value.StandardOutput;
        var truncated = body.Length > remaining;
        builder.Append(truncated ? body[..remaining] : body);
        return new Success<StagedPatch>(new StagedPatch(builder.ToString(), truncated, subjects));
    }
}
