namespace CleanTray.Core;

public record TrayIconInfo(
    ulong Id, uint Pid, string AppKey, string ExePath, string Tip,
    bool Captured, bool Hidden, int Width, int Height, byte[] Bgra);
