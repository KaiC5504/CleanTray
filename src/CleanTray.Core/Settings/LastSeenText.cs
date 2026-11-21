using System.Globalization;

namespace CleanTray.Core;

public static class LastSeenText
{
    public static string Format(DateTime lastSeenUtc, DateTime nowUtc)
    {
        if (lastSeenUtc == default) return "Not seen yet";
        var ago = nowUtc - lastSeenUtc;
        if (ago < TimeSpan.FromMinutes(1)) return "Just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} h ago";
        return lastSeenUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }
}
