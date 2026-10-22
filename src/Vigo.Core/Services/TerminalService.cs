using System.Collections.Immutable;
using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// Shells of the integrated terminal (J7.10): detected once, the default one coming from the preferences (first
/// detected shell when none is chosen or the chosen one is gone).
/// </summary>
public sealed class TerminalService(ITerminalShellLocator locator, ITerminalSessionFactory sessions, Preferences preferences, GitCommandLog? commands = null)
{
    /// <summary>Commands run by git.exe, listed in the Git tab of the panel (travels with the terminal services).</summary>
    public GitCommandLog? Commands => commands;

    // Detected in the background from the creation of the service: the first use rarely waits.
    private readonly Task<ImmutableArray<TerminalShell>> _shells = Task.Run(() => ImmutableArray.CreateRange(locator.Detect()));

    public ITerminalSessionFactory Sessions => sessions;

    public ImmutableArray<TerminalShell> Shells => _shells.GetAwaiter().GetResult();

    /// <summary>The shell a new terminal runs; null when no shell is installed.</summary>
    public TerminalShell? DefaultShell =>
        Shells.FirstOrDefault(s => s.Id == preferences.Current.TerminalShell) ?? Shells.FirstOrDefault();

    /// <summary>
    /// Command line to run for <paramref name="shell"/>: the custom command of the preferences replaces the one of the
    /// default shell only (it is that shell's command, shown next to its choice).
    /// </summary>
    public string CommandLineOf(TerminalShell shell) =>
        preferences.Current.TerminalCommand is { } custom && shell == DefaultShell ? custom : shell.CommandLine;
}
