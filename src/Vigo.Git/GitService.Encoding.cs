using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Default encoding of the files and diffs shown (J7.29): <c>gui.encoding</c> of the repository's own config.</summary>
public sealed partial class GitService
{
    public async Task<GitResult<string?>> GetEncodingAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        // Exit code 1: the key is not set in the local config.
        var result = await Cli.RunAsync(repositoryPath, ["config", "--local", "--get", "gui.encoding"], 1, cancellationToken);
        return result is Success<CliOutput> s
            ? new Success<string?>(NullIfEmpty(s.Value.ExitCode == 0 ? s.Value.StandardOutput.Trim() : string.Empty))
            : Propagate<string?>(result);
    }

    public async Task<GitResult> SetEncodingAsync(string repositoryPath, string? encoding, CancellationToken cancellationToken = default)
    {
        if (encoding is not null && TextDecoding.Resolve(encoding) is null) return new Failure(Loc.Format("GitError.UnknownEncoding", encoding));

        // Exit code 5: nothing to remove.
        var result = encoding is null
            ? await Cli.RunAsync(repositoryPath, ["config", "--local", "--unset", "gui.encoding"], 5, cancellationToken)
            : await Cli.RunAsync(repositoryPath, ["config", "--local", "gui.encoding", encoding], cancellationToken);
        Cli.ForgetEncoding(repositoryPath);
        return ToResult(result);
    }
}
