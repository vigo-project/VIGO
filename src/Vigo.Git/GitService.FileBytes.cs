using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git;

/// <summary>Raw bytes of a file version, for the image diff (J5.7, ADR-043).</summary>
public sealed partial class GitService
{
    public async Task<GitResult<byte[]?>> ReadFileBytesAsync(string repositoryPath, FileVersion version, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        if (version.Source == FileVersionSource.WorkingTree)
        {
            var full = Path.Combine(top, version.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                return await Task.Run<GitResult<byte[]?>>(() =>
                {
                    var info = new FileInfo(full);
                    if (!info.Exists) return new Success<byte[]?>(null);
                    if (info.Length > DiffImages.MaxBytes) return TooLarge(version.Path);
                    return new Success<byte[]?>(File.ReadAllBytes(full));
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Canceled.Instance;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Failure(Loc.Format("GitError.CannotRead", version.Path, ex.Message), Exception: ex);
            }
        }

        // The size first: it also tells whether the path exists in that version (git fails otherwise).
        var spec = version.Source == FileVersionSource.Index ? $":{version.Path}" : $"{version.Revision}:{version.Path}";
        var sized = await Cli.RunAsync(top, ["cat-file", "-s", spec], cancellationToken);
        switch (sized)
        {
            case Failure f when IsMissing(f.Message):
                return new Success<byte[]?>(null);
            case Success<CliOutput> s:
                if (!long.TryParse(s.Value.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var size))
                    return new Failure(Loc.Format("GitError.SizeUnreadable", version.Path));
                if (size > DiffImages.MaxBytes) return TooLarge(version.Path);
                break;
            default:
                return Propagate<byte[]?>(sized);
        }

        using var buffer = new MemoryStream();
        var read = await Cli.RunStreamingAsync(top, ["cat-file", "blob", spec], (output, ct) => output.CopyToAsync(buffer, ct), cancellationToken);
        return read switch
        {
            Done => new Success<byte[]?>(buffer.ToArray()),
            Canceled c => c,
            Failure f when IsMissing(f.Message) => new Success<byte[]?>(null),
            Failure f => f,
            Conflict c => c,
            AuthenticationFailed a => a,
        };

        static bool IsMissing(string message) =>
            message.Contains("does not exist", StringComparison.Ordinal)
            || message.Contains("not in the index", StringComparison.Ordinal)
            || message.Contains("exists on disk, but not in", StringComparison.Ordinal)
            || message.Contains("Not a valid object name", StringComparison.Ordinal);

        static Failure TooLarge(string path) =>
            new(Loc.Format("GitError.ImageTooLarge", path, DiffImages.MaxBytes / (1024 * 1024)));
    }
}
