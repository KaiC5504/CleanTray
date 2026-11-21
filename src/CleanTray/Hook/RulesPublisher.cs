using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using CleanTray.Core;

namespace CleanTray.Hook;

/// <summary>Writes the rules table the hook DLL reads, under the header's seqlock.</summary>
sealed unsafe class RulesPublisher : IDisposable
{
    const string MappingName = "Local\\CleanTray.Rules.v1";
    const long Capacity = 4L * 1024 * 1024;
    const int SeqOffset = 8;
    const int BodyOffset = 12;

    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _base;
    bool _disposed;

    public RulesPublisher()
    {
        _file = MemoryMappedFile.CreateOrOpen(MappingName, Capacity, MemoryMappedFileAccess.ReadWrite);
        try
        {
            _view = _file.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
    }

    public void Publish(AppSettings s)
    {
        // Settings changes can still arrive while the app shuts down.
        if (_disposed) return;
        var table = RulesTableWriter.Build(s);
        if (table.Length > Capacity)
        {
            Log.Write($"rules: table of {table.Length} bytes does not fit the mapping, not published");
            return;
        }

        ref uint seq = ref Unsafe.AsRef<uint>(_base + SeqOffset);
        var current = Volatile.Read(ref seq);
        // An odd value means a previous writer died mid-publish; carry on from the next even one.
        var start = (current & 1) != 0 ? current : current + 1;

        Volatile.Write(ref seq, start);
        Interlocked.MemoryBarrier();
        fixed (byte* src = table)
        {
            Buffer.MemoryCopy(src, _base, SeqOffset, SeqOffset);
            Buffer.MemoryCopy(src + BodyOffset, _base + BodyOffset, Capacity - BodyOffset, table.Length - BodyOffset);
        }
        Interlocked.MemoryBarrier();
        Volatile.Write(ref seq, start + 1);
        Log.Write($"rules: published {table.Length} bytes, seq {start + 1}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
