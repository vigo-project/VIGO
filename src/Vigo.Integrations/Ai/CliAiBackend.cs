using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.Ai;

/// <summary>
/// Commit message through a command-line tool installed on the machine (J7.9, ADR-070): <c>claude -p</c> by default.
/// The prompt goes to its standard input, the message comes back on its standard output; the tool runs from the temp
/// folder (not the repository), so it does not pick up project files and cannot touch the working tree.
/// </summary>
public sealed partial class CliAiBackend : IAiBackend
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat", ".com"];

    public AiProvider Provider => AiProvider.Cli;

    public Task<GitResult> CheckAsync(AiRequest settings, CancellationToken cancellationToken = default)
    {
        var (program, _) = Split(settings.CliCommand, AiSettings.DefaultCommandFor(settings.Tool));
        return Task.FromResult<GitResult>(Resolve(program) is null
            ? new Failure(Loc.Format("Ai.Cli.NotFoundTool", program))
            : Done.Instance);
    }

    public async Task<GitResult<string>> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var (program, arguments) = Split(request.CliCommand, AiSettings.DefaultCommandFor(request.Tool));
        if (Resolve(program) is not { } executable)
            return new Failure(Loc.Format("Ai.Cli.NotFoundToolPrefs", program));

        // A .cmd or .bat runs through cmd.exe, which reads ", & and %VAR% in an argument: the diff in the prompt could run commands (J9.3).
        var promptAsArgument = arguments.Contains(AiSettings.PromptPlaceholder, StringComparer.Ordinal);
        if (promptAsArgument && Path.GetExtension(executable).ToLowerInvariant() is ".cmd" or ".bat")
            return new Failure(Loc.Format("Ai.Cli.ScriptPromptRefused", program));
        if (request.Model is { Length: > 0 } model && !ModelName().IsMatch(model))
            return new Failure(Loc.Get("Ai.Cli.ModelRefused"));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument == AiSettings.PromptPlaceholder ? Clip(request.Prompt) : argument);
        }
        if (!string.IsNullOrWhiteSpace(request.Model) && SupportsModelFlag(program))
        {
            process.StartInfo.ArgumentList.Add("--model");
            process.StartInfo.ArgumentList.Add(request.Model);
        }

        try
        {
            if (!process.Start()) return new Failure(Loc.Format("Ai.Cli.NotStarted", program));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new Failure(Loc.Format("Ai.Cli.NotStartedWhy", program, e.Message), Exception: e);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (!promptAsArgument) await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Kills the tool and what it spawned (claude starts a node child on some installs).
            try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return cancellationToken.IsCancellationRequested
                ? Canceled.Instance
                : new Failure(Loc.Format("Ai.Cli.Timeout", program, (int)Timeout.TotalSeconds));
        }
        catch (IOException)
        {
            // The tool closed its input early (bad arguments): its exit code and error output say why, below.
        }

        var text = await output;
        var errors = (await error).Trim();
        if (process.ExitCode != 0)
        {
            var first = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return new Failure(Loc.Format("Ai.Cli.Failed", program, process.ExitCode) + (first is null ? string.Empty : $" {first}"),
                process.ExitCode, Details: errors.Length > 0 ? errors : null);
        }
        return new Success<string>(text);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:/@-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelName();

    private static bool SupportsModelFlag(string program) =>
        Path.GetFileNameWithoutExtension(program) is var name
        && (name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.Equals("copilot", StringComparison.OrdinalIgnoreCase));

    // A command line has a size limit: the end of a long patch is cut (the diffstat at the top stays).
    private static string Clip(string prompt) =>
        prompt.Length <= AiSettings.MaxArgumentPromptChars ? prompt : prompt[..AiSettings.MaxArgumentPromptChars] + " [prompt truncated]";

    /// <summary>Program and arguments of a command line (double quotes group words; no shell involved).</summary>
    internal static (string Program, List<string> Arguments) Split(string? command, string defaultCommand)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var c in string.IsNullOrWhiteSpace(command) ? defaultCommand : command)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) words.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started) words.Add(current.ToString());
        return (words[0], words.Skip(1).ToList());
    }

    /// <summary>Full path of <paramref name="program"/> (as is when it has a directory; else PATH × known extensions); null when absent.</summary>
    internal static string? Resolve(string program)
    {
        if (Path.IsPathRooted(program) || program.Contains('\\', StringComparison.Ordinal) || program.Contains('/', StringComparison.Ordinal))
            return File.Exists(program) ? Path.GetFullPath(program) : null;

        var hasExtension = Path.HasExtension(program);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var folder = directory.Trim('"');
            if (hasExtension && File.Exists(Path.Combine(folder, program))) return Path.Combine(folder, program);
            if (hasExtension) continue;
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(folder, program + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
