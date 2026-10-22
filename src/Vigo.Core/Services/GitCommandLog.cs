using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Vigo.Core.Services;

/// <summary>One git.exe call followed in the Git log: started by <see cref="GitCommandLog.Begin"/>, ended by <see cref="Complete"/>.</summary>
public sealed class GitCommandRun
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly GitCommandLog _log;

    internal GitCommandRun(GitCommandLog log, string workingDirectory, string commandLine, bool isRead)
    {
        _log = log;
        WorkingDirectory = workingDirectory;
        CommandLine = commandLine;
        IsRead = isRead;
        StartedAt = DateTimeOffset.Now;
    }

    public DateTimeOffset StartedAt { get; }

    public string WorkingDirectory { get; }

    /// <summary>"git push origin main": arguments quoted as in a shell, secrets masked.</summary>
    public string CommandLine { get; }

    /// <summary>Command that only reads the repository (status, log…): hidden unless the log shows everything.</summary>
    public bool IsRead { get; }

    /// <summary>Null while the command runs or when it was canceled.</summary>
    public int? ExitCode { get; private set; }

    public bool IsCanceled { get; private set; }

    public bool IsCompleted { get; private set; }

    public TimeSpan Duration { get; private set; }

    /// <summary>Text git printed (standard output then standard error), masked and truncated; empty for most reads.</summary>
    public string Output { get; private set; } = string.Empty;

    /// <summary>Ends the run; a null <paramref name="exitCode"/> means canceled (or never started).</summary>
    public void Complete(int? exitCode, string? standardOutput = null, string? standardError = null)
    {
        if (IsCompleted) return;
        Duration = Stopwatch.GetElapsedTime(_startTimestamp);
        ExitCode = exitCode;
        IsCanceled = exitCode is null;
        // Reads print patches and listings: only their errors are worth the space.
        Output = GitCommandLog.FormatOutput(IsRead && exitCode is 0 ? null : standardOutput, standardError);
        IsCompleted = true;
        _log.RaiseCompleted(this);
    }
}

/// <summary>
/// Journal of the git.exe commands VIGO runs, shown in the "Git" tab of the terminal panel. Commands are published
/// when they start and when they end, on the thread that runs them: subscribers marshal to the UI themselves.
/// LibGit2Sharp reads do not go through git.exe and are not listed.
/// </summary>
public sealed partial class GitCommandLog
{
    private const int MaxOutputLength = 4000;

    /// <summary>Subcommands that never write: listed only when the log shows everything.</summary>
    private static readonly HashSet<string> ReadCommands =
    [
        "log", "status", "for-each-ref", "rev-parse", "rev-list", "show", "diff", "blame", "ls-files", "ls-tree",
        "cat-file", "merge-file", "show-ref", "describe", "symbolic-ref", "name-rev", "check-ignore", "merge-base",
        "diff-tree", "diff-index", "diff-files", "grep", "shortlog", "reflog", "var", "version", "check-attr", "count-objects",
    ];

    private static readonly HashSet<string> ConfigReadFlags =
        ["--get", "--get-all", "--get-regexp", "--get-urlmatch", "--list", "-l", "--show-origin", "--show-scope"];

    public event Action<GitCommandRun>? Started;

    public event Action<GitCommandRun>? Completed;

    /// <summary>Announces a command about to run in <paramref name="workingDirectory"/> (null: no repository, not listed).</summary>
    public GitCommandRun? Begin(string? workingDirectory, IReadOnlyList<string> arguments)
    {
        if (workingDirectory is null) return null;
        var run = new GitCommandRun(this, workingDirectory, FormatCommandLine(arguments), IsRead(arguments));
        Started?.Invoke(run);
        return run;
    }

    internal void RaiseCompleted(GitCommandRun run) => Completed?.Invoke(run);

    /// <summary>The command only reads the repository (see <see cref="ReadCommands"/>; <c>config --get</c>, <c>remote -v</c>…).</summary>
    public static bool IsRead(IReadOnlyList<string> arguments)
    {
        var index = SubcommandIndex(arguments);
        if (index < 0) return true;
        var subcommand = arguments[index];
        if (subcommand == "config") return arguments.Skip(index + 1).Any(ConfigReadFlags.Contains);
        return ReadCommands.Contains(subcommand);
    }

    /// <summary>"git commit -m "two words"": arguments with spaces or quotes are double-quoted, secrets masked.</summary>
    public static string FormatCommandLine(IReadOnlyList<string> arguments)
    {
        var text = new StringBuilder("git");
        foreach (var argument in arguments)
            text.Append(' ').Append(Quote(IsSecretSetting(argument) ? "***" : Mask(argument)));
        return text.ToString();
    }

    /// <summary>Output shown under the command: masked, trimmed, limited to a few thousand characters.</summary>
    internal static string FormatOutput(string? standardOutput, string? standardError)
    {
        var combined = string.Join('\n', new[] { standardOutput, standardError }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim().ReplaceLineEndings("\n")));
        combined = Mask(combined);
        return combined.Length <= MaxOutputLength ? combined : combined[..MaxOutputLength] + "\n…";
    }

    /// <summary>Hides what authenticates: the user info of URLs and the value of authorization settings.</summary>
    internal static string Mask(string text) => UserInfo().Replace(text, "$1***@");

    private static bool IsSecretSetting(string argument) =>
        argument.Contains("extraheader=", StringComparison.OrdinalIgnoreCase)
        || argument.Contains("authorization:", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\"'") < 0
            ? argument
            : $"\"{argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static int SubcommandIndex(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace") { i++; continue; }
            if (!argument.StartsWith('-')) return i;
        }
        return -1;
    }

    // "https://user:secret@host/…", "https://token@host/…": the part before '@' in an URL authority.
    [GeneratedRegex(@"(\b[a-z][a-z0-9+.\-]*://)[^/@\s]+@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UserInfo();
}
