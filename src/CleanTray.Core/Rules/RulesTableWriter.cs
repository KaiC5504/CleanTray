using System.Buffers.Binary;
using System.Text;

namespace CleanTray.Core;

public static class RulesTableWriter
{
    const uint Magic = 0x4C525443;
    const uint Version = 1;
    const int HeaderSize = 24;

    // Keys come from user-editable JSON; the wire keyLen is only u16 and real keys are paths.
    const int MaxKeyLength = 4096;

    readonly record struct Entry(string Key, RuleMode Mode, bool Popups);

    /// <summary>Builds the shared-memory table with seq 0; the publisher stamps the real seq.</summary>
    public static byte[] Build(AppSettings s)
    {
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(Entry e)
        {
            if (e.Key.Length <= MaxKeyLength && seen.Add(e.Key)) entries.Add(e);
        }

        foreach (var app in s.Apps)
        {
            var appKey = app.Key.ToLowerInvariant();
            Add(new Entry(appKey, app.Mode, app.Popups));
            foreach (var icon in app.Icons)
            {
                if (icon.Mode is null && icon.Popups is null) continue;
                Add(new Entry(
                    appKey + "#" + icon.IconPart.ToLowerInvariant(),
                    icon.Mode ?? app.Mode,
                    icon.Popups ?? app.Popups));
            }
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        var payload = entries.Sum(e => 4 + 2 * e.Key.Length);
        var buf = new byte[HeaderSize + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), s.CaptureNewByDefault ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)payload);

        var pos = HeaderSize;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos), (ushort)e.Key.Length);
            buf[pos + 2] = (byte)e.Mode;
            buf[pos + 3] = e.Popups ? (byte)1 : (byte)0;
            pos += 4;
            pos += Encoding.Unicode.GetBytes(e.Key, buf.AsSpan(pos));
        }
        return buf;
    }
}
