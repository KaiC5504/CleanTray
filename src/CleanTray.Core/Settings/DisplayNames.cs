using System.Diagnostics;

namespace CleanTray.Core;

public static class DisplayNames
{
    public static string Choose(string? fileDescription, string exePath)
    {
        var description = fileDescription?.Trim();
        return string.IsNullOrEmpty(description) ? Path.GetFileNameWithoutExtension(exePath) : description;
    }

    /// <summary>The exe's FileDescription, else its file name. Never throws.</summary>
    public static string For(string exePath) => Choose(Description(exePath), exePath);

    /// <summary>The exe's FileDescription, or null. Never throws.</summary>
    public static string? Description(string exePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exePath).FileDescription;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
