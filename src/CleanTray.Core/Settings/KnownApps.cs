namespace CleanTray.Core;

/// <summary>A tray app Windows remembers, which CleanTray may never have seen add an icon.</summary>
public sealed record KnownApp(string Key, string ExePath, string DisplayName, byte[]? IconPng);

/// <summary>What reading Windows' tray list needs from the system, so the mapping can be tested.</summary>
public sealed record KnownAppSystem(
    Func<Guid, string?> KnownFolder,
    Func<string, bool> FileExists,
    Func<string, string?> AppKey,
    Func<string, string?> FileDescription);

public static class KnownFolderPath
{
    /// <summary>
    /// Expands the known-folder GUID Windows puts in front of some paths
    /// ("{6D809377-…}\Vendor\x.exe"). Null when the GUID is malformed or unknown.
    /// </summary>
    public static string? Resolve(string path, Func<Guid, string?> folderPath)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path[0] != '{') return path;
        var close = path.IndexOf('}');
        if (close < 0 || !Guid.TryParseExact(path[..(close + 1)], "B", out var id)) return null;
        var rest = path[(close + 1)..].TrimStart('\\');
        if (rest.Length == 0) return null;
        var folder = folderPath(id);
        return string.IsNullOrEmpty(folder) ? null : Path.Join(folder.TrimEnd('\\'), rest);
    }
}

public static class KnownApps
{
    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Maps HKCU\Control Panel\NotifyIconSettings subkeys (value name to data) to one app per key,
    /// skipping exes that no longer exist. Later entries for a key only fill in a missing tooltip or snapshot.
    /// </summary>
    public static IReadOnlyList<KnownApp> From(IEnumerable<IReadOnlyDictionary<string, object?>> entries, KnownAppSystem system)
    {
        var found = new List<Found>();
        var byKey = new Dictionary<string, Found>();
        foreach (var entry in entries)
        {
            if (entry.GetValueOrDefault("ExecutablePath") is not string raw) continue;
            var path = KnownFolderPath.Resolve(raw, system.KnownFolder);
            if (path is null || !Path.IsPathFullyQualified(path) || !system.FileExists(path)) continue;
            var key = system.AppKey(path);
            if (string.IsNullOrEmpty(key)) continue;

            var tip = FirstLine(entry.GetValueOrDefault("InitialTooltip") as string);
            var png = entry.GetValueOrDefault("IconSnapshot") is byte[] bytes && bytes.AsSpan().StartsWith(PngSignature) ? bytes : null;
            if (byKey.TryGetValue(key, out var known))
            {
                known.Tip ??= tip;
                known.Png ??= png;
                continue;
            }
            known = new Found(key, path) { Tip = tip, Png = png };
            byKey[key] = known;
            found.Add(known);
        }

        return found
            .Select(f => new KnownApp(f.Key, f.Path,
                DisplayNames.Choose(NonBlank(system.FileDescription(f.Path)) ?? f.Tip, f.Path), f.Png))
            .ToList();
    }

    static string? FirstLine(string? text) => NonBlank(text?.Split('\n', 2)[0]);

    static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    sealed class Found(string key, string path)
    {
        public string Key { get; } = key;
        public string Path { get; } = path;
        public string? Tip { get; set; }
        public byte[]? Png { get; set; }
    }
}
