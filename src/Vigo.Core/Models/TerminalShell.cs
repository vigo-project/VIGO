namespace Vigo.Core.Models;

/// <summary>A shell the integrated terminal can run (J7.10, ADR-071).</summary>
/// <param name="Id">Stable identifier stored in <c>preferences.json</c> (<see cref="TerminalShellIds"/>).</param>
/// <param name="Name">Name shown in the tab and the menu ("PowerShell 7", "WSL : Ubuntu").</param>
/// <param name="CommandLine">Full command line handed to CreateProcess (executable path quoted when needed, then arguments).</param>
public sealed record TerminalShell(string Id, string Name, string CommandLine)
{
    public override string ToString() => Name;
}

/// <summary>Identifiers of the shells VIGO knows how to find.</summary>
public static class TerminalShellIds
{
    public const string PowerShell7 = "pwsh";
    public const string WindowsPowerShell = "powershell";
    public const string GitBash = "git-bash";
    public const string Cmd = "cmd";

    /// <summary>Prefix of a WSL distribution: <c>wsl:Ubuntu</c>.</summary>
    public const string WslPrefix = "wsl:";
}
