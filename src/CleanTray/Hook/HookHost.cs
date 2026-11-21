using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using System.Windows.Threading;
using CleanTray.Core;
using CleanTray.Native;

namespace CleanTray.Hook;

enum HostState
{
    Attaching,
    Attached,
    AttachFailed,
    Paused,
}

/// <summary>
/// Serves the hook DLL's pipe and keeps it injected into Explorer. All events are raised on the
/// dispatcher, in the order the DLL sent them.
/// </summary>
sealed class HookHost : IDisposable
{
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan FailedRetry = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StableSession = TimeSpan.FromMinutes(1);
    const int MaxAttemptsPerMinute = 5;
    const int ReadChunk = 64 * 1024;

    readonly Dispatcher _dispatcher;
    readonly Injector _injector = new();
    readonly string _pipeName = $"CleanTray.Hook.S{Process.GetCurrentProcess().SessionId}";
    readonly CancellationTokenSource _stop = new();
    readonly AutoResetEvent _wake = new(false);
    readonly ManualResetEventSlim _sessionEnded = new(true);
    readonly Queue<long> _attempts = new();
    readonly object _sendGate = new();
    Thread? _thread;
    Channel<byte[]>? _outbox;
    TimeSpan _backoff = MinBackoff;
    volatile bool _paused;
    volatile bool _connected;
    volatile uint _explorerPid;

    public HookHost(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public event Action<HookEvent>? EventReceived;
    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<HostState>? StateChanged;

    public Task StartAsync()
    {
        if (_thread is not null) return Task.CompletedTask;
        // A dedicated thread because a hook is owned by the thread that installed it.
        _thread = new Thread(Run) { IsBackground = true, Name = "CleanTray.HookHost" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public void Send(byte[] command)
    {
        lock (_sendGate)
        {
            if (_outbox is null || !_outbox.Writer.TryWrite(command))
                Log.Write($"send: dropped command {BitConverter.ToUInt16(command, 4)}, not connected");
        }
    }

    /// <summary>Explorer broadcast TaskbarCreated: a new Explorer needs a fresh injection.</summary>
    public void OnTaskbarCreated()
    {
        var tray = Win32.FindWindowW("Shell_TrayWnd", null);
        Win32.GetWindowThreadProcessId(tray, out var pid);
        if (_connected && pid == _explorerPid) return;
        _wake.Set();
    }

    public void Pause()
    {
        if (_paused) return;
        _paused = true;
        Raise(HostState.Paused);
        Send(CommandWriter.ReleaseAllAndIdle());
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        _wake.Set();
    }

    /// <summary>Asks the DLL to release everything and waits up to <paramref name="wait"/> for it to hang up.</summary>
    public void Stop(TimeSpan wait)
    {
        if (_stop.IsCancellationRequested) return;
        _paused = true;
        if (_connected)
        {
            Send(CommandWriter.ReleaseAllAndIdle());
            if (!_sessionEnded.Wait(wait)) Log.Write("stop: the DLL did not hang up in time");
        }
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(1));

    void Run()
    {
        long nextAttempt = 0;
        while (!_stop.IsCancellationRequested)
        {
            if (_paused)
            {
                Wait(Timeout.InfiniteTimeSpan);
                nextAttempt = 0;
                continue;
            }

            var now = Environment.TickCount64;
            while (_attempts.Count > 0 && now - _attempts.Peek() >= 60_000) _attempts.Dequeue();
            var due = nextAttempt;
            if (_attempts.Count >= MaxAttemptsPerMinute) due = Math.Max(due, _attempts.Peek() + 60_000);
            if (due > now)
            {
                // A wake (TaskbarCreated, Resume) skips the backoff but not the per-minute cap.
                if (Wait(TimeSpan.FromMilliseconds(due - now))) nextAttempt = 0;
                continue;
            }

            _attempts.Enqueue(now);
            Raise(HostState.Attaching);
            var started = Environment.TickCount64;
            bool attached;
            try
            {
                attached = Attempt();
            }
            catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException or IOException)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Write($"attach attempt failed: {LogText.Error(e)}");
                _injector.Unhook();
                attached = false;
            }

            if (!attached)
            {
                Raise(HostState.AttachFailed);
                nextAttempt = Environment.TickCount64 + (long)FailedRetry.TotalMilliseconds;
                continue;
            }
            if (Environment.TickCount64 - started >= StableSession.TotalMilliseconds) _backoff = MinBackoff;
            nextAttempt = Environment.TickCount64 + (long)_backoff.TotalMilliseconds;
            _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
        }
        _injector.Unhook();
    }

    // True when woken early, false on timeout or stop.
    bool Wait(TimeSpan timeout) =>
        WaitHandle.WaitAny([_stop.Token.WaitHandle, _wake], timeout) == 1;

    /// <summary>One injection and, if Hello arrives, the whole session. False if it never attached.</summary>
    bool Attempt()
    {
        NamedPipeServerStream pipe;
        try
        {
            pipe = CreatePipe();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // FirstPipeInstance refuses a name someone else already holds.
            Log.Write($"pipe: cannot create {_pipeName}: {LogText.Error(e)}");
            return false;
        }

        using (pipe)
        {
            HelloEvent? hello;
            var buffer = new FrameBuffer();
            if (!_injector.Inject()) return false;
            try
            {
                hello = WaitForHello(pipe, buffer);
            }
            finally
            {
                // Never leave the hook in place without a served pipe: the DLL would
                // reactivate on every tray message and retry the connection.
                _injector.Unhook();
            }
            if (hello is null) return false;

            RunSession(pipe, buffer, hello);
            return true;
        }
    }

    NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    HelloEvent? WaitForHello(NamedPipeServerStream pipe, FrameBuffer buffer)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(HelloTimeout);
        try
        {
            pipe.WaitForConnectionAsync(deadline.Token).GetAwaiter().GetResult();
            if (!Win32.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != _injector.TrayPid)
            {
                Log.Write($"pipe: client pid {client} is not Explorer ({_injector.TrayPid}), dropping it");
                return null;
            }
            while (true)
            {
                var ev = buffer.Next(out var corrupt);
                if (corrupt) return null;
                if (ev is HelloEvent h)
                {
                    if (h.ProtocolVersion == 1) return h;
                    Log.Write($"pipe: unsupported protocol {h.ProtocolVersion}");
                    return null;
                }
                if (ev is not null)
                {
                    Log.Write($"pipe: {ev.GetType().Name} before Hello");
                    return null;
                }
                if (!buffer.Fill(pipe, deadline.Token)) return null;
            }
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            Log.Write("pipe: no Hello within 5 s of injecting");
            return null;
        }
        catch (IOException e)
        {
            Log.Write($"pipe: broke before Hello: {LogText.Error(e)}");
            return null;
        }
    }

