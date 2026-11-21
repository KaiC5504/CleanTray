using System.Buffers.Binary;
using System.Text;

namespace CleanTray.Core;

public static class EventReader
{
    /// <summary>
    /// Reads one frame. Returns false with consumed=0 while the frame is incomplete. A complete
    /// frame of unknown type or with a malformed payload is consumed and yields ev=null.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> buf, out HookEvent? ev, out int consumed) =>
        TryRead(buf, out ev, out consumed, out _);

    /// <summary>
    /// As above, but sets corrupt when the header declares a payload over the frame cap. The
    /// stream cannot be resynchronised after that, so the caller should disconnect.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> buf, out HookEvent? ev, out int consumed, out bool corrupt)
    {
        ev = null;
        consumed = 0;
        corrupt = false;
        if (buf.Length < Frames.HeaderSize) return false;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        if (length > Frames.MaxFrameBytes)
        {
            corrupt = true;
            return false;
        }
        var total = Frames.HeaderSize + (long)length;
        if (buf.Length < total) return false;

        var type = BinaryPrimitives.ReadUInt16LittleEndian(buf[4..]);
        var reader = new Reader(buf.Slice(Frames.HeaderSize, (int)length));
        ev = type switch
        {
            Frames.Hello => new HelloEvent(reader.U32(), reader.U32()),
            Frames.IconUpsert => ReadIconUpsert(ref reader),
            Frames.IconRemoved => new IconRemovedEvent(reader.U64()),
            Frames.Popup => new PopupEvent(reader.U64(), reader.U32(), reader.Str(), reader.Str()),
            _ => null,
        };
        if (!reader.Ok) ev = null;
        consumed = (int)total;
        return true;
    }

    static IconUpsertEvent ReadIconUpsert(ref Reader r)
    {
        var iconId = r.U64();
        var pid = r.U32();
        var hwnd = r.U32();
        var uid = r.U32();
        var hasGuid = r.U8() != 0;
        var guid = r.Guid();
        var callbackMsg = r.U32();
        var version = r.U32();
        var state = r.U32();
        var captured = r.U8() != 0;
        var appKey = r.Str();
        var exePath = r.Str();
        var tip = r.Str();
        int w = r.U16(), h = r.U16();
        // w*h*4 can reach 16 GiB, so a wrapped int would pass the bounds check with a tiny length.
        var pixelBytes = (long)w * h * 4;
        var bgra = r.Bytes(pixelBytes > int.MaxValue ? -1 : (int)pixelBytes);
        return new IconUpsertEvent(iconId, pid, hwnd, uid, hasGuid, guid, callbackMsg, version, state,
            captured, appKey, exePath, tip, w, h, bgra);
    }

    // Reads past the end flip Ok and return zero values, so one check after parsing covers every field.
    ref struct Reader
    {
        ReadOnlySpan<byte> _b;
        public bool Ok;

        public Reader(ReadOnlySpan<byte> b)
        {
            _b = b;
            Ok = true;
        }

        ReadOnlySpan<byte> Take(int n)
        {
            if (!Ok || n < 0 || _b.Length < n)
            {
                Ok = false;
                return default;
            }
            var s = _b[..n];
            _b = _b[n..];
            return s;
        }

        public byte U8() => Take(1) is { Length: 1 } s ? s[0] : (byte)0;
        public ushort U16() => Take(2) is { Length: 2 } s ? BinaryPrimitives.ReadUInt16LittleEndian(s) : (ushort)0;
        public uint U32() => Take(4) is { Length: 4 } s ? BinaryPrimitives.ReadUInt32LittleEndian(s) : 0;
        public ulong U64() => Take(8) is { Length: 8 } s ? BinaryPrimitives.ReadUInt64LittleEndian(s) : 0;
        public Guid Guid() => Take(16) is { Length: 16 } s ? new Guid(s) : default;
        public byte[] Bytes(int n) => Take(n).ToArray();

        public string Str()
        {
            var units = U16();
            var s = Take(units * 2);
            return s.IsEmpty ? "" : Encoding.Unicode.GetString(s);
        }
    }
}
