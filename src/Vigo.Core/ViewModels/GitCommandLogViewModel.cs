using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>One command of the Git log: its command line, then, once it ends, its duration, exit code and output.</summary>
/// <remarks>User-facing texts: <c>GitLog.*</c> resources (ADR-075).</remarks>
public sealed partial class GitCommandEntryViewModel(GitCommandRun run) : ObservableObject
{
    public string Time { get; } = run.StartedAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string CommandLine => run.CommandLine;

    public bool IsRead => run.IsRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailed), nameof(HasOutput))]
    public partial string Output { get; set; } = string.Empty;

    /// <summary>"running…", "120 ms", "code 1 · 85 ms", "canceled" (localized).</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = Loc.Get("GitLog.Running");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    public partial bool IsRunning { get; set; } = true;

    private bool _failed;
    private bool _succeeded;

    public bool IsFailed => !IsRunning && _failed;

    /// <summary>Ended with exit code 0 (a canceled command is neither a success nor a failure).</summary>
    public bool IsSucceeded => _succeeded;

    public bool HasOutput => Output.Length > 0;

    /// <summary>Takes the final state of the run (exit code, duration, output).</summary>
    public void Refresh()
    {
        if (!run.IsCompleted) return;
        _failed = run.ExitCode is not 0 and not null;
        _succeeded = run.ExitCode is 0;
        Output = run.Output;
        Summary = run switch
        {
            { IsCanceled: true } => Loc.Get("GitLog.Canceled"),
            { ExitCode: not 0 and { } code } => Loc.Format("GitLog.ExitCode", code, FormatDuration(run.Duration)),
            _ => FormatDuration(run.Duration),
        };
        IsRunning = false;
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSucceeded));
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? string.Create(CultureInfo.CurrentCulture, $"{duration.TotalMilliseconds:0} ms")
            : string.Create(CultureInfo.CurrentCulture, $"{duration.TotalSeconds:0.0} s");
}

/// <summary>
/// "Git" tab of the terminal panel: the git.exe commands VIGO runs in this repository, as they start and end, so that
/// one can follow what the application does. Reads (status, log…) are left out unless <see cref="ShowAll"/> is on,
/// and are then only listed from that moment.
/// </summary>
public sealed partial class GitCommandLogViewModel : ObservableObject, IDisposable
{
    private const int MaxEntries = 1000;

    private readonly GitCommandLog? _log;
    private readonly string _workingDirectory;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Dictionary<GitCommandRun, GitCommandEntryViewModel> _byRun = [];
    private volatile bool _showAll;

    public GitCommandLogViewModel(GitCommandLog? log, string workingDirectory)
    {
        _log = log;
        _workingDirectory = Normalize(workingDirectory);
        if (log is null) return;
        log.Started += OnStarted;
        log.Completed += OnCompleted;
    }

    public ObservableCollection<GitCommandEntryViewModel> Entries { get; } = [];

    /// <summary>No command listed yet (the tab shows a hint).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    /// <summary>Also list the commands that only read the repository (verbose).</summary>
    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (_showAll == value) return;
            _showAll = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        _byRun.Clear();
        IsEmpty = true;
    }

    private void OnStarted(GitCommandRun run)
    {
        // Runs on the thread that starts git: only cheap, thread-safe tests here.
        if (!Normalize(run.WorkingDirectory).Equals(_workingDirectory, StringComparison.OrdinalIgnoreCase)) return;
        if (run.IsRead && !_showAll) return;
        Post(() => Add(run));
    }

    private void OnCompleted(GitCommandRun run) => Post(() =>
    {
        if (_byRun.Remove(run, out var entry)) entry.Refresh();
    });

    private void Add(GitCommandRun run)
    {
        var entry = new GitCommandEntryViewModel(run);
        // Already over when the UI thread gets here (a short command): show its final state at once.
        if (run.IsCompleted) entry.Refresh();
        else _byRun[run] = entry;
        Entries.Add(entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        IsEmpty = false;
    }

    private void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(static state => ((Action)state!)(), action);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public void Dispose()
    {
        if (_log is null) return;
        _log.Started -= OnStarted;
        _log.Completed -= OnCompleted;
    }
}
