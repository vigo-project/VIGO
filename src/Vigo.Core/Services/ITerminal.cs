using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>Finds the shells installed on this machine.</summary>
public interface ITerminalShellLocator
{
    /// <summary>Installed shells, in the order the menu shows them.</summary>
    IReadOnlyList<TerminalShell> Detect();
}
