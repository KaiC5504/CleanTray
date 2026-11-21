namespace CleanTray.Core;

public static class AppCatalog
{
    /// <summary>Records a sighting; returns true when a new app or icon rule was added. No copy of CleanTray is recorded.</summary>
    public static bool RecordSeen(AppSettings s, IconUpsertEvent e, DateTime nowUtc, Func<string, string> displayNameFor,
        string? ownKey = null)
    {
        if (AppList.IsCleanTray(e.AppKey, ownKey)) return false;
        var added = false;
        var app = s.Apps.FirstOrDefault(a => a.Key == e.AppKey);
        if (app is null)
        {
            app = new AppRule
            {
                Key = e.AppKey,
                DisplayName = displayNameFor(e.ExePath),
                ExePath = e.ExePath,
                Mode = s.CaptureNewByDefault ? RuleMode.Captured : RuleMode.Show,
            };
            s.Apps.Add(app);
            added = true;
        }
        app.LastSeenUtc = nowUtc;

        var part = IconPart(e);
        var icon = app.Icons.FirstOrDefault(i => i.IconPart == part);
        if (icon is null)
        {
            icon = new IconRule { IconPart = part };
            app.Icons.Add(icon);
            added = true;
        }
        icon.Tip = e.Tip;
        return added;
    }

    // Must match the DLL's IconPart, since icon rules are keyed by appKey#iconPart.
    static string IconPart(IconUpsertEvent e) =>
        e.HasGuid ? e.Guid.ToString("B").ToLowerInvariant() : "uid:" + e.Uid;
}
