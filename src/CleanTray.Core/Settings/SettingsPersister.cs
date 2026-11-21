namespace CleanTray.Core;

public enum SaveOutcome
{
    Saved,
    Blocked,
    Failed,
}

/// <summary>
/// Saves settings, refusing when the startup load could not read the real file: saving defaults
/// over it would destroy the user's rules.
/// </summary>
public sealed class SettingsPersister(string path, SettingsLoadStatus loadStatus)
{
    public bool CanSave => loadStatus != SettingsLoadStatus.Unreadable;

    public SaveOutcome Save(AppSettings settings)
    {
        if (!CanSave) return SaveOutcome.Blocked;
        try
        {
            SettingsStore.Save(path, settings);
            return SaveOutcome.Saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return SaveOutcome.Failed;
        }
    }
}
