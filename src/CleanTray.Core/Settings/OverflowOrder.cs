using System.Buffers.Binary;

namespace CleanTray.Core;

/// <summary>One HKCU\Control Panel\NotifyIconSettings subkey: its id and the icon it stands for.</summary>
public sealed record NotifyIconEntry(ulong Id, string? ExePath, uint? Uid, bool HasGuid);

/// <summary>
/// Windows' saved order of the hidden-icons flyout, NotifyIconSettings\UIOrderList: the subkeys'
/// 64-bit ids, front first. Explorer puts every icon it has not seen before at the front and only
/// reads the list when it starts.
/// </summary>
public static class OverflowOrder
{
    /// <summary>The list with <paramref name="id"/> first, or null when it already is or the data is not a list of ids.</summary>
    public static byte[]? MoveToFront(byte[] list, ulong id)
    {
        if (list.Length == 0 || list.Length % 8 != 0) return null;
        if (BinaryPrimitives.ReadUInt64LittleEndian(list) == id) return null;

        var result = new byte[list.Length + 8];
        BinaryPrimitives.WriteUInt64LittleEndian(result, id);
        var length = 8;
        for (var i = 0; i < list.Length; i += 8)
        {
            var span = list.AsSpan(i, 8);
            if (BinaryPrimitives.ReadUInt64LittleEndian(span) == id) continue;
            span.CopyTo(result.AsSpan(length));
            length += 8;
        }
        return result[..length];
    }

    /// <summary>The entry for the icon an exe added with this uID and no GUID, matched on the resolved path.</summary>
    public static ulong? FindIcon(IEnumerable<NotifyIconEntry> entries, string exePath, uint uid) =>
        entries.FirstOrDefault(e => !e.HasGuid && e.Uid == uid
            && string.Equals(e.ExePath, exePath, StringComparison.OrdinalIgnoreCase))?.Id;
}
