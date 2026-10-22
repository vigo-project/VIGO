using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;
using Vigo.Git.History;

namespace Vigo.Git;

/// <summary>
/// File history and blame (J5.6, ADR-042): <c>git log --follow</c> and <c>git blame --incremental</c> streamed from
/// git.exe; restore of an older version with <c>git cat-file --filters</c>.
/// </summary>
public sealed partial class GitService
{
    public async Task<GitResult> ReadFileHistoryAsync(
        string repositoryPath, string path, bool follow, int pageSize, FileHistoryPageHandler onPage, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        var reader = new FileHistoryReader(pageSize, onPage);
        try
        {
            return await Cli.RunStreamingAsync(top, FileHistoryReader.BuildArguments("HEAD", path, follow), reader.ReadAsync, cancellationToken);
        }
        catch (FormatException ex)
        {
            return new Failure($"Sortie de git log illisible : {ex.Message}", Exception: ex);
        }
    }

    public async Task<GitResult> BlameAsync(
        string repositoryPath, string path, string? revision, BlameHunkHandler onHunks, CancellationToken cancellationToken = default)
    {
        if (revision is not (null or "HEAD") && !IsRevision(revision)) return new Failure(Loc.Get("GitError.InvalidRef"));
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        var reader = new BlameReader(onHunks);
        GitResult result;
        try
        {
            result = await Cli.RunStreamingAsync(top, BlameReader.BuildArguments(revision, path), reader.ReadAsync, cancellationToken);
        }
        catch (FormatException ex)
        {
            return new Failure($"Sortie de git blame illisible : {ex.Message}", Exception: ex);
        }
        // "no such path 'x' in HEAD", "cannot stat path": the file is not in that version.
        return result is Failure f && (f.Message.Contains("no such path", StringComparison.Ordinal) || f.Message.Contains("cannot stat", StringComparison.Ordinal))
            ? f with { Message = Loc.Format("GitError.MissingInVersion", path) }
            : result;
    }

    public async Task<GitResult<IReadOnlyList<string>>> ListFilesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        var result = await Cli.RunAsync(top, ["ls-files", "-z"], cancellationToken);
        if (result is not Success<CliOutput> s) return Propagate<IReadOnlyList<string>>(result);
        return new Success<IReadOnlyList<string>>(s.Value.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task<GitResult> RestoreFileAsync(
        string repositoryPath, GitSha commit, string sourcePath, string path, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        var target = Path.GetFullPath(Path.Combine(top, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(top) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new Failure(Loc.Get("GitError.PathOutsideRepository"));

        // Written next to the file, then moved over it: a failure leaves the file as it was. --filters converts line
        // endings (and runs the smudge filters) as a checkout of the target path would.
        var temporary = target + ".vigo-restore";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            GitResult result;
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                result = await Cli.RunStreamingAsync(top, ["cat-file", "--filters", $"--path={path}", $"{commit}:{sourcePath}"],
                    (output, ct) => output.CopyToAsync(file, ct), cancellationToken);
            }
            if (result is not Done)
            {
                File.Delete(temporary);
                return result is Failure f && f.Message.Contains("does not exist", StringComparison.Ordinal)
                    ? f with { Message = Loc.Format("GitError.MissingInCommit", sourcePath, commit.ToShortString()) }
                    : result;
            }
            File.Move(temporary, target, overwrite: true);
            return Done.Instance;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Left behind: harmless, and overwritten by the next restore.
            }
            return new Failure(Loc.Format("GitError.CannotWrite", path, ex.Message), Exception: ex);
        }
    }
}
