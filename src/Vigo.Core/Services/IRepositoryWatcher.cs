using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// Watches the working directory, the index and the references of a repository. Events are raised on a background
/// thread, once per burst of file-system events (debounced).
/// </summary>
public interface IRepositoryWatcher : IDisposable
{
    /// <summary><c>git status</c> may have changed (working directory, index, HEAD, current branch).</summary>
    event EventHandler? Changed;

    /// <summary>A branch, remote branch, tag, stash or HEAD may have moved, appeared or disappeared.</summary>
    event EventHandler? RefsChanged;
}

public interface IRepositoryWatcherFactory
{
    /// <summary>Starts watching <paramref name="repository"/>; null when it has no working directory (bare).</summary>
    IRepositoryWatcher? Watch(RepositoryInfo repository);
}
