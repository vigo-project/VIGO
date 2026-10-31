using System.Diagnostics;

namespace Vigo.App.Services;

/// <summary>Starts the Windows Explorer.</summary>
public static class Explorer
{
    // Full path: without the shell, CreateProcess would also search the current directory (e.g. a cloned repo).
    private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Runs explorer.exe with these (already quoted) arguments.</summary>
    public static void Open(string arguments) =>
        Process.Start(new ProcessStartInfo(ExplorerPath, arguments) { UseShellExecute = false });
}
