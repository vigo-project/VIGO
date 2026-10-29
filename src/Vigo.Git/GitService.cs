using System.Text;
using LibGit2Sharp;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;
using Vigo.Git.Commits;
using Vigo.Git.Conflicts;
using Vigo.Git.Log;
using Vigo.Git.Refs;
using Vigo.Git.Status;
using Conflict = Vigo.Core.Results.Conflict;
using StatusEntry = Vigo.Core.Models.StatusEntry;
using ResetMode = Vigo.Core.Models.ResetMode;

namespace Vigo.Git;

/// <summary>
/// <see cref="IGitService"/> implementation: LibGit2Sharp for reads,
/// git.exe for everything else and as a fallback when libgit2 cannot read the repository (reftable, SHA-256…).
/// </summary>
public sealed partial class GitService(GitCli cli) : IGitService
{
    /// <summary>The primary constructor parameter, for the other parts of the class.</summary>
    private GitCli Cli => cli;

    public async Task<GitResult<string>> GetGitVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await cli.RunAsync(null, ["--version"], cancellationToken);
        return result switch
        {
            Success<CliOutput> s => new Success<string>(s.Value.StandardOutput.Trim().Replace("git version ", "", StringComparison.Ordinal)),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
        };
    }

    public async Task<GitResult<RepositoryInfo>> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path)) return new Failure(Loc.Format("GitError.FolderMissing", path));

        GitResult<RepositoryInfo> fromLibGit2 = await Task.Run(() => OpenWithLibGit2(path), cancellationToken);
        if (fromLibGit2 is not Failure { Exception: not null }) return fromLibGit2;

        // libgit2 threw on a repository it did detect: ask git.exe instead.
        return await OpenWithCliAsync(path, cancellationToken);
    }

    public async Task<GitResult<RepositoryInfo>> InitAsync(string path, string initialBranch = "main", CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(path);
        var result = await cli.RunAsync(path, ["init", "--initial-branch", initialBranch], cancellationToken);
        return result is Success<CliOutput> ? await OpenAsync(path, cancellationToken) : Propagate<RepositoryInfo>(result);
    }

    public async Task<GitResult<GitIdentity>> GetIdentityAsync(string? repositoryPath = null, CancellationToken cancellationToken = default)
    {
        var nameTask = ReadConfigAsync(repositoryPath, "user.name", cancellationToken);
        var emailTask = ReadConfigAsync(repositoryPath, "user.email", cancellationToken);
        var name = await nameTask;
        var email = await emailTask;
        if (cancellationToken.IsCancellationRequested) return Canceled.Instance;
        return new Success<GitIdentity>(new GitIdentity(name, email));
    }

    public async Task<GitResult<RepositoryRefs>> GetRefsAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        GitResult<RepositoryRefs> fromLibGit2 = await Task.Run(() => ReadWithLibGit2(repositoryPath, RefReader.FromLibGit2), cancellationToken);
        if (fromLibGit2 is not Failure { Exception: not null }) return fromLibGit2;
        return await GetRefsWithCliAsync(repositoryPath, cancellationToken);
    }

    /// <summary>git.exe fallback for <see cref="GetRefsAsync"/>; internal so tests can compare both paths.</summary>
    internal async Task<GitResult<RepositoryRefs>> GetRefsWithCliAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var refsTask = cli.RunAsync(repositoryPath,
            ["for-each-ref", RefReader.ForEachRefFormat, "refs/heads", "refs/remotes", "refs/tags"], cancellationToken);
        var stashTask = cli.RunAsync(repositoryPath, ["stash", "list", RefReader.StashListFormat], cancellationToken);
        var headTask = cli.RunAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
        var branchTask = cli.RunAsync(repositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        await Task.WhenAll(refsTask, stashTask, headTask, branchTask);

        if (await refsTask is not Success<CliOutput> refs) return Propagate<RepositoryRefs>(await refsTask);
        if (cancellationToken.IsCancellationRequested) return Canceled.Instance;

        // An unborn HEAD or a detached HEAD makes rev-parse / symbolic-ref exit non-zero: not an error.
        var stashes = await stashTask is Success<CliOutput> s ? s.Value.StandardOutput : string.Empty;
        GitSha? head = await headTask is Success<CliOutput> h && GitSha.TryParse(h.Value.StandardOutput.Trim(), out var sha) ? sha : null;
        var branch = await branchTask is Success<CliOutput> b ? NullIfEmpty(b.Value.StandardOutput.Trim()) : null;
        return new Success<RepositoryRefs>(RefReader.FromCli(refs.Value.StandardOutput, stashes, head, branch));
    }

    public async Task<GitResult> ReadLogAsync(string repositoryPath, CommitLogOptions options, CommitPageHandler onPage, CancellationToken cancellationToken = default)
    {
        var reader = new CommitLogReader(options.PageSize, onPage);
        try
        {
            return await cli.RunStreamingAsync(repositoryPath, CommitLogReader.BuildArguments(options), reader.ReadAsync, cancellationToken);
        }
        catch (FormatException ex)
        {
            return new Failure($"Sortie de git log illisible : {ex.Message}", Exception: ex);
        }
    }

    /// <remarks>
    /// git.exe only (ADR-019): the line counts need a libgit2 patch, whose native line callback crashes the process
    /// under Native AOT (fail-fast, no managed handler). git.exe also covers reftable and SHA-256 repositories.
    /// </remarks>
    public async Task<GitResult<CommitDetails>> GetCommitAsync(string repositoryPath, GitSha sha, CancellationToken cancellationToken = default)
    {
        // One round of git.exe (each launch costs tens of ms): the diffs are asked against "<sha>^" (the first parent) before
        // the parents are known. A root commit has none: that attempt fails and the diffs are asked again below.
        var showTask = cli.RunAsync(repositoryPath, ["show", "-s", "--no-show-signature", CommitCliReader.ShowFormat, sha.ToString()], cancellationToken);
        var guessedStatus = cli.RunAsync(repositoryPath, CommitCliReader.DiffTreeAgainstFirstParent(sha, "--name-status"), cancellationToken);
        var guessedStat = cli.RunAsync(repositoryPath, CommitCliReader.DiffTreeAgainstFirstParent(sha, "--numstat"), cancellationToken);
        var show = await showTask;
        var guesses = (await guessedStatus, await guessedStat);
        if (show is not Success<CliOutput> s) return Propagate<CommitDetails>(show);

        try
        {
            var (id, parents, author, committer, message, signature) = CommitCliReader.ParseShow(s.Value.StandardOutput);
            GitSha? firstParent = parents.Length > 0 ? parents[0] : null;
            var nameStatusTask = firstParent is not null && guesses.Item1 is Success<CliOutput>
                ? Task.FromResult(guesses.Item1)
                : cli.RunAsync(repositoryPath, CommitCliReader.DiffTreeArguments(id, firstParent, "--name-status"), cancellationToken);
            var numstatTask = firstParent is not null && guesses.Item2 is Success<CliOutput>
                ? Task.FromResult(guesses.Item2)
                : cli.RunAsync(repositoryPath, CommitCliReader.DiffTreeArguments(id, firstParent, "--numstat"), cancellationToken);
            if (await nameStatusTask is not Success<CliOutput> nameStatus) return Propagate<CommitDetails>(await nameStatusTask);
            if (await numstatTask is not Success<CliOutput> numstat) return Propagate<CommitDetails>(await numstatTask);

            var files = CommitCliReader.ParseFiles(nameStatus.Value.StandardOutput, numstat.Value.StandardOutput);
            return new Success<CommitDetails>(new CommitDetails(id, parents, author, committer, message, files, signature));
        }
        catch (FormatException ex)
        {
            return new Failure($"Sortie de git show illisible : {ex.Message}", Exception: ex);
        }
    }

    public async Task<GitResult<FileDiff>> GetFileDiffAsync(
        string repositoryPath, GitSha sha, GitSha? firstParent, ChangedFile file, CancellationToken cancellationToken = default)
    {
        string[] paths = file.OldPath is { } old ? ["--", old, file.Path] : ["--", file.Path];
        string[] arguments = firstParent is { } parent
            ? ["--literal-pathspecs", "diff", .. DiffOptions, parent.ToString(), sha.ToString(), .. paths]
            : ["--literal-pathspecs", "diff-tree", "-p", "--root", "--no-commit-id", .. DiffOptions, sha.ToString(), .. paths];
        return await RunDiffAsync(repositoryPath, arguments, file.Path, file.OldPath, 0, cancellationToken);
    }

    public async Task<GitResult<string?>> ReadFileTextAsync(string repositoryPath, FileVersion version, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        if (version.Source == FileVersionSource.WorkingTree)
        {
            var encoding = await cli.EncodingOfAsync(top, cancellationToken);
            var full = Path.Combine(top, version.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                return await Task.Run<GitResult<string?>>(() =>
                {
                    var info = new FileInfo(full);
                    if (!info.Exists) return new Success<string?>(null);
                    if (info.Length > IGitService.MaxFileTextBytes) return TooLarge(version.Path);
                    return new Success<string?>(TextDecoding.Decode(File.ReadAllBytes(full), out _, encoding));
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Canceled.Instance;
            }
            catch (IOException ex)
            {
                return new Failure(Loc.Format("GitError.CannotRead", version.Path, ex.Message), Exception: ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return new Failure(Loc.Format("GitError.CannotRead", version.Path, ex.Message), Exception: ex);
            }
        }

        // cat-file --batch answers "<spec> missing" instead of failing when the path is not in that version.
        var spec = version.Source == FileVersionSource.Index ? $":{version.Path}" : $"{version.Revision}:{version.Path}";
        // The header first (--batch-check does not read the content): a huge blob must not be loaded to be refused.
        var checkedSpec = await cli.RunWithInputAsync(top, ["cat-file", "--batch-check"], spec + "\n", cancellationToken);
        if (checkedSpec is not Success<CliOutput> checkedOutput) return Propagate<string?>(checkedSpec);
        var header = checkedOutput.Value.StandardOutput.Split('\n')[0].Split(' ');
        // "<sha> <type> <size>"; anything else ("missing", a submodule commit…) means no file text.
        if (header.Length != 3 || header[1] != "blob") return new Success<string?>(null);
        if (long.TryParse(header[2], System.Globalization.CultureInfo.InvariantCulture, out var size) && size > IGitService.MaxFileTextBytes)
            return TooLarge(version.Path);

        var result = await cli.RunWithInputAsync(top, ["cat-file", "--batch"], spec + "\n", cancellationToken);
        if (result is not Success<CliOutput> s) return Propagate<string?>(result);

        var output = s.Value.StandardOutput;
        var content = output[(output.IndexOf('\n', StringComparison.Ordinal) + 1)..];
        // The content is followed by one newline of the batch format.
        return new Success<string?>(content.EndsWith('\n') ? content[..^1] : content);

        static Failure TooLarge(string path) =>
            new(Loc.Format("GitError.TextTooLarge", path, IGitService.MaxFileTextBytes / (1024 * 1024)));
    }

    public async Task<GitResult<WorkingTreeStatus>> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var gitDirectoryTask = GetGitDirectoryAsync(repositoryPath, cancellationToken);
        var result = await cli.RunAsync(repositoryPath, StatusReader.Arguments, cancellationToken);
        var gitDirectory = await gitDirectoryTask;
        if (result is not Success<CliOutput> s) return Propagate<WorkingTreeStatus>(result);

        WorkingTreeStatus status;
        try
        {
            status = StatusReader.Parse(s.Value.StandardOutput);
        }
        catch (FormatException ex)
        {
            return new Failure($"Sortie de git status illisible : {ex.Message}", Exception: ex);
        }

        if (gitDirectory is null) return new Success<WorkingTreeStatus>(status);
        var operation = await Task.Run(() => OperationReader.Read(gitDirectory), cancellationToken);
        return new Success<WorkingTreeStatus>(status with { Operation = operation });
    }

    public async Task<GitResult<FileDiff>> GetWorkingFileDiffAsync(
        string repositoryPath, StatusEntry entry, WorkingDiffSide side, CancellationToken cancellationToken = default)
    {
        // Status paths are relative to the top-level directory, pathspecs to the current one: run from the top level.
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;

        if (entry.IsUntracked && side == WorkingDiffSide.Unstaged)
        {
            // Unknown to the index: compare with an empty file.
            return await RunDiffAsync(top, ["diff", "--no-index", .. DiffOptions, "--", "/dev/null", entry.Path],
                entry.Path, null, maxSuccessExitCode: 1, cancellationToken);
        }

        var oldPath = side == WorkingDiffSide.Staged ? entry.OldPath : null;
        string[] paths = oldPath is { } old ? ["--", old, entry.Path] : ["--", entry.Path];
        string[] arguments = side == WorkingDiffSide.Staged
            ? ["--literal-pathspecs", "diff", "--cached", .. DiffOptions, .. paths]
            : ["--literal-pathspecs", "diff", .. DiffOptions, .. paths];
        return await RunDiffAsync(top, arguments, entry.Path, oldPath, 0, cancellationToken);
    }

    public async Task<GitResult<ConflictFile>> GetConflictAsync(string repositoryPath, StatusEntry entry, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        var path = entry.Path;
        var kind = entry.Conflict ?? ConflictKind.BothModified;
        var listed = await cli.RunAsync(top, ["--literal-pathspecs", "ls-files", "-u", "-z", "--", path], cancellationToken);
        if (listed is not Success<CliOutput> l) return Propagate<ConflictFile>(listed);

        var stages = ConflictReader.ParseStages(l.Value.StandardOutput, path);
        if (stages.Count == 0) return new Failure(Loc.Format("GitError.NoLongerConflicted", path));
        var @base = stages.FirstOrDefault(s => s.Stage == 1);
        var ours = stages.FirstOrDefault(s => s.Stage == 2);
        var theirs = stages.FirstOrDefault(s => s.Stage == 3);
        if (ours is null || theirs is null) return new Success<ConflictFile>(new ConflictFile(path, kind, ours is not null, theirs is not null, false, []));
        if (!ours.IsRegularFile || !theirs.IsRegularFile || @base is { IsRegularFile: false })
            return new Success<ConflictFile>(new ConflictFile(path, kind, true, true, IsBinary: true, []));

        // Added on both sides: merged against an empty base (the empty blob may not be in the object database yet).
        var baseId = @base?.ObjectId;
        if (baseId is null)
        {
            var empty = await cli.RunWithInputAsync(top, ["hash-object", "-w", "--stdin"], string.Empty, cancellationToken);
            if (empty is not Success<CliOutput> e) return Propagate<ConflictFile>(empty);
            baseId = e.Value.StandardOutput.Trim();
        }

        // Exit code: number of conflicts (capped at 127); negative (255) on error.
        // The version tells which merge-file the installed git has; the failure fallback below covers an unknown version.
        var merged = await IsLegacyMergeFileAsync(cancellationToken) ? await MergeFileLegacyAsync(top, path, cancellationToken) : await cli.RunAsync(top,
            ["merge-file", "-p", "--zdiff3", "--object-id", $"--marker-size={ConflictReader.MarkerSize}",
                "-L", "ours", "-L", "base", "-L", "theirs", ours.ObjectId, baseId, theirs.ObjectId],
            127, cancellationToken);
        if (merged is Failure f && $"{f.Message} {f.Details}".Contains("binary", StringComparison.OrdinalIgnoreCase))
            return new Success<ConflictFile>(new ConflictFile(path, kind, true, true, IsBinary: true, []));
        // An older git (--zdiff3 is git 2.35, --object-id git 2.43) has the same merge from temporary files.
        if (merged is Failure { } old && $"{old.Message} {old.Details}".Contains("unknown option", StringComparison.OrdinalIgnoreCase))
            merged = await MergeFileLegacyAsync(top, path, cancellationToken);
        if (merged is not Success<CliOutput> m) return Propagate<ConflictFile>(merged);

        try
        {
            var (segments, endsWithNewline, crlf) = ConflictReader.Parse(m.Value.StandardOutput);
            // The blobs have LF with core.autocrlf: the file git wrote in the working tree tells the line ending to keep.
            var lineEnding = crlf || await WorkingFileHasCrLfAsync(top, path, cancellationToken) ? "\r\n" : "\n";
            return new Success<ConflictFile>(new ConflictFile(path, kind, true, true, false, segments, lineEnding, endsWithNewline));
        }
        catch (FormatException ex)
        {
            return new Failure(Loc.Format("GitError.MergeUnreadable", path, ex.Message), Exception: ex);
        }
    }

    /// <summary>
    /// <c>merge-file</c> for a git older than 2.43 (no <c>--object-id</c>, and no <c>--zdiff3</c> before 2.35): the three
    /// stages are written to temporary files by <c>checkout-index</c>, then merged with <c>--diff3</c> (same markers).
    /// </summary>
    internal async Task<GitResult<CliOutput>> MergeFileLegacyAsync(string top, string path, CancellationToken cancellationToken)
    {
        var extracted = await cli.RunAsync(top, ["--literal-pathspecs", "checkout-index", "--stage=all", "--temp", "--", path], cancellationToken);
        if (extracted is not Success<CliOutput> x) return extracted;

        // "<stage 1> <stage 2> <stage 3>\t<path>": "." for a stage that does not exist (added on both sides: empty base).
        var names = x.Value.StandardOutput.Split('\t')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length != 3) return new Failure(Loc.Get("GitError.MergeFileUnsupported"));
        string[] files = [.. names.Where(n => n != ".").Select(n => Path.Combine(top, n))];
        try
        {
            var @base = names[0];
            if (@base == ".")
            {
                @base = $".merge_file_empty_{Guid.NewGuid():N}";
                await File.WriteAllBytesAsync(Path.Combine(top, @base), [], cancellationToken);
                files = [.. files, Path.Combine(top, @base)];
            }
            return await cli.RunAsync(top,
                ["merge-file", "-p", "--diff3", $"--marker-size={ConflictReader.MarkerSize}", "-L", "ours", "-L", "base", "-L", "theirs", names[1], @base, names[2]],
                127, cancellationToken);
        }
        finally
        {
            foreach (var file in files)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>The working-tree file uses CRLF line endings (first 64 KB); false when it cannot be read.</summary>
    private static Task<bool> WorkingFileHasCrLfAsync(string top, string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(top, path.Replace('/', Path.DirectorySeparatorChar)));
            var buffer = new byte[64 * 1024];
            var read = stream.Read(buffer);
            return buffer.AsSpan(0, read).IndexOf("\r\n"u8) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }, cancellationToken);

    public async Task<GitResult> ResolveConflictAsync(
        string repositoryPath, string path, ConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        switch (resolution)
        {
            case ConflictResolution.Content content:
            {
                var written = await WriteWorkingFileAsync(top, path, content.Text, cancellationToken);
                return written is Done ? await StageAsync(top, [path], cancellationToken) : written;
            }
            case ConflictResolution.TakeSide take:
            {
                var stage = take.Side == ConflictSide.Ours ? 2 : 3;
                var listed = await cli.RunAsync(top, ["--literal-pathspecs", "ls-files", "-u", "-z", "--", path], cancellationToken);
                if (listed is not Success<CliOutput> l) return ToResult(listed);
                // That side deleted the file: taking it means deleting it.
                if (ConflictReader.ParseStages(l.Value.StandardOutput, path).All(s => s.Stage != stage))
                    return await RunWithPathsAsync(top, ["rm", "--quiet", "--force", "--ignore-unmatch"], [path], cancellationToken);
                var checkout = await RunWithPathsAsync(top, ["checkout", take.Side == ConflictSide.Ours ? "--ours" : "--theirs"], [path], cancellationToken);
                return checkout is Done ? await StageAsync(top, [path], cancellationToken) : checkout;
            }
            case ConflictResolution.Delete:
                return await RunWithPathsAsync(top, ["rm", "--quiet", "--force", "--ignore-unmatch"], [path], cancellationToken);
            default:
                return new Failure(Loc.Get("GitError.UnknownResolution"));
        }
    }

    /// <summary>
    /// Writes a working-tree file in UTF-8, with a byte order mark if the file had one (the text read from git lost it).
    /// </summary>
    private static Task<GitResult> WriteWorkingFileAsync(string top, string path, string text, CancellationToken cancellationToken) => Task.Run<GitResult>(() =>
    {
        var full = Path.GetFullPath(Path.Combine(top, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(top) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new Failure(Loc.Format("GitError.PathRefused", path));
        try
        {
            var bom = false;
            if (File.Exists(full))
            {
                using var existing = File.OpenRead(full);
                Span<byte> start = stackalloc byte[3];
                bom = existing.Read(start) == 3 && start.SequenceEqual("﻿"u8);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text.StartsWith('﻿') ? text[1..] : text, new System.Text.UTF8Encoding(bom || text.StartsWith('﻿')));
            return Done.Instance;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Failure(Loc.Format("GitError.CannotWrite", path, ex.Message), Exception: ex);
        }
    }, cancellationToken);

    public async Task<GitResult> RunMergeToolAsync(string repositoryPath, string path, string? tool, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        // Without a tool, git would fall back to a terminal one (vimdiff) that has no console to run in.
        if (tool is null && await ReadConfigAsync(top, "merge.tool", cancellationToken) is null)
            return new Failure(Loc.Get("GitError.NoMergeTool"));

        // No .orig copy left next to the file; --no-prompt: no "Hit return to start merge resolution tool" question.
        List<string> arguments = ["-c", "mergetool.keepBackup=false", "mergetool", "--no-prompt"];
        if (tool is not null) arguments.Add($"--tool={tool}");
        arguments.AddRange(["--", path]);
        return ToResult(await cli.RunAsync(top, arguments, cancellationToken));
    }

    public Task<GitResult> StageAsync(string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        RunWithPathsAsync(repositoryPath, ["add", "-A"], paths, cancellationToken);

    public async Task<GitResult> StageAllAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        return ToResult(await cli.RunAsync(top, ["add", "-A", "--", "."], cancellationToken));
    }

    public Task<GitResult> UnstageAsync(string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        RunWithPathsAsync(repositoryPath, ["reset", "-q"], paths, cancellationToken);

    public async Task<GitResult> IgnoreAsync(string repositoryPath, IReadOnlyList<string> patterns, CancellationToken cancellationToken = default)
    {
        if (patterns.Count == 0) return Done.Instance;
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        return await Task.Run<GitResult>(() =>
        {
            var file = Path.Combine(top, ".gitignore");
            try
            {
                var existing = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
                var known = existing.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
                var added = patterns.Where(p => !known.Contains(p)).ToList();
                if (added.Count == 0) return Done.Instance;
                // The last line may lack its line break: do not glue the first new pattern to it.
                var separator = existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : string.Empty;
                File.AppendAllText(file, separator + string.Join(Environment.NewLine, added) + Environment.NewLine);
                return Done.Instance;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Failure(Loc.Format("GitError.CannotWrite", ".gitignore", ex.Message), Exception: ex);
            }
        }, cancellationToken);
    }

    public async Task<GitResult> DiscardAsync(string repositoryPath, IReadOnlyList<StatusEntry> entries, CancellationToken cancellationToken = default)
    {
        var tracked = entries.Where(e => !e.IsUntracked && !e.IsConflicted && e.IsUnstaged).Select(e => e.Path).ToList();
        if (tracked.Count > 0)
        {
            // Back to the index content: staged changes are kept.
            var restore = await RunWithPathsAsync(repositoryPath, ["restore", "--worktree"], tracked, cancellationToken);
            if (restore is not Done) return restore;
        }

        var untracked = entries.Where(e => e.IsUntracked).Select(e => e.Path).ToList();
        if (untracked.Count == 0) return Done.Instance;

        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        return await Task.Run<GitResult>(() =>
        {
            foreach (var path in untracked)
            {
                var full = Path.GetFullPath(Path.Combine(top, path.Replace('/', Path.DirectorySeparatorChar)));
                // Never outside the working tree, whatever the status said.
                if (!full.StartsWith(Path.TrimEndingDirectorySeparator(top) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return new Failure(Loc.Format("GitError.PathRefused", path));
                try
                {
                    // A nested repository (or a folder link) is listed as "sub/": never deleted from here, so not reported as done.
                    if (Directory.Exists(full)) return new Failure(Loc.Format("GitError.NestedRepository", path));
                    if (File.Exists(full)) File.Delete(full);
                    DeleteEmptyParents(Path.GetDirectoryName(full), top);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new Failure(Loc.Format("GitError.CannotDelete", path, ex.Message), Exception: ex);
                }
            }
            return Done.Instance;
        }, cancellationToken);
    }

    public async Task<GitResult> ApplyPatchAsync(
        string repositoryPath, string patch, PatchTarget target, bool reverse, CancellationToken cancellationToken = default)
    {
        // Patch paths are relative to the top level. --recount: hunk line counts of a partial patch are recomputed.
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        List<string> arguments = ["apply", "--recount", "--whitespace=nowarn"];
        if (target == PatchTarget.Index) arguments.Add("--cached");
        if (reverse) arguments.Add("--reverse");
        arguments.Add("-");
        return ToResult(await cli.RunWithInputAsync(top, arguments, patch, cancellationToken));
    }

    public async Task<GitResult<GitSha>> CommitAsync(string repositoryPath, CommitRequest request, CancellationToken cancellationToken = default)
    {
        // --file=-: any message (length, leading '-', quotes) without command-line escaping. Hooks, signing
        // (commit.gpgsign, gpg.format) and commit.cleanup come from the user's config.
        List<string> arguments = ["commit", "--quiet", "--file=-"];
        if (request.Amend) arguments.Add("--amend");
        if (request.SkipHooks || await SkipHooksAsync(repositoryPath, cancellationToken)) arguments.Add("--no-verify");
        foreach (var coAuthor in request.CoAuthors ?? [])
        {
            if (string.IsNullOrWhiteSpace(coAuthor.Name) || string.IsNullOrWhiteSpace(coAuthor.Email)) continue;
            arguments.Add($"--trailer=Co-authored-by: {coAuthor.Name.Trim()} <{coAuthor.Email.Trim()}>");
        }

        var commit = await cli.RunWithInputAsync(repositoryPath, arguments, request.Message, cancellationToken);
        if (commit is not Success<CliOutput>) return Propagate<GitSha>(commit);

        var head = await cli.RunAsync(repositoryPath, ["rev-parse", "--verify", "HEAD"], CancellationToken.None);
        return head is Success<CliOutput> h && GitSha.TryParse(h.Value.StandardOutput.Trim(), out var sha)
            ? new Success<GitSha>(sha)
            : new Failure(Loc.Get("GitError.HeadUnreadable"));
    }

    public async Task<GitResult> FetchAsync(string repositoryPath, IProgress<GitProgress>? progress, bool prune, CancellationToken cancellationToken = default) =>
        ExplainRemoteFailure(ToResult(await cli.RunWithProgressAsync(repositoryPath, PruneArguments(prune, ["fetch", "--all", "--progress"]), progress, cancellationToken)), null);

    public async Task<GitResult> FetchRemoteAsync(
        string repositoryPath, string remote, IProgress<GitProgress>? progress, bool prune, CancellationToken cancellationToken = default) =>
        remote.Length == 0 || remote.StartsWith('-')
            ? new Failure(Loc.Get("GitError.InvalidRemote"))
            : ExplainRemoteFailure(ToResult(await cli.RunWithProgressAsync(repositoryPath, PruneArguments(prune, ["fetch", "--progress", "--", remote]), progress, cancellationToken)), null);

    public async Task<GitResult> FetchInBackgroundAsync(string repositoryPath, bool prune, CancellationToken cancellationToken = default) =>
        ExplainRemoteFailure(ToResult(await cli.RunWithProgressAsync(
            repositoryPath, PruneArguments(prune, ["-c", "credential.interactive=false", "fetch", "--all", "--quiet"]), null, NonInteractive, cancellationToken)), null);

    /// <summary>Adds <c>--prune</c> when asked; otherwise nothing, so <c>fetch.prune</c> / <c>remote.&lt;name&gt;.prune</c> decide.</summary>
    private static List<string> PruneArguments(bool prune, List<string> arguments)
    {
        if (prune) arguments.Insert(arguments.IndexOf("fetch") + 1, "--prune");
        return arguments;
    }

    /// <summary>
    /// Environment of an automatic operation: Git Credential Manager shows no window, git asks no askpass program
    /// (an empty <c>GIT_ASKPASS</c> ends git's lookup) and OpenSSH asks for no passphrase.
    /// </summary>
    private static readonly Dictionary<string, string?> NonInteractive = new()
    {
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_ASKPASS"] = string.Empty,
        ["SSH_ASKPASS_REQUIRE"] = "never",
    };

    public async Task<GitResult<RepositoryInfo>> CloneAsync(
        CloneRequest request, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Url)) return new Failure(Loc.Get("GitError.RepositoryUrlRequired"));
        if (request.Url.StartsWith('-')) return new Failure(Loc.Format("GitError.InvalidUrl", request.Url));
        if (string.IsNullOrWhiteSpace(request.Directory) || !Path.IsPathFullyQualified(request.Directory))
            return new Failure(Loc.Get("GitError.DestinationFullPath"));

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Directory));
        bool existed;
        try
        {
            existed = await Task.Run(() => PrepareCloneDirectory(directory), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new Failure(ex is InvalidOperationException ? ex.Message : Loc.Format("GitError.CannotCreateFolder", directory, ex.Message));
        }
        catch (OperationCanceledException)
        {
            return Canceled.Instance;
        }

        List<string> arguments = ["clone", "--progress"];
        if (request.RecurseSubmodules) arguments.Add("--recurse-submodules");
        if (request.Shallow) arguments.AddRange(["--depth", "1"]);
        arguments.AddRange(["--", request.Url.Trim(), directory]);

        var result = ExplainCloneFailure(ToResult(await cli.RunWithProgressAsync(Path.GetDirectoryName(directory), arguments, progress, cancellationToken)));
        if (result.Value is Done) return await OpenAsync(directory, CancellationToken.None);

        // Failed or killed: git leaves a partial repository behind when it is killed.
        await Task.Run(() => RemoveCloneLeftovers(directory, existed), CancellationToken.None);
        return result switch
        {
            Done => new Failure(Loc.Get("Common.UnexpectedResult")),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
        };
    }

    /// <summary>Creates the destination; true when it already existed (empty). Throws when it is not empty.</summary>
    private static bool PrepareCloneDirectory(string directory)
    {
        if (File.Exists(directory))
            throw new InvalidOperationException(Loc.Format("GitError.DestinationIsFile", directory));
        if (Directory.Exists(directory))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidOperationException(Loc.Format("GitError.DestinationNotEmpty", directory));
            return true;
        }
        Directory.CreateDirectory(directory);
        return false;
    }

    /// <summary>Deletes what a failed clone wrote; the destination itself is kept when it existed before.</summary>
    private static void RemoveCloneLeftovers(string directory, bool existed)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var root = new DirectoryInfo(directory);
            // Pack files are read-only on Windows: clear the attribute or the deletion fails.
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes = FileAttributes.Normal;
            if (existed)
            {
                foreach (var entry in root.EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo d) d.Delete(recursive: true);
                    else entry.Delete();
                }
            }
            else
            {
                root.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held by an antivirus or an indexer: the folder is left for the user to remove.
        }
    }

    private static GitResult ExplainCloneFailure(GitResult result)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        string? message = text switch
        {
            _ when text.Contains("does not appear to be a git repository", StringComparison.Ordinal)
                   || text.Contains("not found", StringComparison.OrdinalIgnoreCase) && text.Contains("repository", StringComparison.OrdinalIgnoreCase) =>
                Loc.Get("GitError.CloneRepoNotFound"),
            _ when text.Contains("Could not resolve host", StringComparison.Ordinal) =>
                Loc.Get("GitError.ServerNotFound"),
            _ when text.Contains("Could not read from remote repository", StringComparison.Ordinal) =>
                Loc.Get("GitError.RemoteReadFailed"),
            _ => null,
        };
        return message is null ? result : failure with { Message = message, Details = text };
    }

    public async Task<GitResult> PullAsync(
        string repositoryPath, PullMode mode, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default)
    {
        // --no-edit: a merge commit never waits for an editor.
        List<string> arguments = ["pull", "--progress", "--no-edit"];
        switch (mode)
        {
            case PullMode.Merge: arguments.Add("--no-rebase"); break;
            case PullMode.Rebase: arguments.Add("--rebase"); break;
            case PullMode.FastForwardOnly: arguments.Add("--ff-only"); break;
            case PullMode.Default: break;
        }
        // GIT_TRACE=2 only to learn when the merge starts (see GitProgressPhase.Merging); GitCli drops the trace lines.
        return ExplainRemoteFailure(ToResult(await cli.RunWithProgressAsync(
            repositoryPath, arguments, progress, new Dictionary<string, string?> { ["GIT_TRACE"] = "2" }, cancellationToken)), null);
    }

    public async Task<GitResult> PushAsync(
        string repositoryPath, PushRequest request, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(repositoryPath, cancellationToken);
        if (status.Value is not Success<WorkingTreeStatus> { Value: var current }) return ToResult(status);
        if (current.Branch is not { } branch) return new Failure(Loc.Get("GitError.DetachedHeadPush"));

        List<string> arguments = ["push", "--progress"];
        if (await SkipHooksAsync(repositoryPath, cancellationToken)) arguments.Add("--no-verify");
        // --force-if-includes: the lease alone is satisfied by a background fetch that brought a colleague's commits we never integrated.
        if (request.ForceWithLease) arguments.AddRange(["--force-with-lease", "--force-if-includes"]);
        if (current.Upstream is null)
        {
            // First push of the branch: same name on the only remote (or origin), which becomes its upstream.
            var remotes = await cli.RunAsync(repositoryPath, ["remote"], cancellationToken);
            if (remotes is not Success<CliOutput> r) return ToResult(remotes);
            var names = r.Value.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var remote = names.Contains("origin") ? "origin" : names.Length == 1 ? names[0] : null;
            if (remote is null)
            {
                return new Failure(names.Length == 0
                    ? Loc.Get("GitError.NoRemote")
                    : Loc.Format("GitError.NoUpstreamManyRemotes", branch, string.Join(", ", names)));
            }
            arguments.AddRange(["--set-upstream", remote, "HEAD"]);
        }

        var result = ToResult(await cli.RunWithProgressAsync(repositoryPath, arguments, progress, cancellationToken));
        return ExplainRemoteFailure(result, current.Upstream ?? branch);
    }

    public async Task<GitResult> CheckoutAsync(string repositoryPath, CheckoutTarget target, CancellationToken cancellationToken = default)
    {
        // Ref names cannot start with '-' (check-ref-format), but a crafted name must not become an option.
        string[] arguments = target switch
        {
            CheckoutBranch b when !b.Name.StartsWith('-') => ["switch", "--quiet", "--no-guess", b.Name],
            CheckoutRemoteBranch r when !r.LocalName.StartsWith('-') && !r.RemoteBranch.StartsWith('-') =>
                ["switch", "--quiet", "--create", r.LocalName, "--track", "refs/remotes/" + r.RemoteBranch],
            CheckoutCommit c => ["switch", "--quiet", "--detach", c.Commit.ToString()],
            _ => [],
        };
        if (arguments.Length == 0) return new Failure(Loc.Get("GitError.InvalidBranchName"));

        var result = ToResult(await cli.RunAsync(repositoryPath, arguments, cancellationToken));
        return ExplainCheckoutFailure(result, target);
    }

    /// <summary>Rewrites the usual checkout refusals (local changes, conflicts, existing branch) in French.</summary>
    private static GitResult ExplainCheckoutFailure(GitResult result, CheckoutTarget target)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        var name = target switch
        {
            CheckoutBranch b => b.Name,
            CheckoutRemoteBranch r => r.LocalName,
            CheckoutCommit c => c.Commit.ToShortString(),
        };

        string? message = text switch
        {
            _ when text.Contains("would be overwritten by checkout", StringComparison.Ordinal) =>
                Loc.Format("GitError.CheckoutOverwrites", name),
            _ when text.Contains("you need to resolve your current index first", StringComparison.Ordinal)
                   || text.Contains("You are in the middle of", StringComparison.Ordinal) =>
                Loc.Get("GitError.OperationBeforeSwitch"),
            _ when text.Contains("already exists", StringComparison.Ordinal) =>
                Loc.Format("GitError.LocalBranchExists", name),
            _ when text.Contains("is already used by worktree", StringComparison.Ordinal)
                   || text.Contains("is already checked out at", StringComparison.Ordinal) =>
                Loc.Format("GitError.BranchInOtherWorktree", name),
            _ => null,
        };
        if (message is null) return result;
        var overwritten = text.Contains("would be overwritten by checkout", StringComparison.Ordinal);
        return failure with { Message = message, Details = text, Reason = overwritten ? FailureReason.LocalChangesWouldBeOverwritten : FailureReason.Unknown };
    }

    public async Task<GitResult<bool>> IsAncestorAsync(
        string repositoryPath, GitSha ancestor, GitSha descendant, CancellationToken cancellationToken = default)
    {
        // Exit code 0: ancestor (or the same commit), 1: not an ancestor, anything else: error.
        var result = await cli.RunAsync(repositoryPath, ["merge-base", "--is-ancestor", ancestor.ToString(), descendant.ToString()], 1, cancellationToken);
        return result is Success<CliOutput> s ? new Success<bool>(s.Value.ExitCode == 0) : Propagate<bool>(result);
    }

    public async Task<GitResult<MergeOutcome>> MergeAsync(string repositoryPath, string source, CancellationToken cancellationToken = default)
    {
        if (!IsRevision(source)) return new Failure(Loc.Get("GitError.InvalidRef"));
        // --no-edit: the default message, never an editor. Fast-forward or merge commit: merge.ff of the user decides.
        // Shortest unambiguous name: the default message says "Merge branch 'feature'", not "'refs/heads/feature'".
        var name = await ShortRevisionAsync(repositoryPath, source, cancellationToken);
        var result = await cli.RunAsync(repositoryPath, ["merge", "--no-edit", name], cancellationToken);
        if (result is Success<CliOutput> s)
        {
            var output = s.Value.StandardOutput + s.Value.StandardError;
            return new Success<MergeOutcome>(
                output.Contains("Already up to date", StringComparison.Ordinal) ? MergeOutcome.UpToDate
                : output.Contains("Fast-forward", StringComparison.Ordinal) ? MergeOutcome.FastForward
                : MergeOutcome.MergeCommit);
        }

        var stopped = await StoppedOnConflictsAsync(repositoryPath, ToResult(result), DisplayRevision(source), "merge");
        return stopped switch
        {
            Done => new Failure(Loc.Get("Common.UnexpectedResult")),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
        };
    }

    public async Task<GitResult> FastForwardBranchAsync(
        string repositoryPath, string branch, GitSha target, CancellationToken cancellationToken = default)
    {
        if (branch.Length == 0 || branch.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidBranchName"));
        // Fetch from the repository itself: git updates the branch only when it is a fast-forward and refuses a branch
        // checked out in any worktree (whose files would no longer match).
        string[] arguments = ["fetch", "--no-tags", "--no-write-fetch-head", ".", $"{target}:refs/heads/{branch}"];
        var result = ToResult(await cli.RunAsync(repositoryPath, arguments, cancellationToken));
        if (result.Value is not Failure failure) return result;

        var text = failure.Details ?? failure.Message;
        string? message = text switch
        {
            _ when text.Contains("non-fast-forward", StringComparison.Ordinal) || text.Contains("[rejected]", StringComparison.Ordinal) =>
                Loc.Format("GitError.CannotFastForward", branch, target.ToShortString()),
            _ when text.Contains("refusing to fetch into", StringComparison.Ordinal) || text.Contains("checked out", StringComparison.Ordinal) =>
                Loc.Format("GitError.BranchCheckedOutMerge", branch),
            _ => null,
        };
        return message is null ? result : failure with { Message = message, Details = text };
    }

    public async Task<GitResult<RebaseOutcome>> RebaseAsync(
        string repositoryPath, string upstream, string? branch, CancellationToken cancellationToken = default)
    {
        if (!IsRevision(upstream) || branch is { } name && (name.Length == 0 || name.StartsWith('-'))) return new Failure(Loc.Get("GitError.InvalidRef"));
        // <branch> by its short name: git switches to it first (a full ref name would detach HEAD).
        string[] arguments = branch is null ? ["rebase", upstream] : ["rebase", upstream, branch];
        var result = await cli.RunAsync(repositoryPath, arguments, cancellationToken);
        if (result is Success<CliOutput> s)
        {
            var output = s.Value.StandardOutput + s.Value.StandardError;
            return new Success<RebaseOutcome>(output.Contains("is up to date", StringComparison.Ordinal) ? RebaseOutcome.UpToDate : RebaseOutcome.Rebased);
        }

        var stopped = await StoppedOnConflictsAsync(repositoryPath, ToResult(result), DisplayRevision(upstream), "rebase");
        return stopped switch
        {
            Done => new Failure(Loc.Get("Common.UnexpectedResult")),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
        };
    }

    public async Task<GitResult> ContinueOperationAsync(
        string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default)
    {
        // The prepared message (replayed commit, MERGE_MSG) is kept: GIT_EDITOR=true (GitCli) accepts it as is.
        var command = OperationCommand(kind);
        var result = ToResult(await cli.RunAsync(repositoryPath, [command, "--continue"], cancellationToken));
        return await StoppedOnConflictsAsync(repositoryPath, result, null, command);
    }

    public async Task<GitResult> SkipOperationAsync(
        string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default)
    {
        if (kind == RepositoryOperationKind.Merge) return new Failure(Loc.Get("GitError.MergeCannotSkip"));
        var command = OperationCommand(kind);
        var result = ToResult(await cli.RunAsync(repositoryPath, [command, "--skip"], cancellationToken));
        return await StoppedOnConflictsAsync(repositoryPath, result, null, command);
    }

    public Task<GitResult<GitSha>> CherryPickAsync(string repositoryPath, GitSha commit, CancellationToken cancellationToken = default) =>
        ApplyCommitAsync(repositoryPath, commit, RepositoryOperationKind.CherryPick, cancellationToken);

    public Task<GitResult<GitSha>> RevertAsync(string repositoryPath, GitSha commit, CancellationToken cancellationToken = default) =>
        ApplyCommitAsync(repositoryPath, commit, RepositoryOperationKind.Revert, cancellationToken);

    /// <summary>Cherry-pick or revert of one commit; returns the new HEAD.</summary>
    private async Task<GitResult<GitSha>> ApplyCommitAsync(
        string repositoryPath, GitSha commit, RepositoryOperationKind kind, CancellationToken cancellationToken)
    {
        var command = OperationCommand(kind);
        List<string> arguments = [command];
        // Revert: the default "Revert "subject"" message, never an editor. A merge commit is taken against its first
        // parent (the changes the merge brought into its branch).
        if (kind == RepositoryOperationKind.Revert) arguments.Add("--no-edit");
        if (await IsMergeCommitAsync(repositoryPath, commit, cancellationToken)) arguments.AddRange(["--mainline", "1"]);
        arguments.Add(commit.ToString());

        var result = ToResult(await cli.RunAsync(repositoryPath, arguments, cancellationToken));
        if (result.Value is Done)
        {
            var head = await cli.RunAsync(repositoryPath, ["rev-parse", "HEAD"], CancellationToken.None);
            return head is Success<CliOutput> s && GitSha.TryParse(s.Value.StandardOutput.Trim(), out var sha)
                ? new Success<GitSha>(sha)
                : new Failure(Loc.Get("GitError.HeadUnreadable"));
        }

        var stopped = await StoppedOnConflictsAsync(repositoryPath, result, commit.ToShortString(), command);
        if (stopped.Value is Failure failure && await IsEmptyApplicationAsync(repositoryPath, failure))
        {
            // Nothing left to commit (changes already there): git stops anyway and keeps CHERRY_PICK_HEAD / REVERT_HEAD.
            // Nothing was changed, so the operation is abandoned at once rather than left in progress.
            await cli.RunAsync(repositoryPath, [command, "--abort"], CancellationToken.None);
            return failure with
            {
                Message = kind == RepositoryOperationKind.CherryPick
                    ? Loc.Format("GitError.NothingToApply", commit.ToShortString())
                    : Loc.Format("GitError.NothingToRevert", commit.ToShortString()),
                Details = failure.Details ?? failure.Message,
            };
        }

        return stopped switch
        {
            Done => new Failure(Loc.Get("Common.UnexpectedResult")),
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
        };
    }

    /// <summary>
    /// The cherry-pick or revert stopped without conflicts because its result is empty ("The previous cherry-pick is now
    /// empty", "nothing to commit"), leaving the operation in progress.
    /// </summary>
    private async Task<bool> IsEmptyApplicationAsync(string repositoryPath, Failure failure)
    {
        var text = failure.Details ?? failure.Message;
        if (!text.Contains("is now empty", StringComparison.Ordinal) && !text.Contains("nothing to commit", StringComparison.Ordinal)) return false;
        var status = await GetStatusAsync(repositoryPath, CancellationToken.None);
        return status.Value is Success<WorkingTreeStatus> { Value.Operation.Kind: RepositoryOperationKind.CherryPick or RepositoryOperationKind.Revert };
    }

    private async Task<bool> IsMergeCommitAsync(string repositoryPath, GitSha commit, CancellationToken cancellationToken)
    {
        // "<sha> <parent1> <parent2>…"
        var result = await cli.RunAsync(repositoryPath, ["rev-list", "--no-walk", "--parents", commit.ToString()], cancellationToken);
        return result is Success<CliOutput> s && s.Value.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 2;
    }

    public async Task<GitResult> ResetAsync(string repositoryPath, GitSha target, ResetMode mode, CancellationToken cancellationToken = default)
    {
        var option = mode switch
        {
            ResetMode.Soft => "--soft",
            ResetMode.Mixed => "--mixed",
            ResetMode.Hard => "--hard",
            ResetMode.Keep => "--keep",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        // "--": the SHA is a revision, never a path.
        var result = ToResult(await cli.RunAsync(repositoryPath, ["reset", "--quiet", option, target.ToString(), "--"], cancellationToken));
        if (result.Value is not Failure failure) return result;

        var text = failure.Details ?? failure.Message;
        if (text.Contains("not uptodate", StringComparison.Ordinal) || text.Contains("would be overwritten", StringComparison.Ordinal))
        {
            return failure with
            {
                Message = Loc.Get("GitError.UncommittedWouldBeOverwritten"),
                Details = text,
                Reason = FailureReason.LocalChangesWouldBeOverwritten,
            };
        }

        string? message = text switch
        {
            _ when text.Contains("Cannot do a soft reset in the middle of a merge", StringComparison.Ordinal) =>
                Loc.Get("GitError.SoftResetDuringMerge"),
            _ when text.Contains("Cannot do a", StringComparison.Ordinal) && text.Contains("reset in a bare repository", StringComparison.Ordinal) =>
                Loc.Get("GitError.ResetNeedsWorkTree"),
            _ when text.Contains("unable to unlink", StringComparison.Ordinal) || text.Contains("Permission denied", StringComparison.Ordinal) =>
                Loc.Get("GitError.FilesLocked"),
            _ => null,
        };
        return message is null ? result : failure with { Message = message, Details = text };
    }

    public async Task<GitResult<RefSnapshot>> GetRefSnapshotAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        // objectname is not peeled: an annotated tag keeps its tag object. upstream:remotename / remoteref read the
        // branch.<name>.remote / merge settings, whether the remote-tracking branch exists or not.
        var refsTask = cli.RunAsync(repositoryPath,
            ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(upstream:remotename)%00%(upstream:remoteref)",
             RefSnapshot.BranchPrefix, RefSnapshot.TagPrefix], cancellationToken);
        // Exit code 1: detached HEAD (symbolic-ref), unborn HEAD (rev-parse).
        var branchTask = cli.RunAsync(repositoryPath, ["symbolic-ref", "--quiet", "HEAD"], 1, cancellationToken);
        var headTask = cli.RunAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"], 1, cancellationToken);

        if (await refsTask is not Success<CliOutput> refs) return Propagate<RefSnapshot>(await refsTask);
        if (await branchTask is not Success<CliOutput> branch) return Propagate<RefSnapshot>(await branchTask);
        if (await headTask is not Success<CliOutput> head) return Propagate<RefSnapshot>(await headTask);

        var states = new Dictionary<string, RefState>(StringComparer.Ordinal);
        foreach (var line in refs.Value.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\0');
            if (fields.Length < 4 || !GitSha.TryParse(fields[1], out var target)) continue;
            states[fields[0]] = new RefState(target, NullIfEmpty(fields[2]), NullIfEmpty(fields[3]));
        }

        var symbolic = branch.Value.StandardOutput.Trim();
        var headBranch = symbolic.StartsWith(RefSnapshot.BranchPrefix, StringComparison.Ordinal) ? symbolic[RefSnapshot.BranchPrefix.Length..] : null;
        GitSha? headSha = GitSha.TryParse(head.Value.StandardOutput.Trim(), out var sha) ? sha : null;
        return new Success<RefSnapshot>(new RefSnapshot(headBranch, headSha, states));
    }

    public async Task<GitResult> RestoreRefsAsync(
        string repositoryPath, RefSnapshot current, RefSnapshot target, ResetMode headMode, CancellationToken cancellationToken = default)
    {
        if (headMode == ResetMode.Hard) throw new ArgumentOutOfRangeException(nameof(headMode), "Undo never discards local changes.");
        const string Branches = RefSnapshot.BranchPrefix;

        var changed = RefSnapshot.ChangedRefs(current, target).ToHashSet(StringComparer.Ordinal);
        var created = changed.Where(n => !current.Refs.ContainsKey(n)).ToHashSet(StringComparer.Ordinal);
        var deleted = changed.Where(n => !target.Refs.ContainsKey(n)).ToHashSet(StringComparer.Ordinal);
        var headBranch = current.HeadBranch;

        // A renamed branch (one gone, one appeared on the same commit): renamed back, its reflog and upstream follow.
        var goneBranches = deleted.Where(n => n.StartsWith(Branches, StringComparison.Ordinal)).ToList();
        var newBranches = created.Where(n => n.StartsWith(Branches, StringComparison.Ordinal)).ToList();
        if (goneBranches is [var gone] && newBranches is [var appeared] && current.Refs[gone].Target == target.Refs[appeared].Target)
        {
            var (from, to) = (gone[Branches.Length..], appeared[Branches.Length..]);
            var renamed = await RenameBranchAsync(repositoryPath, from, to, cancellationToken);
            if (renamed.Value is not Done) return renamed;
            if (headBranch == from) headBranch = to;
            foreach (var name in (string[])[gone, appeared])
            {
                changed.Remove(name);
                created.Remove(name);
                deleted.Remove(name);
            }
        }

        // HEAD on another branch (or detached): checkout, which keeps local changes or refuses.
        var head = current.Head;
        if (target.HeadBranch != headBranch)
        {
            GitResult switched;
            if (target.HeadBranch is { } branch)
            {
                var name = Branches + branch;
                if (!current.Refs.ContainsKey(name) && target.Refs.TryGetValue(name, out var recreated))
                {
                    var restored = ToResult(await cli.RunAsync(repositoryPath, ["update-ref", name, recreated.Target.ToString(), ""], cancellationToken));
                    if (restored.Value is not Done) return restored;
                    changed.Remove(name);
                }
                switched = target.Head is null
                    ? ToResult(await cli.RunAsync(repositoryPath, ["symbolic-ref", "HEAD", name], cancellationToken))
                    : await CheckoutAsync(repositoryPath, new CheckoutBranch(branch), cancellationToken);
                head = current.Refs.TryGetValue(name, out var state) ? state.Target : target.Head;
            }
            else
            {
                switched = await CheckoutAsync(repositoryPath, new CheckoutCommit(target.Head!.Value), cancellationToken);
                head = target.Head;
            }
            if (switched.Value is not Done) return switched;
            headBranch = target.HeadBranch;
        }

        // Checked-out commit: git reset (the index and the files follow according to the mode).
        var headRef = headBranch is null ? null : Branches + headBranch;
        if (headRef is not null) changed.Remove(headRef);
        if (target.Head is { } targetHead && head != targetHead)
        {
            var reset = await ResetAsync(repositoryPath, targetHead, headMode, cancellationToken);
            if (reset.Value is not Done) return reset;
        }
        else if (target.Head is null && headRef is not null && current.Refs.TryGetValue(headRef, out var unborn))
        {
            // Back before the first commit: the branch becomes unborn again, its files stay staged.
            var emptied = ToResult(await cli.RunAsync(repositoryPath, ["update-ref", "-d", headRef, unborn.Target.ToString()], cancellationToken));
            if (emptied.Value is not Done) return emptied;
        }

        // The other branches and tags: one transaction, each one checked against the value it is expected to have.
        var commands = new System.Text.StringBuilder();
        foreach (var name in changed.Order(StringComparer.Ordinal))
        {
            var (from, to) = (current.Refs.GetValueOrDefault(name), target.Refs.GetValueOrDefault(name));
            commands.Append((from, to) switch
            {
                ({ } f, { } t) => $"update {name} {t.Target} {f.Target}\n",
                (null, { } t) => $"create {name} {t.Target}\n",
                ({ } f, null) => $"delete {name} {f.Target}\n",
                _ => string.Empty,
            });
        }
        if (commands.Length > 0)
        {
            var updated = ToResult(await cli.RunWithInputAsync(repositoryPath, ["update-ref", "--stdin"], commands.ToString(), cancellationToken));
            if (updated.Value is Failure failure)
            {
                return failure with { Message = Loc.Get("GitError.RefsChangedMeanwhile"), Details = failure.Details ?? failure.Message };
            }
            if (updated.Value is not Done) return updated;
        }

        // Upstream settings: removed with a branch deleted again, put back with a branch restored.
        foreach (var name in deleted.Where(n => n.StartsWith(Branches, StringComparison.Ordinal) && current.Refs[n].UpstreamRemote is not null))
            await cli.RunAsync(repositoryPath, ["config", "--remove-section", "branch." + name[Branches.Length..]], cancellationToken);
        foreach (var name in created.Where(n => n.StartsWith(Branches, StringComparison.Ordinal)))
        {
            if (target.Refs[name] is not { UpstreamRemote: { } remote, UpstreamMerge: { } merge }) continue;
            var section = "branch." + name[Branches.Length..];
            await cli.RunAsync(repositoryPath, ["config", section + ".remote", remote], cancellationToken);
            await cli.RunAsync(repositoryPath, ["config", section + ".merge", merge], cancellationToken);
        }
        return Done.Instance;
    }

    public async Task<GitResult<int>> CountCommitsAsync(
        string repositoryPath, GitSha since, GitSha until, CancellationToken cancellationToken = default)
    {
        var result = await cli.RunAsync(repositoryPath, ["rev-list", "--count", $"{since}..{until}"], cancellationToken);
        return result is Success<CliOutput> s
            ? int.TryParse(s.Value.StandardOutput.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                ? new Success<int>(count)
                : new Failure(Loc.Get("GitError.RevListUnexpected"))
            : Propagate<int>(result);
    }

    private static string OperationCommand(RepositoryOperationKind kind) => kind switch
    {
        RepositoryOperationKind.Merge => "merge",
        RepositoryOperationKind.Rebase => "rebase",
        RepositoryOperationKind.CherryPick => "cherry-pick",
        RepositoryOperationKind.Revert => "revert",
        RepositoryOperationKind.ApplyMailbox => "am",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public async Task<GitResult> AbortOperationAsync(
        string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default)
    {
        var command = OperationCommand(kind);
        var result = ToResult(await cli.RunAsync(repositoryPath, [command, "--abort"], cancellationToken));
        // Already finished or aborted elsewhere (terminal): what was asked is done.
        if (result.Value is Failure f && (f.Details ?? f.Message) is var text
            && (text.Contains("There is no merge to abort", StringComparison.Ordinal)
                || text.Contains("No rebase in progress", StringComparison.OrdinalIgnoreCase)
                || text.Contains("no cherry-pick or revert in progress", StringComparison.Ordinal)
                || text.Contains("not resuming", StringComparison.Ordinal)))
        {
            return Done.Instance;
        }
        return result;
    }

    public async Task<GitResult<GitSha>> StashAsync(string repositoryPath, StashRequest request, CancellationToken cancellationToken = default)
    {
        var before = await StashTopAsync(repositoryPath, cancellationToken);
        List<string> arguments = ["stash", "push"];
        if (request.IncludeUntracked) arguments.Add("--include-untracked");
        // --message=: a message starting with '-' stays a message.
        // A description follows a blank line: the commit keeps it, the stash list shows the subject only.
        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            var description = request.Description?.ReplaceLineEndings("\n").Trim();
            arguments.Add("--message=" + request.Message.Trim() + (string.IsNullOrEmpty(description) ? "" : "\n\n" + description));
        }
        // Paths go on stdin like everywhere else (--literal-pathspecs: a folder name is a prefix, not a pattern).
        var result = request.Paths is { Count: > 0 } paths
            ? ToResult(await cli.RunWithInputAsync(repositoryPath, ["--literal-pathspecs", .. arguments, "--pathspec-from-file=-", "--pathspec-file-nul"],
                string.Join('\0', paths) + '\0', cancellationToken))
            : ToResult(await cli.RunAsync(repositoryPath, arguments, cancellationToken));
        switch (result.Value)
        {
            case Done: break;
            case Conflict c: return c;
            case AuthenticationFailed a: return a;
            case Canceled c: return c;
            case Failure f:
                var text = f.Details ?? f.Message;
                return text.Contains("You do not have the initial commit yet", StringComparison.Ordinal)
                    ? f with { Message = Loc.Get("GitError.StashBeforeFirstCommit"), Details = text }
                    : text.Contains("needs merge", StringComparison.Ordinal) || text.Contains("could not save index tree", StringComparison.OrdinalIgnoreCase)
                        ? f with { Message = Loc.Get("GitError.StashConflicts"), Details = text }
                        : f;
        }

        // "No local changes to save" exits with 0: the stash list tells whether something was stashed.
        var after = await StashTopAsync(repositoryPath, cancellationToken);
        return after is { } sha && after != before
            ? new Success<GitSha>(sha)
            : new Failure(Loc.Get(request.IncludeUntracked ? "GitError.NothingToStash" : "GitError.NothingTrackedToStash"));
    }

    public async Task<GitResult> ApplyStashAsync(string repositoryPath, GitSha stash, bool drop, CancellationToken cancellationToken = default)
    {
        if (await StashReferenceAsync(repositoryPath, stash, cancellationToken) is not { } reference) return StashGone;
        var result = ToResult(await cli.RunAsync(repositoryPath, ["stash", drop ? "pop" : "apply", reference], cancellationToken));
        if (result.Value is Done or Canceled or AuthenticationFailed) return result;

        var text = result.Value switch
        {
            Failure f => f.Details ?? f.Message,
            _ => string.Empty,
        };
        string? message = text switch
        {
            _ when text.Contains("would be overwritten", StringComparison.Ordinal) =>
                Loc.Get("GitError.StashOverwrites"),
            _ when text.Contains("already exists, no checkout", StringComparison.Ordinal) =>
                Loc.Get("GitError.StashUntrackedExist"),
            _ when text.Contains("needs merge", StringComparison.Ordinal) || text.Contains("Cannot apply a stash in the middle of a merge", StringComparison.Ordinal) =>
                Loc.Get("GitError.StashApplyConflicts"),
            _ => null,
        };
        if (message is not null && result.Value is Failure refused)
        {
            return refused with
            {
                Message = message,
                Details = text,
                Reason = text.Contains("would be overwritten", StringComparison.Ordinal) ? FailureReason.LocalChangesWouldBeOverwritten : FailureReason.Unknown,
            };
        }

        // Applied with conflicts (git keeps the stash): every unmerged path, content conflicts or not.
        var status = await GetStatusAsync(repositoryPath, CancellationToken.None);
        if (status.Value is Success<WorkingTreeStatus> { Value: var current }
            && current.Entries.Where(e => e.IsConflicted).Select(e => e.Path).ToList() is { Count: > 0 } paths)
        {
            return new Conflict(paths, "stash");
        }
        return result;
    }

    /// <summary>
    /// The sections ("diff --git a/x b/y") of <paramref name="patch"/> that concern <paramref name="paths"/>. A header git quotes
    /// (a name with a quote or a tab) cannot be read here: its section is dropped.
    /// </summary>
    internal static string FilterPatch(string patch, IReadOnlyCollection<string> paths)
    {
        const string Header = "diff --git ";
        var wanted = paths as ISet<string> ?? new HashSet<string>(paths, StringComparer.Ordinal);
        var kept = new StringBuilder();
        var keep = false;
        var lines = patch.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Split leaves one empty element after the last newline.
            if (i == lines.Length - 1 && lines[i].Length == 0) break;
            if (lines[i].StartsWith(Header, StringComparison.Ordinal)) keep = Concerns(lines[i][Header.Length..], wanted);
            if (keep) kept.Append(lines[i]).Append('\n');
        }
        return kept.ToString();

        static bool Concerns(string names, ISet<string> wanted)
        {
            // "a/old b/new": the split is at a " b/" that leaves both sides prefixed.
            for (var at = names.IndexOf(" b/", StringComparison.Ordinal); at >= 0; at = names.IndexOf(" b/", at + 1, StringComparison.Ordinal))
            {
                if (!names.StartsWith("a/", StringComparison.Ordinal)) return false;
                if (wanted.Contains(names[(at + 3)..]) || wanted.Contains(names[2..at])) return true;
            }
            return false;
        }
    }

    // The merge runs in a temporary index that holds HEAD plus the local content of the files: `git apply --3way` insists that the
    // working tree matches the index, which would refuse every file with local changes. Merged files are left unstaged in the real
    // index, as `git stash apply` leaves them; the entries of the files that conflict are copied to it, so that the conflict editor sees them.
    public async Task<GitResult> ApplyStashFilesAsync(
        string repositoryPath, GitSha stash, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return Done.Instance;
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;

        // git diff has no --pathspec-from-file: a long list would exceed the command line limit (32,767 characters), so the
        // whole diff of the stash is read and the sections of the other files are dropped.
        var onCommandLine = paths.Sum(p => p.Length + 1) <= MaxPatchPathspecChars;
        var diff = await cli.RunAsync(top,
            ["-c", "core.quotepath=false", "--literal-pathspecs", "diff", "--binary", "--full-index", "--no-color", "--no-ext-diff", $"{stash}^1", stash.ToString(),
                .. onCommandLine ? (string[])["--", .. paths] : []], cancellationToken);
        if (diff is not Success<CliOutput> patch) return ToResult(diff);
        var patchText = onCommandLine ? patch.Value.StandardOutput : FilterPatch(patch.Value.StandardOutput, paths);
        if (string.IsNullOrWhiteSpace(patchText)) return new Failure(Loc.Get("GitError.StashFilesNoChange"));

        var indexFile = Path.Combine(Path.GetTempPath(), "vigo-index-" + Guid.NewGuid().ToString("N"));
        IReadOnlyDictionary<string, string?> temporary = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = indexFile };
        try
        {
            if (await cli.RunAsync(top, ["read-tree", "HEAD"], temporary, cancellationToken) is not Success<CliOutput> and var readTree) return ToResult(readTree);

            // The local content of the files that exist here (a file the stash adds is not here yet).
            var local = paths.Where(p => File.Exists(Path.Combine(top, p.Replace('/', Path.DirectorySeparatorChar)))).ToList();
            if (local.Count > 0)
            {
                var staged = await cli.RunWithInputAsync(top,
                    ["--literal-pathspecs", "add", "-f", "--pathspec-from-file=-", "--pathspec-file-nul"], string.Join('\0', local) + '\0', temporary, cancellationToken);
                if (staged is not Success<CliOutput>) return ToResult(staged);
            }

            var applied = await cli.RunWithInputAsync(top, ["apply", "--3way", "--whitespace=nowarn", "-"], patchText, temporary, cancellationToken);
            if (applied is Success<CliOutput>) return Done.Instance;
            if (applied is Canceled) return ToResult(applied);

            // Unmerged entries of the temporary index: the files that conflict (git exits with 1 then, like for any failure).
            var unmerged = await cli.RunAsync(top, ["ls-files", "-u", "-z"], temporary, CancellationToken.None);
            var entries = unmerged is Success<CliOutput> u ? u.Value.StandardOutput : string.Empty;
            var conflicted = entries.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e[(e.IndexOf('\t', StringComparison.Ordinal) + 1)..]).Distinct().ToList();
            if (conflicted.Count == 0)
            {
                var text = applied is Failure f ? f.Details ?? f.Message : string.Empty;
                return new Failure(Loc.Get("GitError.StashFilesFailed"), Details: text);
            }

            var transferred = await cli.RunWithInputAsync(top, ["update-index", "-z", "--index-info"], entries, CancellationToken.None);
            return transferred is Success<CliOutput> ? new Conflict(conflicted, "stash") : ToResult(transferred);
        }
        finally
        {
            try
            {
                File.Delete(indexFile);
            }
            catch (IOException)
            {
                // A temporary file left in the temp folder is harmless.
            }
        }
    }

    public async Task<GitResult> DropStashAsync(string repositoryPath, GitSha stash, CancellationToken cancellationToken = default)
    {
        if (await StashReferenceAsync(repositoryPath, stash, cancellationToken) is not { } reference) return StashGone;
        return ToResult(await cli.RunAsync(repositoryPath, ["stash", "drop", "--quiet", reference], cancellationToken));
    }

    private static Failure StashGone => new(Loc.Get("GitError.StashGone"));

    /// <summary>
    /// "stash@{n}" of the stash commit <paramref name="stash"/>, looked up at the last moment: indexes shift whenever a
    /// stash is pushed or dropped (here or in a terminal). Null when it is no longer in the list.
    /// </summary>
    private async Task<string?> StashReferenceAsync(string repositoryPath, GitSha stash, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(repositoryPath, ["stash", "list", "--format=%H"], cancellationToken);
        if (result is not Success<CliOutput> s) return null;
        var lines = s.Value.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = Array.FindIndex(lines, l => GitSha.TryParse(l, out var sha) && sha == stash);
        return index < 0 ? null : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"stash@{{{index}}}");
    }

    private async Task<GitSha?> StashTopAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        // Exit code 1 (--quiet --verify): no stash at all.
        var result = await cli.RunAsync(repositoryPath, ["rev-parse", "--quiet", "--verify", "refs/stash"], 1, cancellationToken);
        return result is Success<CliOutput> s && GitSha.TryParse(s.Value.StandardOutput.Trim(), out var sha) ? sha : null;
    }

    /// <summary>
    /// A merge or rebase that stopped with unmerged paths becomes a <see cref="Conflict"/> listing all of them (git
    /// only prints "Merge conflict in" for content conflicts, not for modify/delete ones). A known refusal (another
    /// operation in progress, local changes, conflicts left…) stays a <see cref="Failure"/> explaining it.
    /// </summary>
    private async Task<GitResult> StoppedOnConflictsAsync(string repositoryPath, GitResult result, string? source, string operation)
    {
        if (result.Value is Done or Canceled or AuthenticationFailed) return result;
        var explained = ExplainOperationFailure(result, source);
        if (!ReferenceEquals(explained.Value, result.Value)) return explained;

        var status = await GetStatusAsync(repositoryPath, CancellationToken.None);
        if (status.Value is Success<WorkingTreeStatus> { Value: { Operation: not null } current }
            && current.Entries.Where(e => e.IsConflicted).Select(e => e.Path).ToList() is { Count: > 0 } paths)
        {
            return new Conflict(paths, operation);
        }
        return result;
    }

    private static string OperationInProgressMessage => Loc.Get("GitError.OperationInProgress");

    /// <summary>Rewrites the usual merge, rebase, cherry-pick and revert refusals in readable (localized) text.</summary>
    private static GitResult ExplainOperationFailure(GitResult result, string? source)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        string? message = text switch
        {
            _ when text.Contains("You have not concluded your", StringComparison.Ordinal)
                   || text.Contains("Merging is not possible because you have unmerged files", StringComparison.Ordinal)
                   || text.Contains("Exiting because of an unresolved conflict", StringComparison.Ordinal)
                   || text.Contains("Exiting because of unfinished merge", StringComparison.Ordinal)
                   || text.Contains("you need to resolve your current index first", StringComparison.Ordinal)
                   || text.Contains("already a rebase-merge directory", StringComparison.Ordinal)
                   || text.Contains("already a rebase-apply directory", StringComparison.Ordinal)
                   || text.Contains("You are in the middle of", StringComparison.Ordinal)
                   || text.Contains("is already in progress", StringComparison.Ordinal) => OperationInProgressMessage,
            _ when text.Contains("would be overwritten by merge", StringComparison.Ordinal)
                   || text.Contains("would be overwritten by checkout", StringComparison.Ordinal)
                   || text.Contains("local changes would be overwritten by cherry-pick", StringComparison.Ordinal)
                   || text.Contains("local changes would be overwritten by revert", StringComparison.Ordinal) =>
                Loc.Get("GitError.OverwritesStash"),
            _ when text.Contains("cannot rebase: You have unstaged changes", StringComparison.Ordinal)
                   || text.Contains("Your index contains uncommitted changes", StringComparison.Ordinal)
                   || text.Contains("cannot rebase: You have uncommitted changes", StringComparison.Ordinal) =>
                Loc.Get("GitError.RebaseNeedsClean"),
            _ when text.Contains("refusing to merge unrelated histories", StringComparison.Ordinal) =>
                Loc.Format("GitError.NoCommonHistory", source ?? Loc.Get("GitError.ThisBranch")),
            _ when text.Contains("Not possible to fast-forward", StringComparison.Ordinal) =>
                Loc.Get("GitError.FastForwardOnly"),
            _ when text.Contains("not something we can merge", StringComparison.Ordinal)
                   || text.Contains("invalid upstream", StringComparison.Ordinal) =>
                Loc.Format("GitError.RefGone", source ?? Loc.Get("GitError.ThisRef")),
            _ when text.Contains("No changes - did you forget to use 'git add'", StringComparison.Ordinal)
                   || text.Contains("The previous cherry-pick is now empty", StringComparison.Ordinal) =>
                Loc.Get("GitError.CommitEmptyAfterResolve"),
            _ when text.Contains("Committing is not possible because you have unmerged files", StringComparison.Ordinal)
                   || text.Contains("must edit all merge conflicts", StringComparison.Ordinal) =>
                Loc.Get("GitError.MarkResolvedFirst"),
            _ => null,
        };
        return message is null ? result : failure with { Message = message, Details = text };
    }

    /// <summary>
    /// Shortest name git resolves to <paramref name="revision"/> alone ("feature", or "heads/feature" when a tag has
    /// the same name); the revision itself when git cannot tell (a SHA, a missing ref: git then reports it).
    /// </summary>
    private async Task<string> ShortRevisionAsync(string repositoryPath, string revision, CancellationToken cancellationToken)
    {
        if (!revision.StartsWith("refs/", StringComparison.Ordinal)) return revision;
        var result = await cli.RunAsync(repositoryPath, ["rev-parse", "--abbrev-ref=strict", revision], cancellationToken);
        return result is Success<CliOutput> s && s.Value.StandardOutput.Trim() is { Length: > 0 } name && !name.StartsWith('-') ? name : revision;
    }

    /// <summary>A canonical ref name or a full SHA: never an option, never ambiguous.</summary>
    private static bool IsRevision(string revision) =>
        revision.StartsWith("refs/", StringComparison.Ordinal) || GitSha.TryParse(revision, out _);

    /// <summary>"refs/remotes/origin/main" → "origin/main", a SHA → its short form.</summary>
    private static string DisplayRevision(string revision) =>
        revision.StartsWith("refs/remotes/", StringComparison.Ordinal) ? revision[13..]
        : GitSha.TryParse(revision, out var sha) ? sha.ToShortString()
        : ShortName(revision);

    public async Task<GitResult> CreateBranchAsync(string repositoryPath, CreateBranchRequest request, CancellationToken cancellationToken = default)
    {
        if (RefNames.Check(request.Name) is { } invalid) return new Failure(invalid);
        // --no-track: a branch made from a remote branch's commit does not silently follow it (branch.autoSetupMerge).
        string[] arguments = request.Checkout
            ? ["switch", "--quiet", "--no-track", "--create", request.Name, request.StartPoint.ToString()]
            : ["branch", "--no-track", "--", request.Name, request.StartPoint.ToString()];
        var result = ToResult(await cli.RunAsync(repositoryPath, arguments, cancellationToken));
        return request.Checkout ? ExplainCheckoutFailure(result, new CheckoutBranch(request.Name)) : ExplainRefFailure(result, request.Name);
    }

    public async Task<GitResult> RenameBranchAsync(string repositoryPath, string name, string newName, CancellationToken cancellationToken = default)
    {
        if (RefNames.Check(newName) is { } invalid) return new Failure(invalid);
        if (name.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidBranchName"));
        return ExplainRefFailure(ToResult(await cli.RunAsync(repositoryPath, ["branch", "--move", "--", name, newName], cancellationToken)), newName);
    }

    public async Task<GitResult> DeleteBranchAsync(string repositoryPath, string name, CancellationToken cancellationToken = default)
    {
        if (name.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidBranchName"));
        var result = ToResult(await cli.RunAsync(repositoryPath, ["branch", "-D", "--", name], cancellationToken));
        if (result.Value is Failure failure && (failure.Details ?? failure.Message) is var text
            && (text.Contains("cannot delete branch", StringComparison.OrdinalIgnoreCase) || text.Contains("checked out at", StringComparison.Ordinal)))
        {
            return failure with { Message = Loc.Format("GitError.BranchCheckedOutSwitch", name), Details = text };
        }
        return result;
    }

    public async Task<GitResult<int>> CountExclusiveCommitsAsync(
        string repositoryPath, IReadOnlyList<string> canonicalNames, CancellationToken cancellationToken = default)
    {
        if (canonicalNames.Count == 0) return new Success<int>(0);
        if (canonicalNames.Any(n => !n.StartsWith("refs/", StringComparison.Ordinal))) return new Failure(Loc.Get("GitError.InvalidRef"));

        // --exclude applies to the next --branches / --remotes / --tags only, with names relative to their prefix.
        static IEnumerable<string> Excluding(IReadOnlyList<string> names, string prefix, string option) =>
            names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(n => "--exclude=" + n[prefix.Length..]).Append(option);

        string[] arguments =
        [
            "rev-list", "--count", .. canonicalNames, "--not",
            .. Excluding(canonicalNames, "refs/heads/", "--branches"),
            .. Excluding(canonicalNames, "refs/remotes/", "--remotes"),
            .. Excluding(canonicalNames, "refs/tags/", "--tags"),
        ];
        var result = await cli.RunAsync(repositoryPath, arguments, cancellationToken);
        if (result is not Success<CliOutput> s) return Propagate<int>(result);
        return int.TryParse(s.Value.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? new Success<int>(count)
            : new Failure(Loc.Get("GitError.RevListUnreadable"));
    }

    public async Task<GitResult> CreateTagAsync(string repositoryPath, CreateTagRequest request, CancellationToken cancellationToken = default)
    {
        if (RefNames.Check(request.Name) is { } invalid) return new Failure(invalid);
        var target = request.Target.ToString();
        // Annotated: message on stdin (any length or character), cleaned like a commit message; tag.gpgSign applies. Without a message the tag
        // is lightweight: --no-sign, since tag.gpgSign (a signing profile sets it) would make git ask for a message ("no tag message?").
        var result = string.IsNullOrWhiteSpace(request.Message)
            ? await cli.RunAsync(repositoryPath, ["tag", "--no-sign", "--", request.Name, target], cancellationToken)
            : await cli.RunWithInputAsync(repositoryPath, ["tag", "--annotate", "--cleanup=strip", "--file=-", "--", request.Name, target],
                request.Message, cancellationToken);
        return ExplainRefFailure(ToResult(result), request.Name);
    }

    public async Task<GitResult> DeleteTagAsync(string repositoryPath, string name, CancellationToken cancellationToken = default)
    {
        if (name.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidTagName"));
        return ToResult(await cli.RunAsync(repositoryPath, ["tag", "--delete", "--", name], cancellationToken));
    }

    public async Task<GitResult> PushRefAsync(
        string repositoryPath, RefPush push, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (push.Remote.Length == 0 || push.Remote.StartsWith('-')
            || !push.Destination.StartsWith("refs/", StringComparison.Ordinal)
            || push.Source is { } source && !source.StartsWith("refs/", StringComparison.Ordinal))
        {
            return new Failure(Loc.Get("GitError.InvalidRefOrRemote"));
        }

        List<string> arguments = ["push", "--progress"];
        if (await SkipHooksAsync(repositoryPath, cancellationToken)) arguments.Add("--no-verify");
        if (push.SetUpstream) arguments.Add("--set-upstream");
        arguments.AddRange(["--", push.Remote, push.IsDelete ? ":" + push.Destination : push.Source + ":" + push.Destination]);
        var result = ToResult(await cli.RunWithProgressAsync(repositoryPath, arguments, progress, cancellationToken));

        if (result.Value is Failure failure && push.IsDelete && (failure.Details ?? failure.Message).Contains("remote ref does not exist", StringComparison.Ordinal))
        {
            // Already gone on the server (deleted by someone else): what was asked is done; the stale remote branch is
            // pruned locally (git 2.50 does it itself for a local remote, which does not report an error).
            if (push.Destination.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                var tracking = $"refs/remotes/{push.Remote}/{ShortName(push.Destination)}";
                await cli.RunAsync(repositoryPath, ["update-ref", "-d", tracking], CancellationToken.None);
            }
            return Done.Instance;
        }
        if (result.Value is Failure rejected && push.Destination.StartsWith("refs/tags/", StringComparison.Ordinal)
            && (rejected.Details ?? rejected.Message).Contains("already exists", StringComparison.Ordinal))
        {
            return rejected with { Message = Loc.Format("GitError.TagExistsOnRemote", ShortName(push.Destination), push.Remote) };
        }
        return ExplainRemoteFailure(result, $"{push.Remote}/{ShortName(push.Destination)}");
    }

    public async Task<GitResult> AddRemoteAsync(string repositoryPath, string name, string url, CancellationToken cancellationToken = default)
    {
        if (CheckRemoteName(name) is { } invalid) return new Failure(invalid);
        if (url.Length == 0 || url.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidRemoteUrl"));
        var result = ToResult(await cli.RunAsync(repositoryPath, ["remote", "add", "--", name, url], cancellationToken));
        return ExplainRemoteNameFailure(result, name);
    }

    public async Task<GitResult> RenameRemoteAsync(string repositoryPath, string name, string newName, CancellationToken cancellationToken = default)
    {
        if (CheckRemoteName(newName) is { } invalid) return new Failure(invalid);
        if (name.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidRemoteName"));
        // --no-progress: the refs are renamed silently (git prints a progress meter on large repositories).
        var result = ToResult(await cli.RunAsync(repositoryPath, ["remote", "rename", "--no-progress", "--", name, newName], cancellationToken));
        return ExplainRemoteNameFailure(result, newName);
    }

    public async Task<GitResult> RemoveRemoteAsync(string repositoryPath, string name, CancellationToken cancellationToken = default)
    {
        if (name.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidRemoteName"));
        return ToResult(await cli.RunAsync(repositoryPath, ["remote", "remove", "--", name], cancellationToken));
    }

    /// <summary>A remote name becomes <c>refs/remotes/&lt;name&gt;/…</c>: same rules as a branch name.</summary>
    private static string? CheckRemoteName(string name) => RefNames.Check(name);

    /// <summary>"refs/heads/feature/x" → "feature/x".</summary>
    private static string ShortName(string canonicalName) =>
        canonicalName.StartsWith("refs/heads/", StringComparison.Ordinal) ? canonicalName[11..]
        : canonicalName.StartsWith("refs/tags/", StringComparison.Ordinal) ? canonicalName[10..]
        : canonicalName;

    /// <summary>Rewrites the refusals of branch and tag creation / renaming in French.</summary>
    private static GitResult ExplainRefFailure(GitResult result, string name)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        string? message = text switch
        {
            _ when text.Contains("already exists", StringComparison.Ordinal) => Loc.Format("GitError.AlreadyExists", name),
            _ when text.Contains("cannot lock ref", StringComparison.Ordinal) && text.Contains("exists", StringComparison.Ordinal) =>
                Loc.Format("GitError.RefConflict", name),
            _ when text.Contains("is not a valid", StringComparison.Ordinal) => Loc.Format("GitError.NotValidName", name),
            _ => null,
        };
        return message is null ? result : failure with { Message = message, Details = text };
    }

    private static GitResult ExplainRemoteNameFailure(GitResult result, string name)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        return text.Contains("already exists", StringComparison.Ordinal)
            ? failure with { Message = Loc.Format("GitError.RemoteExists", name), Details = text }
            : result;
    }

    public async Task<GitResult<IReadOnlyList<GitRemote>>> GetRemotesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await cli.RunAsync(repositoryPath, ["remote", "-v"], cancellationToken);
        if (result is not Success<CliOutput> s) return Propagate<IReadOnlyList<GitRemote>>(result);

        // "origin\thttps://… (fetch)" then "origin\thttps://… (push)", in config order.
        var fetch = new List<(string Name, string Url)>();
        var push = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in s.Value.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            var kind = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (tab <= 0 || kind <= tab) continue;
            var name = line[..tab];
            var url = line[(tab + 1)..kind];
            if (line.EndsWith("(fetch)", StringComparison.Ordinal)) fetch.Add((name, url));
            else if (line.EndsWith("(push)", StringComparison.Ordinal)) push[name] = url;
        }

        return new Success<IReadOnlyList<GitRemote>>([.. fetch.Select(f =>
            new GitRemote(f.Name, f.Url, push.TryGetValue(f.Name, out var p) && p != f.Url ? p : null))]);
    }

    public async Task<GitResult> SetRemoteUrlAsync(string repositoryPath, string remoteName, string url, CancellationToken cancellationToken = default)
    {
        if (remoteName.StartsWith('-') || url.StartsWith('-')) return new Failure(Loc.Get("GitError.InvalidRemoteNameOrUrl"));
        return ToResult(await cli.RunAsync(repositoryPath, ["remote", "set-url", remoteName, url], cancellationToken));
    }

    public async Task<GitResult> StoreCredentialAsync(
        string? repositoryPath, RemoteUrl url, string userName, string secret, CancellationToken cancellationToken = default)
    {
        if (!url.IsHttp) return new Failure(Loc.Get("GitError.TokenNeedsHttps"));
        if (userName.Length == 0 || secret.Length == 0) return new Failure(Loc.Get("GitError.UserAndTokenRequired"));
        // One "key=value" per line: a line break would let the value inject other attributes.
        if (userName.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0 || secret.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0)
            return new Failure(Loc.Get("GitError.NoNewlineInCredential"));
        if (await ReadConfigAsync(repositoryPath, "credential.helper", cancellationToken) is null)
            return new Failure(Loc.Get("GitError.NoCredentialHelper"));

        var protocol = url.Protocol == RemoteProtocol.Http ? "http" : "https";
        var input = $"protocol={protocol}\nhost={url.HostAndPort}\nusername={userName}\npassword={secret}\n\n";
        return ToResult(await cli.RunWithInputAsync(repositoryPath, ["credential", "approve"], input, cancellationToken));
    }

    /// <summary>"stale info": the lease failed; "remote ref updated since checkout": --force-if-includes found commits we do not include.</summary>
    private static bool IsForceRefusal(string text) =>
        text.Contains("stale info", StringComparison.Ordinal) || text.Contains("remote ref updated since checkout", StringComparison.Ordinal);

    /// <summary>Rewrites the usual fetch / pull / push refusals as an explanation and what to do next.</summary>
    private static GitResult ExplainRemoteFailure(GitResult result, string? target)
    {
        if (result.Value is not Failure failure) return result;
        var text = failure.Details ?? failure.Message;
        var where = target ?? Loc.Get("GitError.RemoteBranchDefault");

        string? message = text switch
        {
            _ when IsForceRefusal(text) =>
                Loc.Format("GitError.ForcePushRefused", where),
            _ when text.Contains("[rejected]", StringComparison.Ordinal) =>
                Loc.Format("GitError.PushRefused", where),
            _ when text.Contains("Need to specify how to reconcile divergent branches", StringComparison.Ordinal) =>
                Loc.Get("GitError.DivergedNoStrategy"),
            _ when text.Contains("Not possible to fast-forward", StringComparison.Ordinal) =>
                Loc.Get("GitError.FastForwardImpossible"),
            _ when text.Contains("There is no tracking information", StringComparison.Ordinal) =>
                Loc.Get("GitError.NoUpstreamPushFirst"),
            _ when text.Contains("would be overwritten by merge", StringComparison.Ordinal)
                   || text.Contains("You have unstaged changes", StringComparison.Ordinal)
                   || text.Contains("Your index contains uncommitted changes", StringComparison.Ordinal) =>
                Loc.Get("GitError.PullOverwrites"),
            _ => null,
        };
        // "(fetch first)": the remote has commits we never fetched, a lease on the stale tracking ref would erase them.
        var needsForce = text.Contains("[rejected]", StringComparison.Ordinal) && text.Contains("(non-fast-forward)", StringComparison.Ordinal)
                         && !IsForceRefusal(text);
        return message is null ? result : failure with { Message = message, Details = text, Reason = needsForce ? FailureReason.PushNeedsForce : failure.Reason };
    }

    public async Task<GitResult<string?>> GetConfigValueAsync(string? repositoryPath, string key, CancellationToken cancellationToken = default)
    {
        var value = await ReadConfigAsync(repositoryPath, key, cancellationToken);
        return cancellationToken.IsCancellationRequested ? Canceled.Instance : new Success<string?>(value);
    }

    /// <summary>
    /// Runs a pathspec-aware command from the top level with the paths on stdin (NUL-separated, literal): no
    /// command-line length limit, no quoting issue.
    /// </summary>
    private async Task<GitResult> RunWithPathsAsync(
        string repositoryPath, string[] command, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return Done.Instance;
        var top = await GetTopLevelAsync(repositoryPath, cancellationToken) ?? repositoryPath;
        string[] arguments = ["--literal-pathspecs", .. command, "--pathspec-from-file=-", "--pathspec-file-nul"];
        return ToResult(await cli.RunWithInputAsync(top, arguments, string.Join('\0', paths) + '\0', cancellationToken));
    }

    /// <summary>Removes the directories emptied by a deletion, up to (not including) the top level.</summary>
    private static void DeleteEmptyParents(string? directory, string top)
    {
        var root = Path.TrimEndingDirectorySeparator(top);
        while (directory is not null
               && directory.Length > root.Length
               && Directory.Exists(directory)
               && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static GitResult ToResult<T>(GitResult<T> result) => result switch
    {
        Success<T> => Done.Instance,
        Conflict c => c,
        AuthenticationFailed a => a,
        Canceled c => c,
        Failure f => f,
    };

    // git.exe: the reference implementation of diff options (algorithm, renames, binary detection, CRLF).
    private static readonly string[] DiffOptions = ["--no-color", "--no-ext-diff", "--no-textconv", "-M", "-U3"];

    private async Task<GitResult<FileDiff>> RunDiffAsync(
        string workingDirectory, string[] arguments, string path, string? oldPath, int maxSuccessExitCode, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(workingDirectory, ["-c", "core.quotepath=false", "-c", "diff.suppressBlankEmpty=false", .. arguments], maxSuccessExitCode, cancellationToken);
        if (result is not Success<CliOutput> s) return Propagate<FileDiff>(result);

        try
        {
            return new Success<FileDiff>(UnifiedDiffParser.Parse(s.Value.StandardOutput, path, oldPath) with { IsLossy = s.Value.IsLossy });
        }
        catch (FormatException ex)
        {
            return new Failure($"Diff illisible : {ex.Message}", Exception: ex);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _gitDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Git directory of the repository (of the worktree, for a linked worktree): where git keeps the state of a merge
    /// or rebase. Read once per repository path; null on failure.
    /// </summary>
    private async Task<string?> GetGitDirectoryAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        if (_gitDirectories.TryGetValue(repositoryPath, out var known)) return known;
        var result = await cli.RunAsync(repositoryPath, ["rev-parse", "--absolute-git-dir"], cancellationToken);
        if (result is not Success<CliOutput> s || s.Value.StandardOutput.Trim() is not { Length: > 0 } directory) return null;
        return _gitDirectories[repositoryPath] = Path.GetFullPath(directory);
    }

    /// <summary>Top-level directory of the working tree; null on failure (the next git call reports it).</summary>
    private async Task<string?> GetTopLevelAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(repositoryPath, ["rev-parse", "--show-toplevel"], cancellationToken);
        return result is Success<CliOutput> s && s.Value.StandardOutput.Trim() is { Length: > 0 } top ? Path.GetFullPath(top) : null;
    }

    private async Task<string?> ReadConfigAsync(string? repositoryPath, string key, CancellationToken cancellationToken)
    {
        // `git config --get` exits with 1 when the key is missing: not an error.
        var result = await cli.RunAsync(repositoryPath, ["config", "--get", key], cancellationToken);
        return result is Success<CliOutput> s ? NullIfEmpty(s.Value.StandardOutput.Trim()) : null;
    }

    /// <summary>Opens the repository with libgit2 and runs <paramref name="read"/>; exceptions become a <see cref="Failure"/>.</summary>
    private static GitResult<T> WithLibGit2<T>(string path, Func<Repository, GitResult<T>> read)
    {
        try
        {
            if (Repository.Discover(path) is not { } gitDir) return new Failure(Loc.Format("GitError.NotARepository", path));
            using var repo = new Repository(gitDir);
            return read(repo);
        }
        catch (Exception ex)
        {
            return new Failure(ex.Message, Exception: ex);
        }
    }

    private static GitResult<T> ReadWithLibGit2<T>(string path, Func<Repository, T> read) =>
        WithLibGit2(path, repo => (GitResult<T>)new Success<T>(read(repo)));

    private static GitResult<RepositoryInfo> OpenWithLibGit2(string path) => ReadWithLibGit2(path, repo =>
    {
        var info = repo.Info;
        var unborn = info.IsHeadUnborn;
        var tip = unborn ? null : repo.Head.Tip;
        string? headName = info.IsHeadDetached ? tip?.Sha[..7] : repo.Head.FriendlyName;
        return new RepositoryInfo(
            WorkingDirectory: info.WorkingDirectory is null ? null : Path.TrimEndingDirectorySeparator(info.WorkingDirectory),
            GitDirectory: Path.TrimEndingDirectorySeparator(info.Path),
            Name: DisplayName(info.WorkingDirectory ?? info.Path),
            HeadName: headName,
            HeadSha: tip?.Sha,
            IsBare: info.IsBare,
            IsHeadDetached: info.IsHeadDetached,
            IsEmpty: unborn);
    });

    private async Task<GitResult<RepositoryInfo>> OpenWithCliAsync(string path, CancellationToken cancellationToken)
    {
        var revParse = await cli.RunAsync(path,
            ["rev-parse", "--absolute-git-dir", "--is-bare-repository", "--show-toplevel"], cancellationToken);
        if (revParse is not Success<CliOutput> rp) return Propagate<RepositoryInfo>(revParse);

        var lines = rp.Value.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var gitDir = Path.GetFullPath(lines[0]);
        var isBare = lines.Length > 1 && lines[1] == "true";
        var workDir = !isBare && lines.Length > 2 ? Path.GetFullPath(lines[2]) : null;

        var head = await cli.RunAsync(path, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
        var sha = head is Success<CliOutput> h ? NullIfEmpty(h.Value.StandardOutput.Trim()) : null;
        var symbolic = await cli.RunAsync(path, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        var branch = symbolic is Success<CliOutput> b ? NullIfEmpty(b.Value.StandardOutput.Trim()) : null;

        return new Success<RepositoryInfo>(new RepositoryInfo(
            workDir, gitDir, DisplayName(workDir ?? gitDir),
            HeadName: branch ?? sha?[..7],
            HeadSha: sha,
            IsBare: isBare,
            IsHeadDetached: branch is null && sha is not null,
            IsEmpty: sha is null));
    }

    private static GitResult<T> Propagate<T>(GitResult<CliOutput> result) => result switch
    {
        Success<CliOutput> => new Failure(Loc.Get("Common.UnexpectedResult")),
        Conflict c => c,
        AuthenticationFailed a => a,
        Canceled c => c,
        Failure f => f,
    };

    private static string DisplayName(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
