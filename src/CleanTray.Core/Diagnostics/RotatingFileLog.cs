using System.Collections.Concurrent;
using System.Text;

namespace CleanTray.Core;

/// <summary>
/// Appends lines on a background thread so callers never wait on the disk. When the next line
/// would take the file past the cap, it is moved to "&lt;path&gt;.1" (replacing any older one).
/// </summary>
public sealed class RotatingFileLog : IDisposable
{
    readonly string _path;
    readonly long _maxBytes;
    readonly BlockingCollection<string> _queue = new();
    readonly Thread _thread;
    readonly object _gate = new();
    readonly ManualResetEventSlim _idle = new(true);
    int _pending;

    public RotatingFileLog(string path, long maxBytes)
    {
        _path = path;
        _maxBytes = maxBytes;
        _thread = new Thread(Run) { IsBackground = true, Name = "CleanTray.Log" };
        _thread.Start();
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            if (_queue.IsAddingCompleted) return;
            _pending++;
            _idle.Reset();
            _queue.Add(line);
        }
    }

    /// <summary>Waits until every line written so far is on disk.</summary>
    public bool Flush(TimeSpan timeout) => _idle.Wait(timeout);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_queue.IsAddingCompleted) return;
            _queue.CompleteAdding();
        }
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    void Run()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            Append(line);
            lock (_gate)
            {
                if (--_pending == 0) _idle.Set();
            }
        }
    }

    void Append(string line)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var existing = new FileInfo(_path);
            if (existing.Exists && existing.Length + bytes.Length > _maxBytes)
                File.Move(_path, _path + ".1", overwrite: true);
            using var file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            file.Write(bytes);
        }
        catch
        {
            // This runs on the writer thread, where an escaping exception would end the process.
        }
    }
}
