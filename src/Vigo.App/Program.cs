using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Velopack;
using Windows.ApplicationModel.Activation;

namespace Vigo.App;

/// <summary>
/// Entry point (replaces the XAML-generated Main, see DISABLE_XAML_GENERATED_MAIN):
/// allows measuring cold startup and exposing command-line options.
/// </summary>
public static partial class Program
{
    /// <summary>Command-line options parsed at startup.</summary>
    public static StartupOptions Options { get; private set; } = new();

    [STAThread]
    private static int Main(string[] args)
    {
        // First thing: Velopack handles the installer hooks (install, update, uninstall) and exits when it must.
        VelopackApp.Build().Run();
        Options = StartupOptions.Parse(args);
        if (!RegisterSingleInstance()) return 0;
        // Alone for real (not the probe, not a test instance next to another one): install the update downloaded by a previous run.
        if (Options.StartupProbeFile is null && Environment.GetEnvironmentVariable("VIGO_ALLOW_MULTIPLE") is null)
            new Services.VelopackUpdater().ApplyPendingAtLaunch(args);
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ignored =>
        {
            UiQueue = DispatcherQueue.GetForCurrentThread();
            var context = new DispatcherQueueSynchronizationContext(UiQueue);
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>UI thread queue; set when the application starts.</summary>
    private static DispatcherQueue? UiQueue;

    /// <summary>Opens a path (null: just show the window) in the running instance; set by the application once its windows exist.</summary>
    public static Func<string?, Task>? ActivationHandler { get; set; }

    /// <summary>
    /// One VIGO per user (J9.5): two instances would each rewrite the settings files from their own memory and run
    /// their own watchers and automatic fetches. A second launch hands its command line to the first one and exits.
    /// Not for the startup probe, nor with <c>VIGO_ALLOW_MULTIPLE</c> (test instances next to the user's own).
    /// </summary>
    private static bool RegisterSingleInstance()
    {
        if (Options.StartupProbeFile is not null || Environment.GetEnvironmentVariable("VIGO_ALLOW_MULTIPLE") is not null) return true;
        var instance = AppInstance.FindOrRegisterForKey("vigo");
        if (instance.IsCurrent)
        {
            instance.Activated += OnActivated;
            return true;
        }
        // This process was just launched by the user: it may let the first instance take the foreground.
        AllowSetForegroundWindow((uint)instance.ProcessId);
        // Off the STA thread: the redirection waits for the other process to answer.
        var arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        Task.Run(() => instance.RedirectActivationToAsync(arguments).AsTask()).GetAwaiter().GetResult();
        return false;
    }

    private static void OnActivated(object? sender, AppActivationArguments e)
    {
        var path = e.Data is ILaunchActivatedEventArgs launch ? StartupOptions.Parse(SplitArguments(launch.Arguments)).OpenPath : null;
        UiQueue?.TryEnqueue(() =>
        {
            if (ActivationHandler is { } handler) _ = handler(path);
        });
    }

    /// <summary>Words of a command line; double quotes group words (a path with spaces).</summary>
    internal static string[] SplitArguments(string? commandLine)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var c in commandLine ?? string.Empty)
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
        return [.. words];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("Microsoft.ui.xaml.dll")]
    private static partial void XamlCheckProcessRequirements();
}

/// <summary>
/// <c>--open &lt;path&gt;</c>: opens a repository at launch.
/// <c>--startup-probe &lt;file&gt;</c>: writes the time to first frame (ms), then exits.
/// </summary>
public sealed record StartupOptions(string? OpenPath = null, string? StartupProbeFile = null)
{
    public static StartupOptions Parse(string[] args)
    {
        string? open = null, probe = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--open" when i + 1 < args.Length: open = args[++i]; break;
                case "--startup-probe" when i + 1 < args.Length: probe = args[++i]; break;
            }
        }
        return new StartupOptions(open, probe);
    }
}
