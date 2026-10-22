using System.Diagnostics;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git.Ssh;

/// <summary>
/// SSH keys of the user: listed from <c>~/.ssh</c> and the imported files, generated with <c>ssh-keygen</c> (Windows
/// OpenSSH, or the one of Git for Windows), state of the Windows ssh-agent service read with <c>sc.exe</c>.
/// </summary>
public sealed class SshKeyService(GitCli cli, string? keyDirectory = null) : ISshKeyService
{
    public string KeyDirectory { get; } = keyDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    public Task<IReadOnlyList<SshKey>> ListAsync(IEnumerable<string> imported, CancellationToken cancellationToken = default)
    {
        var importedPaths = imported.ToArray();
        return Task.Run<IReadOnlyList<SshKey>>(() =>
        {
            var keys = new List<SshKey>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(KeyDirectory))
            {
                foreach (var publicKey in Directory.EnumerateFiles(KeyDirectory, "*.pub").Order(StringComparer.OrdinalIgnoreCase))
                {
                    var privateKey = publicKey[..^".pub".Length];
                    if (File.Exists(privateKey) && Read(privateKey) is { } key && seen.Add(key.PrivateKeyPath)) keys.Add(key);
                }
            }
            foreach (var path in importedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path) && Read(path) is { } key && seen.Add(key.PrivateKeyPath)) keys.Add(key);
            }
            return keys;
        }, cancellationToken);
    }

    public Task<GitResult<SshKey>> ReadAsync(string privateKeyPath, CancellationToken cancellationToken = default) =>
        Task.Run<GitResult<SshKey>>(() =>
        {
            var path = Path.GetFullPath(privateKeyPath);
            if (path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                path = path[..^".pub".Length];
            if (!File.Exists(path)) return new Failure(Loc.Format("SshKeys.Service.PrivateMissing", path));
            if (!File.Exists(path + ".pub")) return new Failure(Loc.Format("SshKeys.Service.PublicMissing", Path.GetFileName(path)));
            return Read(path) is { } key ? new Success<SshKey>(key) : new Failure(Loc.Format("SshKeys.Service.PublicUnreadable", Path.GetFileName(path)));
        }, cancellationToken);

    public async Task<GitResult<SshKey>> GenerateAsync(CancellationToken cancellationToken = default)
    {
        if (SshTools.Find("ssh-keygen.exe", cli.GitPath) is not { } keygen)
            return new Failure(Loc.Get("SshKeys.Service.KeygenMissing"));

        var email = await cli.RunAsync(null, ["config", "--global", "--get", "user.email"], 1, cancellationToken);
        if (email.Value is Canceled) return Canceled.Instance;
        var comment = email.Value is Success<CliOutput> { Value.StandardOutput: var value } && value.Trim().Length > 0
            ? value.Trim()
            : $"{Environment.UserName}@{Environment.MachineName}";

        Directory.CreateDirectory(KeyDirectory);
        var path = Path.Combine(KeyDirectory, "id_ed25519");
        for (var i = 1; File.Exists(path) || File.Exists(path + ".pub"); i++)
            path = Path.Combine(KeyDirectory, i == 1 ? "id_ed25519_vigo" : $"id_ed25519_vigo{i}");

        var run = await SshTools.RunAsync(keygen, ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", path], cancellationToken);
        if (run is null) return Canceled.Instance;
        if (run.ExitCode != 0 || !File.Exists(path + ".pub"))
        {
            var error = (run.StandardError.Trim().Length > 0 ? run.StandardError : run.StandardOutput).Trim();
            return new Failure(Loc.Get("SshKeys.Service.GenerateFailed"), run.ExitCode, Details: error.Length > 0 ? error : null);
        }
        return await ReadAsync(path, cancellationToken);
    }

    public Task<GitResult> DeleteAsync(string privateKeyPath, CancellationToken cancellationToken = default) =>
        Task.Run<GitResult>(() =>
        {
            var path = Path.GetFullPath(privateKeyPath);
            if (path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                path = path[..^".pub".Length];
            try
            {
                foreach (var file in (string[])[path, path + ".pub"])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(file))
                    {
                        // A private key is read-only on some setups.
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                }
                return Done.Instance;
            }
            catch (OperationCanceledException)
            {
                return Canceled.Instance;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new Failure(Loc.Format("SshKeys.Service.DeleteFailed", Path.GetFileName(path)), Details: e.Message);
            }
        }, cancellationToken);

    public async Task<SshAgentState> GetAgentStateAsync(CancellationToken cancellationToken = default)
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        var query = await SshTools.RunAsync(sc, ["query", "ssh-agent"], cancellationToken);
        if (query is null) return SshAgentState.Unknown;
        // 1060: ERROR_SERVICE_DOES_NOT_EXIST (optional feature "OpenSSH Client" not installed).
        if (query.ExitCode == 1060) return SshAgentState.NotInstalled;
        if (query.ExitCode != 0) return SshAgentState.Unknown;
        if (query.StandardOutput.Contains("RUNNING", StringComparison.Ordinal)) return SshAgentState.Running;

        // Disabled is the default start type of the service on Windows.
        var config = await SshTools.RunAsync(sc, ["qc", "ssh-agent"], cancellationToken);
        return config is { ExitCode: 0 } && config.StandardOutput.Contains("DISABLED", StringComparison.Ordinal)
            ? SshAgentState.Disabled
            : SshAgentState.Stopped;
    }

    private static SshKey? Read(string privateKeyPath)
    {
        try
        {
            var line = File.ReadLines(privateKeyPath + ".pub").FirstOrDefault(l => l.Trim().Length > 0);
            return line is null ? null : SshPublicKey.Parse(Path.GetFullPath(privateKeyPath), line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Locates and runs the OpenSSH tools.</summary>
internal static class SshTools
{
    /// <summary>Windows OpenSSH client (<c>System32\OpenSSH</c>), null when the optional feature is not installed.</summary>
    public static string? WindowsSsh => WindowsTool("ssh.exe");

    /// <summary>The Windows OpenSSH tool, else the one shipped with Git for Windows (<c>usr\bin</c>).</summary>
    public static string? Find(string fileName, string gitPath) => WindowsTool(fileName) ?? GitTool(fileName, gitPath);

    private static string? WindowsTool(string fileName)
    {
        var path = Path.Combine(Environment.SystemDirectory, "OpenSSH", fileName);
        return File.Exists(path) ? path : null;
    }

    // git.exe lives in <root>\cmd, <root>\bin or <root>\mingw64\bin.
    private static string? GitTool(string fileName, string gitPath)
    {
        for (var dir = Path.GetDirectoryName(gitPath); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "usr", "bin", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Runs a tool without window nor input; null when canceled (the process is then killed).</summary>
    public static async Task<CliOutput?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CliOutput(-1, string.Empty, e.Message);
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        return new CliOutput(process.ExitCode, await stdout, await stderr);
    }
}