    void RunSession(NamedPipeServerStream pipe, FrameBuffer buffer, HelloEvent hello)
    {
        var started = Stopwatch.StartNew();
        var outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _explorerPid = hello.ExplorerPid;
        _sessionEnded.Reset();
        lock (_sendGate) _outbox = outbox;
        _connected = true;
        var writer = Task.Run(() => WriteLoop(pipe, outbox.Reader));
        Log.Write($"pipe: Hello from explorer {hello.ExplorerPid}");
        Post(() => Connected?.Invoke());
        Raise(HostState.Attached);
        Post(() => EventReceived?.Invoke(hello));
        if (_paused) Send(CommandWriter.ReleaseAllAndIdle());

        try
        {
            while (true)
            {
                var ev = buffer.Next(out var corrupt);
                if (corrupt)
                {
                    Log.Write("pipe: corrupt frame header, disconnecting");
                    break;
                }
                if (ev is not null)
                {
                    Post(() => EventReceived?.Invoke(ev));
                    continue;
                }
                if (!buffer.Fill(pipe, _stop.Token)) break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested) Log.Write($"pipe: read ended: {LogText.Error(e)}");
        }
        finally
        {
            _connected = false;
            lock (_sendGate)
            {
                _outbox = null;
                outbox.Writer.TryComplete();
            }
            // Teardown of a session that did attach must never surface as a failed attach.
            try
            {
                if (pipe.IsConnected) pipe.Disconnect();
                writer.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception e)
            {
                Log.Write($"pipe: teardown: {LogText.Error(e)}");
            }
            Log.Write($"pipe: session ended after {started.Elapsed.TotalSeconds:F1} s");
            Post(() => Disconnected?.Invoke());
            _sessionEnded.Set();
        }
    }

    static async Task WriteLoop(NamedPipeServerStream pipe, ChannelReader<byte[]> reader)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync())
                await pipe.WriteAsync(frame);
        }
        catch (Exception e)
        {
            Log.Write($"pipe: write failed: {LogText.Error(e)}");
        }
    }

    void Raise(HostState s) => Post(() => StateChanged?.Invoke(s));

    void Post(Action a) => _dispatcher.BeginInvoke(a);

    sealed class FrameBuffer
    {
        byte[] _buf = new byte[ReadChunk];
        int _start;
        int _end;

        public HookEvent? Next(out bool corrupt)
        {
            while (EventReader.TryRead(_buf.AsSpan(_start, _end - _start), out var ev, out var consumed, out corrupt))
            {
                _start += consumed;
                if (ev is not null) return ev;
            }
            return null;
        }

        /// <summary>Reads more bytes; false when the pipe closed.</summary>
        public bool Fill(Stream pipe, CancellationToken ct)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_buf.Length - _end < ReadChunk)
            {
                // A complete frame never exceeds the cap, so an incomplete one never needs more room.
                var size = Math.Min(Math.Max(_buf.Length * 2, _end + ReadChunk), Frames.HeaderSize + Frames.MaxFrameBytes + ReadChunk);
                if (size - _end <= 0) return false;
                Array.Resize(ref _buf, size);
            }
            var n = pipe.ReadAsync(_buf.AsMemory(_end), ct).AsTask().GetAwaiter().GetResult();
            _end += n;
            return n > 0;
        }
    }
}
