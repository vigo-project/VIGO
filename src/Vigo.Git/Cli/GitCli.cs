using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Git.Cli;

/// <summary>Buffered output of a git.exe call.</summary>
public sealed record CliOutput(int ExitCode, string StandardOutput, string StandardError, bool IsLossy = false);

/// <summary>Consumes git.exe standard output as raw bytes while the process runs.</summary>
public delegate Task OutputConsumer(PipeReader output, CancellationToken cancellationToken);

/// <summary>
/// Runs git.exe as a subprocess. Inherits the user's config, Git Credential Manager,
/// SSH and hooks. Cancellation kills the whole process tree.
/// </summary>
public sealed partial class GitCli
{
    private readonly string _gitPath;

    public GitCli(string? gitPath = null)
    {
        _gitPath = gitPath ?? GitExecutable.Path
            ?? throw new InvalidOperationException(Loc.Get("GitError.GitExeNotFound"));
    }

    public string GitPath => _gitPath;

    /// <summary>
    /// SSH settings of VIGO (preferences, read at each network command); null: git's own SSH behavior. See
    /// <see cref="Ssh.SshCommand"/>.
    /// </summary>
    public Func<SshSettings>? SshSettings { get; init; }

    /// <summary>
    /// Git profile of the repository a command runs in (J6.7, ADR-051): its identity and signing settings are handed to
    /// the commands that create commits, tags or stashes as <c>-c</c> settings, so that no git configuration file is
    /// changed. Null: git's own configuration decides.
    /// </summary>
    public Func<string, GitProfile?>? ProfileFor { get; init; }

    /// <summary>Journal of the commands run, shown in the Git tab of the terminal panel; null: nothing is recorded.</summary>
    public GitCommandLog? CommandLog { get; init; }

    /// <summary>Subcommands that write an author or committer identity (or sign).</summary>
    private static readonly HashSet<string> IdentityCommands = ["stash", "tag", "commit", "merge", "cherry-pick", "revert", "rebase", "pull", "am", "notes"];

    /// <summary>
    /// Hosting accounts of VIGO (J6.16, ADR-085): their tokens go to git over HTTPS as <c>Authorization</c> headers, ahead of the credential helpers.
    /// Null: git's credential helpers alone.
    /// </summary>
    public IGitHttpCredentials? HttpCredentials { get; init; }

    /// <summary>Subcommands that talk HTTPS to a remote and get the account headers.</summary>
    private static readonly HashSet<string> HttpCommands = ["fetch", "pull", "push", "clone", "ls-remote", "submodule", "lfs"];

    /// <summary>Subcommands that may reach a remote over SSH.</summary>
    private static readonly HashSet<string> NetworkCommands = ["fetch", "pull", "push", "clone", "ls-remote", "remote", "submodule"];

    private static readonly Dictionary<string, string?> EmptyEnvironment = [];

    /// <summary>Runs git with the given arguments, buffers its output and classifies the result.</summary>
    public Task<GitResult<CliOutput>> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        RunAsync(workingDirectory, arguments, 0, cancellationToken);

    /// <inheritdoc cref="RunAsync(string?, IReadOnlyList{string}, CancellationToken)"/>
    /// <param name="maxSuccessExitCode">
    /// Highest exit code still meaning success (<c>git diff --no-index</c> exits with 1 when the files differ).
    /// </param>
    public Task<GitResult<CliOutput>> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        int maxSuccessExitCode,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(workingDirectory, arguments, null, maxSuccessExitCode, cancellationToken);

    /// <inheritdoc cref="RunAsync(string?, IReadOnlyList{string}, CancellationToken)"/>
    /// <param name="environment">Variables added to (or, with a null value, removed from) git's environment.</param>
    public Task<GitResult<CliOutput>> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(workingDirectory, arguments, null, 0, cancellationToken, environment);

    /// <summary>
    /// Runs git with <paramref name="standardInput"/> written to its standard input (UTF-8, no BOM): patches for
    /// <c>git apply -</c>, NUL-separated path lists for <c>--pathspec-from-file=-</c> (no command-line length limit).
    /// </summary>
    public Task<GitResult<CliOutput>> RunWithInputAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        string standardInput,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(workingDirectory, arguments, standardInput, 0, cancellationToken);

