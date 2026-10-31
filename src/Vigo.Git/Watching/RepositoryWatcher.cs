using Microsoft.Extensions.Logging;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;

namespace Vigo.Git.Watching;

/// <summary>
/// <see cref="IRepositoryWatcher"/> over two <see cref="FileSystemWatcher"/>s: the working directory (everything but
/// the Git directory) and the Git directory (index, HEAD, refs, stash reflog). Events are coalesced: <see cref="Changed"/>
/// and <see cref="RefsChanged"/> fire once the file system has been quiet for <see cref="QuietPeriod"/>, and at most
/// <see cref="MaxDelay"/> after the first event of a burst, so a long build still refreshes the status regularly.
/// </summary>
public sealed partial class RepositoryWatcher : IRepositoryWatcher
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _gitDirectoryPrefix;
    private readonly ITimer _timer;
    private readonly List<FileSystemWatcher> _watchers = [];
    private long? _burstStart;
    private WatchedChange _pending;
    private bool _disposed;

    /// <summary>Debouncing only, without file-system watchers (tests).</summary>
    internal RepositoryWatcher(string gitDirectory, TimeProvider time, ILogger logger)
    {
        _time = time;
        _logger = logger;
        _gitDirectoryPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitDirectory)) + Path.DirectorySeparatorChar;
        _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private RepositoryWatcher(
        string workingDirectory, string gitDirectory, TimeProvider time, ILogger logger, Func<CancellationToken, Task<IReadOnlyList<string>?>>? listIgnored)
        : this(gitDirectory, time, logger)
    {
        _workingPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)) + Path.DirectorySeparatorChar;
        _listIgnored = listIgnored;
        _watchers.Add(CreateWatcher(workingDirectory, path =>
        {
            if (!IsWorkTreePathRelevant(path, _gitDirectoryPrefix)) return;
            // A changed ignore file changes what is ignored: read the list again (the status is refreshed anyway).
            if (path.EndsWith(".gitignore", StringComparison.OrdinalIgnoreCase)) _ = LoadIgnoredAsync();
            else if (IsIgnored(path)) return;
            else if (Environment.TickCount64 - _lastLoad > IgnoredRefresh.TotalMilliseconds) _ = LoadIgnoredAsync();
            Signal(WatchedChange.Status);
        }));
        _ = LoadIgnoredAsync();
        _watchers.Add(CreateWatcher(gitDirectory, path => Signal(ClassifyGitDirectoryPath(path, _gitDirectoryPrefix))));
        // A linked worktree keeps its branches, remotes and packed-refs in the common Git directory.
        if (ResolveCommonDirectory(gitDirectory) is { } common)
        {
            var commonPrefix = Path.TrimEndingDirectorySeparator(common) + Path.DirectorySeparatorChar;
            _watchers.Add(CreateWatcher(common, path => Signal(ClassifyCommonDirectoryPath(path, commonPrefix))));
        }
    }

    /// <summary>The common Git directory of a linked worktree (its <c>commondir</c> file), null for a normal repository.</summary>
    internal static string? ResolveCommonDirectory(string gitDirectory)
    {
        try
        {
            var file = Path.Combine(gitDirectory, "commondir");
            if (!File.Exists(file)) return null;
            var common = Path.GetFullPath(File.ReadAllText(file).Trim(), gitDirectory);
            return Directory.Exists(common) ? common : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>In the common directory only the refs matter: its index and HEAD belong to the main worktree.</summary>
    internal static WatchedChange ClassifyCommonDirectoryPath(string fullPath, string commonPrefix) =>
        fullPath.Length > commonPrefix.Length && fullPath[commonPrefix.Length..].Replace('\\', '/') is "index" or "HEAD"
            ? WatchedChange.None
            : ClassifyGitDirectoryPath(fullPath, commonPrefix);

    public event EventHandler? Changed;

    public event EventHandler? RefsChanged;

    /// <summary>Starts watching a repository with a working directory.</summary>
    /// <param name="listIgnored">
    /// Lists the ignored untracked directories (relative, '/'-separated, trailing '/'): their events are dropped, so a build
    /// writing under bin/ or node_modules/ does not trigger a full status. Null: every event counts.
    /// </param>
    public static RepositoryWatcher Start(
        string workingDirectory, string gitDirectory, TimeProvider time, ILogger logger, Func<CancellationToken, Task<IReadOnlyList<string>?>>? listIgnored = null) =>
        new(workingDirectory, gitDirectory, time, logger, listIgnored);

    private readonly string? _workingPrefix;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>?>>? _listIgnored;
    private readonly CancellationTokenSource _stopping = new();
    private HashSet<string> _ignored = [];

    // An ignored directory that does not exist yet is unknown (the first build refreshes the status); the list is read again
    // at the first relevant event after this delay, so the directories it created are dropped from then on.
    private static readonly TimeSpan IgnoredRefresh = TimeSpan.FromSeconds(30);
    private long _lastLoad = long.MinValue / 2;

    private async Task LoadIgnoredAsync()
    {
        if (_listIgnored is null) return;
        _lastLoad = Environment.TickCount64;
        try
        {
            // Off the file system event thread; until the list is there, every event counts.
            var directories = await Task.Run(() => _listIgnored(_stopping.Token), _stopping.Token);
            if (directories is not null) _ignored = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Disposed.
        }
    }

    private bool IsIgnored(string fullPath) =>
        _workingPrefix is not null && fullPath.StartsWith(_workingPrefix, StringComparison.OrdinalIgnoreCase)
        && IsInIgnoredDirectory(fullPath[_workingPrefix.Length..].Replace('\\', '/'), _ignored);

    /// <summary>True when a '/'-separated relative path lies under one of the ignored directories ("bin/", "src/obj/").</summary>
    internal static bool IsInIgnoredDirectory(string relative, HashSet<string> ignored)
    {
        if (ignored.Count == 0) return false;
        for (var slash = relative.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = relative.IndexOf('/', slash + 1))
        {
            if (ignored.Contains(relative[..(slash + 1)])) return true;
        }
        // The directory itself (created or deleted).
        return ignored.Contains(relative + "/");
    }

    /// <summary>A working-directory path matters unless it is inside the Git directory (watched separately).</summary>
    internal static bool IsWorkTreePathRelevant(string fullPath, string gitDirectoryPrefix) =>
        !fullPath.StartsWith(gitDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(fullPath + Path.DirectorySeparatorChar, gitDirectoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Inside the Git directory, the index, HEAD and local branches change <c>git status</c>; HEAD, every ref and the
    /// stash reflog (<c>stash@{n}</c> entries live there) change the refs. Lock files and objects change nothing (git
    /// writes <c>index.lock</c>, then renames it to <c>index</c>).
    /// </summary>
    internal static WatchedChange ClassifyGitDirectoryPath(string fullPath, string gitDirectoryPrefix)
    {
        if (!fullPath.StartsWith(gitDirectoryPrefix, StringComparison.OrdinalIgnoreCase)) return WatchedChange.None;
        var relative = fullPath[gitDirectoryPrefix.Length..].Replace('\\', '/');
        if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return WatchedChange.None;
        return relative switch
        {
            "index" => WatchedChange.Status,
            "HEAD" or "packed-refs" => WatchedChange.Status | WatchedChange.Refs,
            _ when relative.StartsWith("refs/heads/", StringComparison.Ordinal) => WatchedChange.Status | WatchedChange.Refs,
            _ when relative.StartsWith("refs/", StringComparison.Ordinal) || relative == "logs/refs/stash" => WatchedChange.Refs,
            _ => WatchedChange.None,
        };
    }

    /// <summary>Records a file-system event and (re)arms the debounce timer.</summary>
    internal void Signal(WatchedChange change = WatchedChange.Status)
    {
        if (change == WatchedChange.None) return;
        lock (_lock)
        {
            if (_disposed) return;
            _pending |= change;
            var now = _time.GetTimestamp();
            _burstStart ??= now;
            var remaining = MaxDelay - _time.GetElapsedTime(_burstStart.Value, now);
            var due = remaining < QuietPeriod ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : QuietPeriod;
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _stopping.Cancel();
        foreach (var watcher in _watchers) watcher.Dispose();
        _timer.Dispose();
        _stopping.Dispose();
    }

    private void OnTimer()
    {
        WatchedChange pending;
        lock (_lock)
        {
            if (_disposed) return;
            _burstStart = null;
            pending = _pending;
            _pending = WatchedChange.None;
        }

        if (pending.HasFlag(WatchedChange.Status)) Changed?.Invoke(this, EventArgs.Empty);
        if (pending.HasFlag(WatchedChange.Refs)) RefsChanged?.Invoke(this, EventArgs.Empty);
    }

    private FileSystemWatcher CreateWatcher(string directory, Action<string> onPath)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => onPath(e.FullPath);
        watcher.Changed += (_, e) => onPath(e.FullPath);
        watcher.Deleted += (_, e) => onPath(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            onPath(e.OldFullPath);
            onPath(e.FullPath);
        };
        watcher.Error += (_, e) =>
        {
            // Buffer overflow (thousands of changes at once) or watched directory gone: refresh anyway.
            LogWatcherError(_logger, directory, e.GetException().Message);
            Signal(WatchedChange.Status | WatchedChange.Refs);
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "File system watcher error on {Directory}: {Message}")]
    private static partial void LogWatcherError(ILogger logger, string directory, string message);
}

/// <summary>What a file-system event may have changed.</summary>
[Flags]
internal enum WatchedChange
{
    None = 0,
    Status = 1,
    Refs = 2,
}

/// <summary>Creates a <see cref="RepositoryWatcher"/> per repository tab.</summary>
public sealed partial class RepositoryWatcherFactory(TimeProvider time, ILogger<RepositoryWatcher> logger, GitCli? cli = null) : IRepositoryWatcherFactory
{
    // Untracked ignored folders, collapsed (bin/, node_modules/); -z: paths come unquoted.
    private static readonly string[] IgnoredDirectoriesArguments = ["ls-files", "-o", "-i", "--exclude-standard", "--directory", "-z"];

    private async Task<IReadOnlyList<string>?> ListIgnoredDirectoriesAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (await cli!.RunAsync(workingDirectory, IgnoredDirectoriesArguments, cancellationToken) is not Success<CliOutput> s) return null;
        return s.Value.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith('/')).ToList();
    }

    public IRepositoryWatcher? Watch(RepositoryInfo repository)
    {
        if (repository.WorkingDirectory is not { } workingDirectory || !Directory.Exists(workingDirectory)) return null;
        try
        {
            return RepositoryWatcher.Start(workingDirectory, repository.GitDirectory, time, logger,
                cli is null ? null : ct => ListIgnoredDirectoriesAsync(workingDirectory, ct));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // E.g. some network shares: the status is still shown, just not refreshed automatically.
            LogWatchFailed(logger, workingDirectory, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot watch {Directory}: {Message}")]
    private static partial void LogWatchFailed(ILogger logger, string directory, string message);
}
