namespace CleanTray.Core;

public static class Frames
{
    // u32 payloadLength (excluding the header) + u16 type.
    public const int HeaderSize = 6;

    // Payload length cap; anything larger means the stream is corrupt.
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public const ushort Hello = 1;
    public const ushort IconUpsert = 2;
    public const ushort IconRemoved = 3;
    public const ushort Popup = 4;

    public const ushort RulesChanged = 101;
    public const ushort Click = 102;
    public const ushort PopupResult = 103;
    public const ushort SetAnchor = 104;
    public const ushort ReleaseAllAndIdle = 105;
}
