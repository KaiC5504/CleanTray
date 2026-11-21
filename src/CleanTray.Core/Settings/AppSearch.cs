namespace CleanTray.Core;

/// <summary>The Settings search box and the section for tray apps CleanTray has not seen yet.</summary>
public static class AppSearch
{
    public static bool HasQuery(string? query) => !string.IsNullOrWhiteSpace(query);

    /// <summary>Whether the display name or the exe's file name contains the query. A blank query matches everything.</summary>
    public static bool Matches(string displayName, string exePath, string? query)
    {
        if (!HasQuery(query)) return true;
        var text = query!.Trim();
        return displayName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(exePath).Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A search opens the section when it finds something there; closing it again is left to clearing the search.</summary>
    public static bool OtherExpanded(bool userExpanded, string? query, int matches) =>
        userExpanded || (HasQuery(query) && matches > 0);

    public static string OtherTitle(int count) => $"Other apps Windows knows about ({count})";
}
