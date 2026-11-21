namespace CleanTray.Core;

public static class PanelOrder
{
    /// <summary>Saved-order icons first, then the rest by Id, which is arrival order. Keys with no icon are ignored.</summary>
    public static IReadOnlyList<TrayIconInfo> Arrange(IReadOnlyList<TrayIconInfo> icons, IReadOnlyList<string> savedOrder)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < savedOrder.Count; i++) rank.TryAdd(savedOrder[i], i);

        return icons
            .OrderBy(i => rank.GetValueOrDefault(i.Key, int.MaxValue))
            .ThenBy(i => i.Id)
            .ToList();
    }

    /// <summary>
    /// The order to save once the visible icons are arranged as <paramref name="visibleKeys"/>. Saved keys that
    /// are not visible (their app is closed) keep their place, right after the key that preceded them before,
    /// or first when nothing did, so the icon is back in its spot when the app returns.
    /// </summary>
    public static IReadOnlyList<string> Commit(IReadOnlyList<string> savedOrder, IReadOnlyList<string> visibleKeys)
    {
        var result = visibleKeys.Distinct(StringComparer.Ordinal).ToList();
        var placed = result.ToHashSet(StringComparer.Ordinal);
        var before = savedOrder.Distinct(StringComparer.Ordinal).ToList();

        for (var i = 0; i < before.Count; i++)
        {
            if (!placed.Add(before[i])) continue;
            // before[i - 1] is in the result by now: it was visible, or it was absent and inserted one step ago.
            result.Insert(i == 0 ? 0 : result.IndexOf(before[i - 1]) + 1, before[i]);
        }
        return result;
    }
}
