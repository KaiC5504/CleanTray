namespace CleanTray.Core;

public record TrayIconInfo(
    ulong Id, uint Pid, string AppKey, string IconPart, string ExePath, string Tip,
    bool Captured, bool Hidden, int Width, int Height, byte[] Bgra)
{
    /// <summary>The same appKey#iconPart identity an icon's rule uses.</summary>
    public string Key => IconKey.Of(AppKey, IconPart);
}
