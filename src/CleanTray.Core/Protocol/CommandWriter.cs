using System.Buffers.Binary;

namespace CleanTray.Core;

public enum ClickKind : byte
{
    Left = 1,
    Double = 2,
    Right = 3,
}

public enum PopupOutcome : byte
{
    Clicked = 1,
    TimedOut = 2,
}

public static class CommandWriter
{
    public static byte[] RulesChanged() => Frame(Frames.RulesChanged, 0);

    public static byte[] Click(ulong id, ClickKind k, int x, int y)
    {
        var b = Frame(Frames.Click, 17);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(6), id);
        b[14] = (byte)k;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(15), x);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(19), y);
        return b;
    }

    public static byte[] PopupResult(ulong id, PopupOutcome o)
    {
        var b = Frame(Frames.PopupResult, 9);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(6), id);
        b[14] = (byte)o;
        return b;
    }

    public static byte[] SetAnchor(int l, int t, int r, int b)
    {
        var f = Frame(Frames.SetAnchor, 16);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(6), l);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(10), t);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(14), r);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(18), b);
        return f;
    }

    public static byte[] ReleaseAllAndIdle() => Frame(Frames.ReleaseAllAndIdle, 0);

    static byte[] Frame(ushort type, int payloadLength)
    {
        var b = new byte[Frames.HeaderSize + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), type);
        return b;
    }
}
