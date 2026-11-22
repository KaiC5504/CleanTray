namespace CleanTray.Core;

public enum AppListGroup
{
    Seen,
    KnownOnly,
}

/// <summary>A Settings row. A KnownOnly row's rule is a draft that is not in the settings yet.</summary>
public sealed record AppListEntry(AppListGroup Group, AppRule Rule, byte[]? IconPng);

/// <summary>
/// The Settings app list: every app CleanTray has a rule for, plus the tray apps Windows knows that
/// it has never seen. Opening Settings must not write the file, so known apps only join the
/// settings once the user changes one.
/// </summary>
public static class AppList
{
    const string CleanTrayExe = "CleanTray.exe";

    static readonly Comparer<AppListEntry> EntryOrder =
        Comparer<AppListEntry>.Create((a, b) => Compare(a.Group, Name(a.Rule), b.Group, Name(b.Rule)));

    public static IReadOnlyList<AppListEntry> Build(AppSettings s, IReadOnlyList<KnownApp> known, string? ownKey)
    {
        var png = new Dictionary<string, byte[]?>();
        foreach (var k in known) png.TryAdd(k.Key, k.IconPng);
        var seenKeys = s.Apps.Select(a => a.Key).ToHashSet();

        var seen = s.Apps
            .Where(a => !IsCleanTray(a.Key, ownKey))
            .Select(a => new AppListEntry(AppListGroup.Seen, a, png.GetValueOrDefault(a.Key)));
        var knownOnly = known
            .Where(k => !IsCleanTray(k.Key, ownKey) && seenKeys.Add(k.Key))
            .Select(k => new AppListEntry(AppListGroup.KnownOnly, Draft(k), k.IconPng));

        return seen.Concat(knownOnly).OrderBy(e => e, EntryOrder).ToList();
    }

    public static string Name(AppRule rule) => DisplayNames.Choose(rule.DisplayName, rule.ExePath);

    public static int Compare(AppListGroup groupA, string nameA, AppListGroup groupB, string nameB)
    {
        var byGroup = groupA.CompareTo(groupB);
        return byGroup != 0 ? byGroup : string.Compare(nameA, nameB, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// The rule a change to a known app's draft must land on: the settings' own rule if the app was
    /// seen meanwhile, else the draft, now added. CleanTray itself never gets a rule.
    /// </summary>
    public static AppRule Adopt(AppSettings s, AppRule draft, string? ownKey)
    {
        if (IsCleanTray(draft.Key, ownKey)) return draft;
        var existing = s.Apps.FirstOrDefault(a => a.Key == draft.Key);
        if (existing is not null) return existing;
        s.Apps.Add(draft);
        return draft;
    }

    /// <summary>
    /// CleanTray itself, or another copy of it (a dev build, an older install). Only one copy runs
    /// at a time and the hook always passes the running one's icon through, so a rule would do nothing.
    /// Without its own key (the hook DLL is missing) it is still recognised by file name.
    /// </summary>
    public static bool IsCleanTray(string key, string? ownKey)
    {
        if (key == ownKey) return true;
        var file = Path.GetFileName(key);
        return file.Equals(CleanTrayExe, StringComparison.OrdinalIgnoreCase)
            || (ownKey is not null && file.Equals(Path.GetFileName(ownKey), StringComparison.OrdinalIgnoreCase));
    }

    static AppRule Draft(KnownApp app) => new()
    {
        Key = app.Key,
        DisplayName = app.DisplayName,
        ExePath = app.ExePath,
        Mode = RuleMode.Show,
    };
}
