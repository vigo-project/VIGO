using Vigo.Core.Models;

namespace Vigo.Git.Ssh;

/// <summary>
/// Builds the <c>GIT_SSH_COMMAND</c> applying VIGO's SSH settings to a network command of git.exe:
/// the user's <c>~/.gitconfig</c> and <c>~/.ssh/config</c> are left alone.
/// </summary>
internal static class SshCommand
{
    /// <summary>
    /// Command line run by git (through its POSIX shell) instead of <c>ssh</c>, or null when the settings change
    /// nothing (no key and no Windows agent). <paramref name="windowsSsh"/>: the Windows OpenSSH client, when installed.
    /// </summary>
    public static string? Build(SshSettings settings, string? windowsSsh)
    {
        var useWindows = settings.UseWindowsAgent && windowsSsh is not null;
        if (settings.KeyPath is null && !useWindows) return null;

        // Git's own ssh unless the Windows agent is wanted: only the Windows client talks to the ssh-agent service.
        var command = useWindows ? Quote(windowsSsh!) : "ssh";
        // Only that key is offered (not every key of ~/.ssh and of the agent: servers refuse after a few failures).
        if (settings.KeyPath is { } key) command += $" -i {Quote(key)} -o IdentitiesOnly=yes";
        return command;
    }

    /// <summary>Single-quoted for the shell of Git for Windows, with forward slashes (accepted by both clients).</summary>
    internal static string Quote(string path) => "'" + path.Replace('\\', '/').Replace("'", @"'\''", StringComparison.Ordinal) + "'";
}