    /// <inheritdoc cref="RunWithInputAsync(string?, IReadOnlyList{string}, string, CancellationToken)"/>
    /// <param name="environment">Variables added to (or, with a null value, removed from) git's environment (<c>GIT_INDEX_FILE</c>).</param>
    public Task<GitResult<CliOutput>> RunWithInputAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        string standardInput,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(workingDirectory, arguments, standardInput, 0, cancellationToken, environment);

    private async Task<GitResult<CliOutput>> RunCoreAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput,
        int maxSuccessExitCode,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (cancellationToken.IsCancellationRequested) return Canceled.Instance;

        var contentEncoding = SubcommandIndex(arguments) is var i and >= 0 && ContentCommands.Contains(arguments[i])
            ? await EncodingOfAsync(workingDirectory, cancellationToken)
            : null;
        // shortcut: the ASCII header of "cat-file --batch" cannot be read in UTF-16 / UTF-32; those files fall back to the detection there.
        if (contentEncoding is { CodePage: 1200 or 1201 or 12000 or 12001 } && arguments.Contains("cat-file")) contentEncoding = null;
        return await WithTokenFallbackAsync(workingDirectory, arguments, environment,
            options => RunOnceAsync(workingDirectory, arguments, standardInput, maxSuccessExitCode, options, contentEncoding, environment, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs a command with the headers of the signed-in accounts (J6.16, ADR-085); when the service refuses the token, runs it once more
    /// without them (credential helpers take over) and reports the refusal, except for a background command nobody could be told about.
    /// </summary>
    private async Task<GitResult<CliOutput>> WithTokenFallbackAsync(
        string? workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment,
        Func<LaunchOptions, Task<GitResult<CliOutput>>> run, CancellationToken cancellationToken)
    {
        var options = await PrepareAsync(workingDirectory, arguments, cancellationToken);
        var result = await run(options);
        if (result.Value is not AuthenticationFailed || options.HttpHeaders is not { Count: > 0 } sent) return result;

        if (environment is null || !environment.ContainsKey("GCM_INTERACTIVE")) HttpCredentials?.ReportRejected(workingDirectory, sent);
        return await run(options with { HttpHeaders = null });
    }

    private async Task<GitResult<CliOutput>> RunOnceAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput,
        int maxSuccessExitCode,
        LaunchOptions options,
        Encoding? contentEncoding,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(workingDirectory, arguments, options) };
        ApplyEnvironment(process, environment ?? EmptyEnvironment);
        var run = CommandLog?.Begin(workingDirectory, arguments);
        if (TryStart(process, standardInput is not null) is { } startFailure)
        {
            run?.Complete(-1, null, startFailure.Message);
            return startFailure;
        }

        var stdoutTask = ReadOutputAsync(process.StandardOutput.BaseStream, contentEncoding);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (standardInput is not null)
            {
                // Written concurrently with the output reads: a large input cannot dead-lock on full pipes.
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (IOException)
        {
            // git exited before reading all its input (e.g. invalid arguments): its exit code tells why.
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            run?.Complete(null);
            return Canceled.Instance;
        }

        var (text, lossy) = await stdoutTask;
        var output = new CliOutput(process.ExitCode, text, await stderrTask, lossy);
        run?.Complete(output.ExitCode, output.StandardOutput, output.StandardError);
        return output.ExitCode > 0 && output.ExitCode <= maxSuccessExitCode
            ? new Success<CliOutput>(output)
            : Classify(output, arguments);
    }

    /// <summary>
    /// Runs a network command (<c>fetch</c>, <c>pull</c>, <c>push</c> with <c>--progress</c>) and reports the progress
    /// lines of its standard error while it runs: one report per phase and percentage change, on the thread reading
    /// the pipe (<see cref="IProgress{T}"/> implementations marshal to the UI). Progress lines overwritten by '\r'
    /// are left out of the buffered error output used for classification. Cancellation kills the process tree.
    /// </summary>
    public Task<GitResult<CliOutput>> RunWithProgressAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        IProgress<GitProgress>? progress,
        CancellationToken cancellationToken = default) =>
        RunWithProgressAsync(workingDirectory, arguments, progress, EmptyEnvironment, cancellationToken);

