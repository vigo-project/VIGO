namespace Vigo.Core.ViewModels;

/// <summary>
/// Lets the graph read the history lazily (J7.28): the reader of <c>git log</c> stops after each page until the view asks
/// for more, and git blocks on its full pipe meanwhile. The commit order and the lanes stay incremental, nothing is read twice.
/// </summary>
internal sealed class PagePump : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts;
    private int _wanted;
    private TaskCompletionSource _demand = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="wanted">Commits to read before the first stop.</param>
    public PagePump(int wanted, CancellationToken lifetime)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _wanted = wanted;
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>The reader reached the end of the history (nothing more to ask for).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Completes the next time the reader stops (or ends); take it before the reader can run.</summary>
    public Task NextPause()
    {
        lock (_gate) return _paused.Task;
    }

    /// <summary>Reader side, after a page: stops while <paramref name="loaded"/> commits are enough.</summary>
    public async ValueTask PauseAsync(int loaded)
    {
        TaskCompletionSource stopped;
        lock (_gate)
        {
            stopped = _paused;
            _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        stopped.TrySetResult();
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (loaded < _wanted) return;
                _demand = new(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _demand.Task;
            }
            await wait.WaitAsync(Token);
        }
    }

    /// <summary>View side: reads on until more than <paramref name="count"/> commits are loaded (a page more).</summary>
    public void Demand(int count)
    {
        lock (_gate)
        {
            if (count <= _wanted) return;
            _wanted = count;
            _demand.TrySetResult();
        }
    }

    public void Finish()
    {
        IsFinished = true;
        lock (_gate) _paused.TrySetResult();
    }

    public void Cancel() => _cts.Cancel();

    public void Dispose() => _cts.Dispose();
}
