namespace CleanTray.Core;

public static class InstallLocation
{
    /// <summary>Written next to the exe by scripts/install.ps1, so a dev or build-output copy never counts as installed.</summary>
    public const string MarkerFileName = "installed.marker";

    public static bool IsInstalled(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            return dir is not null && File.Exists(Path.Combine(dir, MarkerFileName));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
