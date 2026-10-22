using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// Recent repositories, shared by the welcome tabs: pinned entries first, then most recently opened first.
/// </summary>
/// <remarks>
/// Mutations happen on the caller's thread (the UI thread) and raise <see cref="Changed"/> there; file I/O runs on
/// the thread pool and saves are serialized, the last one winning.
/// </remarks>
public sealed class RecentRepositories(IRecentRepositoryStore store, TimeProvider time) : IDisposable
{
    /// <summary>Unpinned entries kept beyond the pinned ones.</summary>
    public const int MaxUnpinned = 30;

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private Task? _load;
    private IReadOnlyList<RecentRepository> _items = [];

    /// <summary>Current list, in display order. Empty until <see cref="LoadAsync"/> completes.</summary>
    public IReadOnlyList<RecentRepository> Items => _items;

    public event EventHandler? Changed;

    /// <summary>Loads the saved list once; later calls wait for the same load.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) => (_load ??= LoadCoreAsync()).WaitAsync(cancellationToken);

    /// <summary>Moves a repository that was just opened to the top of the unpinned entries (keeps its pin).</summary>
    public async Task RecordOpenedAsync(RepositoryInfo repository, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        var path = repository.WorkingDirectory ?? repository.GitDirectory;
        var existing = Find(path);
        var entry = new RecentRepository(path, repository.Name, repository.HeadName, time.GetUtcNow(), existing?.IsPinned ?? false);
        await UpdateAsync([entry, .. _items.Where(r => !SamePath(r.Path, path))], cancellationToken);
    }

    public async Task SetPinnedAsync(string path, bool isPinned, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        if (Find(path) is { } existing && existing.IsPinned != isPinned)
        {
            await UpdateAsync([.. _items.Select(r => ReferenceEquals(r, existing) ? r with { IsPinned = isPinned } : r)], cancellationToken);
        }
    }

    public async Task RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        if (Find(path) is not null)
        {
            await UpdateAsync([.. _items.Where(r => !SamePath(r.Path, path))], cancellationToken);
        }
    }

    private async Task LoadCoreAsync()
    {
        // Not tied to the first caller's token: the load is shared.
        var loaded = await Task.Run(() => store.LoadAsync(CancellationToken.None));
        // Duplicates can only come from a hand-edited file: keep the most recent entry.
        _items = Normalize(loaded.OrderByDescending(r => r.LastOpened).DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task UpdateAsync(IEnumerable<RecentRepository> items, CancellationToken cancellationToken)
    {
        var snapshot = Normalize(items);
        _items = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);

        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            // A newer change may have been made while waiting: it will be saved by its own call.
            if (ReferenceEquals(snapshot, _items))
            {
                await Task.Run(() => store.SaveAsync(snapshot, CancellationToken.None), CancellationToken.None);
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Dispose() => _saveLock.Dispose();

    private RecentRepository? Find(string path) => _items.FirstOrDefault(r => SamePath(r.Path, path));

    private static RecentRepository[] Normalize(IEnumerable<RecentRepository> items)
    {
        // Stable sorts: pinned first, each group by most recent opening.
        var ordered = items.OrderByDescending(r => r.IsPinned).ThenByDescending(r => r.LastOpened).ToList();
        var unpinned = 0;
        return [.. ordered.Where(r => r.IsPinned || ++unpinned <= MaxUnpinned)];
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
