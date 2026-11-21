namespace CleanTray.Core;

/// <summary>
/// Collapses a burst of requests into one run. The first request opens a window of
/// <c>delay</c>; requests inside it add nothing, so latency stays bounded however long the burst
/// lasts. The action runs on a thread-pool thread. Cancel and IsPending let a caller that saved
/// on its own (or is exiting) take over.
/// </summary>
public sealed class Debouncer : IDisposable
{
    readonly TimeSpan _delay;
    readonly Action _action;
    readonly object _gate = new();
    Timer? _timer;
    bool _pending;
    bool _disposed;

    public Debouncer(TimeSpan delay, Action action)
    {
        _delay = delay;
        _action = action;
    }

    public void Request()
    {
        lock (_gate)
        {
            if (_disposed || _pending) return;
            _pending = true;
            _timer = new Timer(_ => Fire(), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    public bool IsPending
    {
        get
        {
            lock (_gate) return _pending;
        }
    }

    /// <summary>Drops a waiting request, for when the caller has just done the work itself.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Cancel();
    }

    void Fire()
    {
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }
        _action();
    }
}
