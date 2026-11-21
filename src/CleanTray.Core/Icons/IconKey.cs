namespace CleanTray.Core;

/// <summary>The appKey#iconPart identity shared by icon rules and the panel order.</summary>
public static class IconKey
{
    // Must match the DLL's IconPart, since icon rules are keyed by appKey#iconPart.
    public static string Part(IconUpsertEvent e) =>
        e.HasGuid ? e.Guid.ToString("B").ToLowerInvariant() : "uid:" + e.Uid;

    public static string Of(string appKey, string iconPart) =>
        appKey.ToLowerInvariant() + "#" + iconPart.ToLowerInvariant();
}
