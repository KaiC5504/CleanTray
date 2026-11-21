using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanTray.Core;

public static class SettingsStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new LenientRuleModeConverter() },
    };

    /// <summary>Never throws: unreadable or invalid files yield defaults.</summary>
    public static AppSettings Load(string path) => LoadWithStatus(path).Settings;

    /// <summary>
    /// Like Load, but reports why defaults were returned. Callers must not Save after
    /// <see cref="SettingsLoadStatus.Unreadable"/>, or the user's real settings get overwritten.
    /// </summary>
    public static SettingsLoadResult LoadWithStatus(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return new(new AppSettings(), SettingsLoadStatus.Missing);
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(new AppSettings(), SettingsLoadStatus.Unreadable);
        }

        AppSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AppSettings>(text, Options);
        }
        catch (JsonException)
        {
            parsed = null;
        }
        if (parsed is null)
        {
            Backup(path);
            return new(new AppSettings(), SettingsLoadStatus.CorruptBackedUp);
        }
        return new(Normalize(parsed), SettingsLoadStatus.Loaded);
    }

    public static void Save(string path, AppSettings s)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write-then-move so a crash mid-save never leaves a truncated settings file.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
        File.Move(tmp, path, overwrite: true);
    }

    static void Backup(string path)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Hand-edited or truncated JSON can leave nulls and out-of-range enums that would crash later consumers.
    static AppSettings Normalize(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.Hotkey)) s.Hotkey = new AppSettings().Hotkey;
        s.Apps = (s.Apps ?? new()).Where(a => a is not null && !string.IsNullOrEmpty(a.Key)).ToList();
        s.PanelOrder = (s.PanelOrder ?? new()).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var app in s.Apps)
        {
            app.DisplayName ??= "";
            app.ExePath ??= "";
            if (!Enum.IsDefined(app.Mode)) app.Mode = RuleMode.Show;
            app.Icons = (app.Icons ?? new()).Where(i => i is not null && !string.IsNullOrEmpty(i.IconPart)).ToList();
            foreach (var icon in app.Icons)
            {
                icon.Tip ??= "";
                if (icon.Mode is { } m && !Enum.IsDefined(m)) icon.Mode = null;
            }
        }
        return s;
    }
}

public enum SettingsLoadStatus
{
    Loaded,
    Missing,
    CorruptBackedUp,
    Unreadable,
}

public record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status);

// Unknown names or wrong token types become an undefined value so Normalize can clamp them
// without one bad field throwing away the whole file.
sealed class LenientRuleModeConverter : JsonConverter<RuleMode>
{
    const RuleMode Invalid = (RuleMode)(-1);

    public override RuleMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<RuleMode>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named)
                    ? named
                    : Invalid;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var n) ? (RuleMode)n : Invalid;
            default:
                reader.Skip();
                return Invalid;
        }
    }

    public override void Write(Utf8JsonWriter writer, RuleMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
