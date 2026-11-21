namespace CleanTray.Core;

public abstract record HookEvent;

public sealed record HelloEvent(uint ExplorerPid, uint ProtocolVersion) : HookEvent;

public sealed record IconUpsertEvent(
    ulong IconId, uint Pid, uint Hwnd, uint Uid, bool HasGuid, Guid Guid,
    uint CallbackMsg, uint Version, uint State, bool Captured,
    string AppKey, string ExePath, string Tip,
    int Width, int Height, byte[] Bgra) : HookEvent;

public sealed record IconRemovedEvent(ulong IconId) : HookEvent;

public sealed record PopupEvent(ulong IconId, uint InfoFlags, string Title, string Text) : HookEvent;
