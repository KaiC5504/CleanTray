using CleanTray.Core;

namespace CleanTray.UI;

/// <summary>What the settings window needs from the app.</summary>
sealed class SettingsHost
{
    public required AppSettings Settings { get; init; }
    public required IconRegistry Registry { get; init; }
    public required bool CanSave { get; init; }

    /// <summary>CleanTray's own app key, never listed. Null if the hook DLL could not compute it.</summary>
    public required string? OwnKey { get; init; }

    /// <summary>Tray apps Windows remembers, read when the window opens.</summary>
    public required Func<IReadOnlyList<KnownApp>> KnownApps { get; init; }

    /// <summary>Saves, republishes the rules and tells the DLL.</summary>
    public required Func<SaveOutcome> Commit { get; init; }

    public required Func<bool> HotkeyRegistered { get; init; }
    public required Func<string, bool> RegisterHotkey { get; init; }
    public required Action<bool> ShowMasterIcon { get; init; }
    public required Action ApplyAutostart { get; init; }
}
