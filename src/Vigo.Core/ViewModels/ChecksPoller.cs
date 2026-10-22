namespace Vigo.Core.ViewModels;

/// <summary>
/// Runs a read now, then again after <paramref name="interval"/> for as long as the read asks for it (a check is still running).
/// A new <see cref="Start"/> replaces the previous loop; the loop also stops with <paramref name="lifetime"/>.
/// </summary>
internal sealed class ChecksPoller(TimeProvider time, TimeSpan interval, CancellationToken lifetime) : IDisposable
{
    private CancellationTokenSource? _source;

    /// <summary>The running read-and-wait loop (tests await it); completed when nothing is running any more.</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <param name="read">One read; true when another one is due later.</param>
    public void Start(Func<CancellationToken, Task<bool>> read)
    {
        Dispose();
        var source = _source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Running = WatchAsync(read, source.Token);
    }

    /// <summary>Stops the loop, keeping the poller reusable.</summary>
    public void Cancel() => _source?.Cancel();

    public void Dispose()
    {
        _source?.Cancel();
        _source?.Dispose();
        _source = null;
    }

    private async Task WatchAsync(Func<CancellationToken, Task<bool>> read, CancellationToken cancellationToken)
    {
        try
        {
            while (await read(cancellationToken))
                await Task.Delay(interval, time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
