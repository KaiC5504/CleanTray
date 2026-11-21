namespace CleanTray.Core;

/// <summary>
/// Log lines for tray data. Tooltips, balloon text and full paths can be private, so only ids,
/// flags and the exe file name are ever written.
/// </summary>
public static class LogText
{
    public static string Icon(IconUpsertEvent e) =>
        $"icon {e.IconId}: pid {e.Pid} captured={e.Captured} exe={Path.GetFileName(e.ExePath)}";

    /// <summary>Exception messages often carry paths, so only the type and HRESULT are logged.</summary>
    public static string Error(Exception e) => $"{e.GetType().Name} 0x{e.HResult:X8}";
}
