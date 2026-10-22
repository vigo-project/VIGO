using System.Globalization;
using System.Text.RegularExpressions;
using Vigo.Core.Models;

namespace Vigo.Git.Status;

/// <summary>
/// Reads the operation stopped half-way in a repository from its git directory: the state files git itself uses
/// (<c>MERGE_HEAD</c>, <c>rebase-merge/</c>, <c>rebase-apply/</c>, <c>CHERRY_PICK_HEAD</c>, <c>REVERT_HEAD</c>).
/// </summary>
internal static partial class OperationReader
{
    public static RepositoryOperation? Read(string gitDirectory)
    {
        var rebaseMerge = Path.Combine(gitDirectory, "rebase-merge");
        if (Directory.Exists(rebaseMerge)) return ReadRebase(rebaseMerge, "msgnum", "end");

        var rebaseApply = Path.Combine(gitDirectory, "rebase-apply");
        if (Directory.Exists(rebaseApply))
        {
            // rebase-apply also holds a "git am" session ("applying", J7.12): a patch mailbox being applied.
            if (File.Exists(Path.Combine(rebaseApply, "applying"))) return ReadMailbox(rebaseApply);
            return ReadRebase(rebaseApply, "next", "last");
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD"))) return ReadMerge(gitDirectory);
        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD"))) return ReadPick(gitDirectory, RepositoryOperationKind.CherryPick, "CHERRY_PICK_HEAD");
        if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD"))) return ReadPick(gitDirectory, RepositoryOperationKind.Revert, "REVERT_HEAD");
        return null;
    }

    /// <summary>Cherry-pick or revert: the commit being applied and the message git prepared (MERGE_MSG).</summary>
    private static RepositoryOperation ReadPick(string gitDirectory, RepositoryOperationKind kind, string headFile)
    {
        var commit = ReadLine(gitDirectory, headFile) is { } sha && GitSha.TryParse(sha, out var parsed) ? parsed.ToString() : null;
        return new RepositoryOperation(kind, Source: commit, Message: ReadMessage(gitDirectory));
    }

    /// <summary>MERGE_MSG without its comment lines (conflict list, hints), as git commit does with the default cleanup.</summary>
    private static string? ReadMessage(string gitDirectory)
    {
        try
        {
            var path = Path.Combine(gitDirectory, "MERGE_MSG");
            return File.Exists(path) ? string.Join('\n', File.ReadAllLines(path).Where(l => !l.StartsWith('#'))).Trim() : null;
        }
        catch (IOException)
        {
            // Being rewritten by git: the next status reads it.
            return null;
        }
    }

    /// <summary>A <c>git am</c> session: the patch being applied ("next") out of "last", with its subject.</summary>
    private static RepositoryOperation ReadMailbox(string directory) =>
        new(RepositoryOperationKind.ApplyMailbox, Step: ReadNumber(directory, "next"), StepCount: ReadNumber(directory, "last"), Message: ReadSubject(directory));

    /// <summary>Subject of the patch git am stopped on: the "Subject:" header of its current patch file (<c>rebase-apply/NNNN</c>).</summary>
    private static string? ReadSubject(string directory)
    {
        try
        {
            var step = ReadNumber(directory, "next");
            var file = Path.Combine(directory, step.ToString("D4", CultureInfo.InvariantCulture));
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadLines(file).Take(40))
            {
                if (line.StartsWith("Subject:", StringComparison.Ordinal)) return PatchPrefix().Replace(line[8..].Trim(), string.Empty);
                if (line.Length == 0) break;
            }
        }
        catch (IOException)
        {
            // Being rewritten by git: the next status reads it.
        }
        return null;
    }

    private static RepositoryOperation ReadRebase(string directory, string stepFile, string countFile)
    {
        var headName = ReadLine(directory, "head-name");
        var branch = headName is { } name && name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name[11..] : null;
        var onto = ReadLine(directory, "onto") is { } sha && GitSha.TryParse(sha, out var parsed) ? parsed : (GitSha?)null;
        return new RepositoryOperation(
            RepositoryOperationKind.Rebase,
            Source: onto?.ToString(),
            Branch: branch,
            Onto: onto,
            Step: ReadNumber(directory, stepFile),
            StepCount: ReadNumber(directory, countFile),
            // Written by the sequencer when it stops on an "edit" line (not on conflicts): HEAD is the commit to change.
            IsEditStop: File.Exists(Path.Combine(directory, "amend")));
    }

    private static RepositoryOperation ReadMerge(string gitDirectory)
    {
        string? source = null, branch = null;
        var message = ReadMessage(gitDirectory);
        if (message is not null && MergeSubject().Match(message) is { Success: true } m)
        {
            source = m.Groups["source"].Value;
            branch = m.Groups["target"].Success ? m.Groups["target"].Value : null;
        }
        return new RepositoryOperation(RepositoryOperationKind.Merge, Source: source, Branch: branch, Message: message);
    }

    // "[PATCH] ", "[PATCH 2/5] ": what git format-patch puts before the subject.
    [GeneratedRegex(@"^(?:\[[^\]]*\]\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex PatchPrefix();

    private static string? ReadLine(string directory, string file)
    {
        try
        {
            var path = Path.Combine(directory, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int ReadNumber(string directory, string file) =>
        int.TryParse(ReadLine(directory, file), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    // "Merge branch 'feature/x' into main", "Merge remote-tracking branch 'origin/x'", "Merge tag 'v1'", "Merge commit 'a1b2c3d'".
    [GeneratedRegex(@"^Merge (?:remote-tracking branch|branch|tag|commit) '(?<source>[^']+)'(?: of [^\n]+?)?(?: into (?<target>[^\n]+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex MergeSubject();
}
