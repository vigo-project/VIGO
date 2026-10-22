using System.Threading.Channels;
using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// A shell running behind a pseudo console (ConPTY): bytes of VT sequences in both directions. The terminal page
/// (xterm.js) interprets and draws them; the session knows nothing about the screen except its size.
/// </summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>
    /// Output of the shell (UTF-8, VT sequences), in chunks; completes when the shell and its console are gone. The
    /// channel is bounded: a reader that stops reading makes the shell wait (flow control).
    /// </summary>
    ChannelReader<byte[]> Output { get; }

    /// <summary>Sends keyboard input (UTF-8) to the shell; never blocks.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);

    /// <summary>Completes with the exit code of the shell process.</summary>
    Task<int> Exited { get; }
}

/// <summary>Starts shells behind a pseudo console.</summary>
public interface ITerminalSessionFactory
{
    /// <summary>Starts <paramref name="commandLine"/> in <paramref name="workingDirectory"/>; throws <see cref="IOException"/> when it cannot start.</summary>
    ITerminalSession Start(string commandLine, string workingDirectory, int columns, int rows);
}

/// <summary>Finds the shells installed on this machine.</summary>
public interface ITerminalShellLocator
{
    /// <summary>Installed shells, in the order the menu shows them.</summary>
    IReadOnlyList<TerminalShell> Detect();
}
