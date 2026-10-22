using System.Runtime.Versioning;
using Microsoft.Win32;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Git.Terminal;

/// <summary>
/// Finds the shells of the integrated terminal (J7.10): PowerShell 7, Windows PowerShell, Git Bash, Command Prompt and
/// the WSL distributions (read from the registry: no process is started). Only installed shells are listed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellLocator : ITerminalShellLocator
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public IReadOnlyList<TerminalShell> Detect()
    {
        List<TerminalShell> shells = [];

        var pwsh = FindOnPath("pwsh.exe") ?? FirstExisting(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
        if (pwsh is not null)
            shells.Add(new TerminalShell(TerminalShellIds.PowerShell7, "PowerShell 7", $"{Quote(pwsh)} -NoLogo"));

        var windowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell))
            shells.Add(new TerminalShell(TerminalShellIds.WindowsPowerShell, "Windows PowerShell", $"{Quote(windowsPowerShell)} -NoLogo"));

        if (FindGitBash() is { } bash)
            shells.Add(new TerminalShell(TerminalShellIds.GitBash, "Git Bash", $"{Quote(bash)} --login -i"));

        var cmd = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec && File.Exists(comSpec)
            ? comSpec
            : Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (File.Exists(cmd)) shells.Add(new TerminalShell(TerminalShellIds.Cmd, "Invite de commandes", Quote(cmd)));

        var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (File.Exists(wsl))
        {
            foreach (var distribution in WslDistributions())
                shells.Add(new TerminalShell(TerminalShellIds.WslPrefix + distribution, $"WSL : {distribution}", $"{Quote(wsl)} -d {Quote(distribution)}"));
        }
        return shells;
    }

    /// <summary>Installed WSL distributions, the default one first; the ones Docker Desktop keeps for itself are left out.</summary>
    private static List<string> WslDistributions()
    {
        List<string> names = [];
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(LxssKey);
            if (root is null) return names;
            var defaultId = root.GetValue("DefaultDistribution") as string;
            string? defaultName = null;
            foreach (var id in root.GetSubKeyNames())
            {
                using var distribution = root.OpenSubKey(id);
                if (distribution?.GetValue("DistributionName") is not string name || name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)) defaultName = name;
                else names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            if (defaultName is not null) names.Insert(0, defaultName);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No WSL information readable: no WSL entry.
        }
        return names;
    }

    /// <summary>The <c>bash.exe</c> of Git for Windows: next to the git.exe on the PATH, else in the usual install folders.</summary>
    private static string? FindGitBash()
    {
        if (FindOnPath("git.exe") is { } git && Path.GetDirectoryName(git) is { } folder)
        {
            // <root>\cmd\git.exe, <root>\bin\git.exe or <root>\mingw64\bin\git.exe
            var root = new DirectoryInfo(folder);
            for (var level = 0; level < 3 && root is not null; level++, root = root.Parent)
            {
                if (FirstExisting(Path.Combine(root.FullName, "bin", "bash.exe")) is { } found) return found;
            }
        }
        return FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"));
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string? FindOnPath(string fileName)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>The path or argument between double quotes when it holds a space (CreateProcess command line).</summary>
    internal static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