    /// <inheritdoc cref="RunWithProgressAsync(string?, IReadOnlyList{string}, IProgress{GitProgress}?, CancellationToken)"/>
    /// <param name="environment">Variables added to (or, with a null value, removed from) git's environment.</param>
    public async Task<GitResult<CliOutput>> RunWithProgressAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        IProgress<GitProgress>? progress,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Canceled.Instance;

        return await WithTokenFallbackAsync(workingDirectory, arguments, environment,
            options => RunWithProgressOnceAsync(workingDirectory, arguments, progress, environment, options, cancellationToken), cancellationToken);
    }

    private async Task<GitResult<CliOutput>> RunWithProgressOnceAsync(
        string? workingDirectory, IReadOnlyList<string> arguments, IProgress<GitProgress>? progress,
        IReadOnlyDictionary<string, string?> environment, LaunchOptions options, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(workingDirectory, arguments, options) };
        ApplyEnvironment(process, environment);
        var run = CommandLog?.Begin(workingDirectory, arguments);
        if (TryStart(process) is { } startFailure)
        {
            run?.Complete(-1, null, startFailure.Message);
            return startFailure;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = ReadProgressAsync(process.StandardError, progress);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ((Task)Task.WhenAll(stdoutTask, stderrTask)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            run?.Complete(null);
            return Canceled.Instance;
        }

        var output = new CliOutput(process.ExitCode, await stdoutTask, await stderrTask);
        run?.Complete(output.ExitCode, output.StandardOutput, output.StandardError);
        return Classify(output, arguments);
    }

    /// <summary>Standard output read as bytes, then decoded as UTF-8 or, failing that, the system ANSI code page (<see cref="TextDecoding"/>).</summary>
    private static async Task<(string Text, bool Lossy)> ReadOutputAsync(Stream output, Encoding? preferred)
    {
        using var buffer = new MemoryStream();
        await output.CopyToAsync(buffer, CancellationToken.None);
        var text = TextDecoding.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), out var lossy, preferred);
        return (text, lossy);
    }

    /// <summary>Subcommands whose output is file content, decoded with the repository's encoding (J7.29).</summary>
    private static readonly HashSet<string> ContentCommands = ["diff", "diff-tree", "show", "cat-file"];

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, Encoding? Encoding)> _encodings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>gui.encoding</c> of the repository (local, then global config); null when not set. Read once per repository and kept until its config
    /// file changes (a worktree, whose <c>.git</c> is a file, until <see cref="ForgetEncoding"/>).
    /// </summary>
    internal async Task<Encoding?> EncodingOfAsync(string? workingDirectory, CancellationToken cancellationToken)
    {
        if (workingDirectory is null) return null;
        workingDirectory = Path.GetFullPath(workingDirectory);
        var config = Path.Combine(workingDirectory, ".git", "config");
        var stamp = File.Exists(config) ? File.GetLastWriteTimeUtc(config) : DateTime.MinValue;
        if (_encodings.TryGetValue(workingDirectory, out var known) && known.Stamp == stamp) return known.Encoding;

        var read = await ReadConfigAsync(workingDirectory, "gui.encoding", cancellationToken);
        if (!read.Found) return null;
        var encoding = TextDecoding.Resolve(read.Value);
        _encodings[workingDirectory] = (stamp, encoding);
        return encoding;
    }

    /// <summary>The setting was changed by VIGO: it is read again at the next use.</summary>
    internal void ForgetEncoding(string workingDirectory) => _encodings.TryRemove(Path.GetFullPath(workingDirectory), out _);

    private static void ApplyEnvironment(Process process, IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var (name, value) in environment)
        {
            if (value is null) process.StartInfo.Environment.Remove(name);
            else process.StartInfo.Environment[name] = value;
        }
    }

    [GeneratedRegex(@"^(?:remote: )?\d\d:\d\d:\d\d\.\d+ \S+\s+trace: ", RegexOptions.CultureInvariant)]
    private static partial Regex TraceLine();

    [GeneratedRegex(@"trace: run_command: git (?:merge|rebase) ", RegexOptions.CultureInvariant)]
    private static partial Regex MergeStart();

    private static async Task<string> ReadProgressAsync(StreamReader reader, IProgress<GitProgress>? progress)
    {
        var kept = new StringBuilder();
        var segment = new StringBuilder();
        var buffer = new char[4096];
        GitProgress? last = null;

        void Flush(bool endOfLine)
        {
            if (segment.Length == 0) return;
            var text = segment.ToString();
            segment.Clear();
            // GIT_TRACE lines (asked by pull only) are not git output: they only tell when the merge phase starts.
            if (TraceLine().IsMatch(text))
            {
                if (MergeStart().IsMatch(text)) progress?.Report(new GitProgress(GitProgressPhase.Merging, text));
                return;
            }
            // A '\r' ends a line that the next one overwrites: only its last state matters.
            if (endOfLine) kept.Append(text).Append('\n');
            if (progress is null || ProgressParser.Parse(text) is not { } parsed) return;
            if (last is not null && parsed.Phase != GitProgressPhase.Other
                && (parsed.Phase, parsed.Percent, parsed.IsRemote) == (last.Phase, last.Percent, last.IsRemote)) return;
            last = parsed;
            progress.Report(parsed);
        }

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                foreach (var c in buffer.AsSpan(0, read))
                {
                    if (c is '\r' or '\n') Flush(endOfLine: c == '\n');
                    else segment.Append(c);
                }
            }
            Flush(endOfLine: true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Process killed (cancellation): what was read is enough.
        }
        return kept.ToString();
    }

    /// <summary>
    /// Runs git and hands its standard output to <paramref name="consumer"/> as a byte stream, without buffering
    /// it all in memory. Standard error is buffered for classification. If the consumer throws or the token is
    /// canceled, the process tree is killed.
    /// </summary>
    public async Task<GitResult> RunStreamingAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        OutputConsumer consumer,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Canceled.Instance;

        var options = await PrepareAsync(workingDirectory, arguments, cancellationToken);
        using var process = new Process { StartInfo = CreateStartInfo(workingDirectory, arguments, options) };
        var run = CommandLog?.Begin(workingDirectory, arguments);
        if (TryStart(process) is { } startFailure)
        {
            run?.Complete(-1, null, startFailure.Message);
            return startFailure;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var reader = PipeReader.Create(process.StandardOutput.BaseStream, new StreamPipeReaderOptions(bufferSize: 64 * 1024));
        try
        {
            await consumer(reader, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            run?.Complete(null);
            return Canceled.Instance;
        }
        catch
        {
            TryKill(process);
            run?.Complete(null);
            throw;
        }
        finally
        {
            await reader.CompleteAsync();
        }

        var output = new CliOutput(process.ExitCode, string.Empty, await stderrTask);
        run?.Complete(output.ExitCode, null, output.StandardError);
        return Classify(output, arguments) switch
        {
            Success<CliOutput> => Done.Instance,
            Conflict c => c,
            AuthenticationFailed a => a,
            Canceled c => c,
            Failure f => f,
        };
    }

    /// <summary>What VIGO adds to a git command: OpenPGP program and SSH command.</summary>
    internal sealed record LaunchOptions(
        string? SigningProgram, string? SshCommand, IReadOnlyList<string>? ConfigOverrides = null, IReadOnlyList<GitHttpHeader>? HttpHeaders = null)
    {
        public static LaunchOptions None { get; } = new(null, null, null);
    }

    /// <summary>
    /// Options of a command: full path of the OpenPGP program when it may sign (see <see cref="SigningProgram"/>),
    /// SSH command of VIGO's settings when it may reach a remote (see <see cref="Ssh.SshCommand"/>). Each costs one
    /// <c>git config</c> read, only for the commands concerned.
    /// </summary>
    internal async Task<LaunchOptions> PrepareAsync(
        string? workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (SubcommandIndex(arguments) is not (var i and >= 0)) return LaunchOptions.None;
        var subcommand = arguments[i];

        IReadOnlyList<string>? identity = null;
        if (IdentityCommands.Contains(subcommand) && workingDirectory is not null && ProfileFor?.Invoke(workingDirectory) is { } profile
            && profile.ConfigOverrides() is { Count: > 0 } overrides)
        {
            identity = overrides;
        }

        string? signingProgram = null;
        if (SigningProgram.MaySign(subcommand))
        {
            // Read in the repository: a local gpg.program overrides the global one. Exit code 1: not set.
            var configured = await ReadConfigAsync(workingDirectory, "gpg.program", cancellationToken);
            signingProgram = configured.Found ? SigningProgram.Resolve(configured.Value) : null;
        }

        string? sshCommand = null;
        if (NetworkCommands.Contains(subcommand) && SshSettings?.Invoke() is { } ssh
            && Ssh.SshCommand.Build(ssh, Ssh.SshTools.WindowsSsh) is { } command
            // The user's own choice wins: GIT_SSH_COMMAND / GIT_SSH in VIGO's environment, core.sshCommand.
            && Environment.GetEnvironmentVariable("GIT_SSH_COMMAND") is null && Environment.GetEnvironmentVariable("GIT_SSH") is null
            && await ReadConfigAsync(workingDirectory, "core.sshCommand", cancellationToken) is { Found: true, Value: null })
        {
            sshCommand = command;
        }
        IReadOnlyList<GitHttpHeader>? headers = null;
        if (HttpCredentials is not null && HttpCommands.Contains(subcommand)
            && await HttpCredentials.GetHeadersAsync(workingDirectory, cancellationToken) is { Count: > 0 } found)
        {
            headers = found;
        }
        return signingProgram is null && sshCommand is null && identity is null && headers is null
            ? LaunchOptions.None : new LaunchOptions(signingProgram, sshCommand, identity, headers);
    }

    /// <summary>Value of a config key; Found false when it could not be read, Value null when it is not set.</summary>
    private async Task<(bool Found, string? Value)> ReadConfigAsync(string? workingDirectory, string key, CancellationToken cancellationToken)
    {
        var read = await RunCoreAsync(workingDirectory, ["config", "--get", key], null, 1, cancellationToken);
        if (read.Value is not Success<CliOutput> { Value: var output }) return (false, null);
        return (true, output.ExitCode == 0 && output.StandardOutput.Trim() is { Length: > 0 } value ? value : null);
    }

    private ProcessStartInfo CreateStartInfo(string? workingDirectory, IReadOnlyList<string> arguments, LaunchOptions? options = null)
    {
        var psi = new ProcessStartInfo(_gitPath)
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        // English messages for reliable error classification; no console prompt
        // (GCM shows its own UI). Messages shown to the user are localized by VIGO.
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // No interactive editor ever (nothing to type into): a message git would open for editing, such as the one of
        // a commit replayed by "rebase --continue", is accepted as prepared. Messages typed by the user go on stdin.
        psi.Environment["GIT_EDITOR"] = "true";
        if (options?.SigningProgram is { } signingProgram)
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"gpg.program={signingProgram}");
        }
        if (options?.ConfigOverrides is { } overrides)
        {
            foreach (var setting in overrides)
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(setting);
            }
        }
        if (options?.SshCommand is { } sshCommand) psi.Environment["GIT_SSH_COMMAND"] = sshCommand;
        if (options?.HttpHeaders is { Count: > 0 } headers)
        {
            ApplyHttpHeaders(psi, headers);
            // A refusal must not wake the credential helpers (a Git Credential Manager window): the command then fails, and the
            // fallback runs it again with them.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("credential.helper=");
        }
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        return psi;
    }

    // Through the environment, never the command line or a file: only this process sees the token. Added after the settings the
    // environment may already carry (GIT_CONFIG_COUNT), without replacing them.
    private static void ApplyHttpHeaders(ProcessStartInfo psi, IReadOnlyList<GitHttpHeader> headers)
    {
        var count = psi.Environment.TryGetValue("GIT_CONFIG_COUNT", out var current) && int.TryParse(current, out var n) && n > 0 ? n : 0;
        foreach (var url in headers.SelectMany(h => h.Urls.Select(u => (Url: u, h.Value))))
        {
            psi.Environment[$"GIT_CONFIG_KEY_{count}"] = $"http.{url.Url}.extraHeader";
            psi.Environment[$"GIT_CONFIG_VALUE_{count}"] = url.Value;
            count++;
        }
        psi.Environment["GIT_CONFIG_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Failure? TryStart(Process process, bool keepInputOpen = false)
    {
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new Failure(Loc.Format("GitError.GitExeFailed", ex.Message), Exception: ex);
        }
        if (!keepInputOpen) process.StandardInput.Close();
        return null;
    }

    internal static GitResult<CliOutput> Classify(CliOutput output, IReadOnlyList<string> arguments)
    {
        if (output.ExitCode == 0) return new Success<CliOutput>(output);

        var err = output.StandardError;
        if (IsAuthenticationError(err))
        {
            return new AuthenticationFailed(
                FindRemote(arguments) ?? FailedRemote(err), FirstMeaningfulLine(err), FailedUrl(err), err.Trim().ReplaceLineEndings("\n"));
        }

        var conflicts = ParseConflicts(output.StandardOutput + "\n" + err);
        if (conflicts.Count > 0)
            return new Conflict(conflicts, SubcommandIndex(arguments) is var i and >= 0 ? arguments[i] : "git");

        var text = (err.Trim().Length > 0 ? err : output.StandardOutput).Trim();
        // Several lines (hook output, hints…): keep them all for the user, the message being the first one.
        var details = text.Contains('\n', StringComparison.Ordinal) ? text.ReplaceLineEndings("\n") : null;
        return new Failure(FirstMeaningfulLine(text), output.ExitCode, Details: details);
    }

    private static bool IsAuthenticationError(string stderr) =>
        stderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("could not read Password", StringComparison.OrdinalIgnoreCase)
        // No prompt possible (automatic fetch: empty GIT_ASKPASS, terminal prompts disabled).
        || stderr.Contains("unable to get password from user", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Permission denied (publickey", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("HTTP Basic: Access denied", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("The requested URL returned error: 401", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("The requested URL returned error: 403", StringComparison.OrdinalIgnoreCase);

    // "Authentication failed for 'https://…'", "could not read Username for 'https://host'", "unable to access 'https://…/':".
    [GeneratedRegex(@"(?:for|access) '(?<url>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedUrl();

    // "git@github.com: Permission denied (publickey)."
    [GeneratedRegex(@"^(?<url>[^\s@]+@[\w.\-]+): Permission denied", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex SshDenied();

    // "error: could not fetch origin" (fetch --all names the remote that failed).
    [GeneratedRegex(@"could not fetch (?<remote>[^\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FetchedRemote();

    private static string? FailedUrl(string stderr) =>
        QuotedUrl().Match(stderr) is { Success: true } m ? m.Groups["url"].Value
        : SshDenied().Match(stderr) is { Success: true } s ? s.Groups["url"].Value
        : null;

    private static string? FailedRemote(string stderr) =>
        FetchedRemote().Match(stderr) is { Success: true } m ? m.Groups["remote"].Value : null;

    private static List<string> ParseConflicts(string text)
    {
        // "CONFLICT (content): Merge conflict in <path>"
        const string marker = "Merge conflict in ";
        var paths = new List<string>();
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("CONFLICT", StringComparison.Ordinal)) continue;
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0) paths.Add(line[(idx + marker.Length)..].Trim().ToString());
        }
        return paths;
    }

    /// <summary>
    /// Index of the git subcommand, skipping global options (<c>-c key=value</c>, <c>-C path</c>, <c>--no-pager</c>…);
    /// -1 if there is none.
    /// </summary>
    private static int SubcommandIndex(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace") { i++; continue; }
            if (!arg.StartsWith('-')) return i;
        }
        return -1;
    }

    private static string? FindRemote(IReadOnlyList<string> arguments)
    {
        // fetch/pull/push <remote> …: first non-option argument after the subcommand.
        var start = SubcommandIndex(arguments);
        if (start < 0) return null;
        for (var i = start + 1; i < arguments.Count; i++)
            if (!arguments[i].StartsWith('-')) return arguments[i];
        return null;
    }

    /// <summary>
    /// First "fatal:" or "error:" line when there is one (push prints "To &lt;url&gt;" first), otherwise the first line
    /// that is not a hint (hook output).
    /// </summary>
    private static string FirstMeaningfulLine(string text)
    {
        string? first = null;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty || trimmed.StartsWith("hint:", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("fatal: ", StringComparison.Ordinal)) return trimmed[7..].ToString();
            if (trimmed.StartsWith("error: ", StringComparison.Ordinal)) return trimmed[7..].ToString();
            first ??= trimmed.ToString();
        }
        return first ?? Loc.Get("GitError.GitFailedNoMessage");
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }
}
