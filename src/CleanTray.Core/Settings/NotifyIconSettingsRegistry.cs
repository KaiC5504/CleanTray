using System.Security;
using Microsoft.Win32;

namespace CleanTray.Core;

/// <summary>Reads, never writes, Windows' list of every tray icon it has seen.</summary>
public static class NotifyIconSettingsRegistry
{
    const string KeyPath = @"Control Panel\NotifyIconSettings";
    static readonly string[] Wanted = ["ExecutablePath", "InitialTooltip", "IconSnapshot"];

    /// <summary>One value map per subkey. Unreadable subkeys are skipped; never throws.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Read()
    {
        var entries = new List<IReadOnlyDictionary<string, object?>>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (root is null) return entries;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var sub = root.OpenSubKey(name, writable: false);
                    if (sub is null) continue;
                    var values = new Dictionary<string, object?>();
                    foreach (var value in Wanted) values[value] = sub.GetValue(value);
                    entries.Add(values);
                }
                catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException)
        {
        }
        return entries;
    }
}
