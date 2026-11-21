namespace CleanTray.Core.Tests;

public sealed class SettingsPersisterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "CleanTrayTests-" + Guid.NewGuid().ToString("N"));
    string SettingsPath => Path.Combine(_dir, "settings.json");

    public SettingsPersisterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(SettingsLoadStatus.Loaded)]
    [InlineData(SettingsLoadStatus.Missing)]
    [InlineData(SettingsLoadStatus.CorruptBackedUp)]
    public void Save_WritesWhenTheFileWasLoadedOrIsReplaceable(SettingsLoadStatus status)
    {
        var persister = new SettingsPersister(SettingsPath, status);

        Assert.Equal(SaveOutcome.Saved, persister.Save(new AppSettings { Hotkey = "Ctrl+9" }));
        Assert.Equal("Ctrl+9", SettingsStore.Load(SettingsPath).Hotkey);
    }

    [Fact]
    public void Save_NeverWritesAfterAnUnreadableLoad()
    {
        File.WriteAllText(SettingsPath, "real user data");
        var persister = new SettingsPersister(SettingsPath, SettingsLoadStatus.Unreadable);

        Assert.False(persister.CanSave);
        Assert.Equal(SaveOutcome.Blocked, persister.Save(new AppSettings()));
        Assert.Equal("real user data", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Save_ReportsFailureInsteadOfThrowing()
    {
        // A directory where the file should be makes the final move fail.
        Directory.CreateDirectory(SettingsPath);
        var persister = new SettingsPersister(SettingsPath, SettingsLoadStatus.Missing);

        Assert.Equal(SaveOutcome.Failed, persister.Save(new AppSettings()));
    }
}
