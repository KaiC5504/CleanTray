using System.Globalization;

namespace CleanTray.Core;

public sealed record PopupResolution(string Token, ulong IconId, uint Pid, PopupOutcome Outcome);

/// <summary>
/// Tracks balloons shown as toasts until the user answers. Every popup is reported exactly once:
/// by whichever of click, dismissal, expiry or ResolveAll gets there first.
/// </summary>
public sealed class PopupTracker : IDisposable
{
    readonly Action<PopupResolution> _report;
    readonly TimeSpan _expiry;
    readonly Dictionary<string, Entry> _pending = new();
    readonly object _gate = new();
    long _next;
    bool _disposed;

    /// <param name="expiry">Backstop for a toast whose Dismissed event never arrives, such as one queued by Focus Assist.</param>
    public PopupTracker(Action<PopupResolution> report, TimeSpan expiry)
    {
        _report = report;
        _expiry = expiry;
    }

    public string Add(ulong iconId, uint pid)
    {
        var token = Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        lock (_gate)
        {
            if (_disposed) return token;
            var entry = new Entry(iconId, pid);
            entry.Expiry = new Timer(_ => Expire(token), null, _expiry, Timeout.InfiniteTimeSpan);
            _pending[token] = entry;
        }
        return token;
    }

    /// <summary>False when the popup was already resolved or is unknown.</summary>
    public bool Resolve(string token, PopupOutcome outcome)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_pending.Remove(token, out entry)) return false;
        }
        entry.Expiry?.Dispose();
        _report(new PopupResolution(token, entry.IconId, entry.Pid, outcome));
        return true;
    }

    public void ResolveAll(PopupOutcome outcome)
    {
        string[] tokens;
        lock (_gate) tokens = _pending.Keys.ToArray();
        foreach (var token in tokens) Resolve(token, outcome);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var e in _pending.Values) e.Expiry?.Dispose();
            _pending.Clear();
        }
    }

    // Runs on a timer thread, where an escaping exception would end the process.
    void Expire(string token)
    {
        try
        {
            Resolve(token, PopupOutcome.TimedOut);
        }
        catch
        {
        }
    }

    sealed class Entry(ulong iconId, uint pid)
    {
        public readonly ulong IconId = iconId;
        public readonly uint Pid = pid;
        public Timer? Expiry;
    }
}
