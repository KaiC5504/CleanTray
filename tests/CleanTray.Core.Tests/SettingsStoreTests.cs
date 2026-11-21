namespace CleanTray.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "CleanTrayTests-" + Guid.NewGuid().ToString("N"));
    string SettingsPath => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SettingsStore_RoundTrips()
    {
        var s = new AppSettings { Hotkey = "Ctrl+Shift+Q", CaptureNewByDefault = true, ShowMasterIcon = false };
        var seen = new DateTime(2026, 11, 21, 1, 2, 3, DateTimeKind.Utc);
        s.Apps.Add(new AppRule
        {
            Key = "c:\\a.exe", DisplayName = "A", ExePath = "C:\\A.exe", LastSeenUtc = seen,
            Mode = RuleMode.Captured, Popups = false,
            Icons = { new IconRule { IconPart = "uid:7", Tip = "tip", Mode = RuleMode.Show, Popups = null } },
        });

        SettingsStore.Save(SettingsPath, s);
        var back = SettingsStore.Load(SettingsPath);

        Assert.Equal("Ctrl+Shift+Q", back.Hotkey);
        Assert.True(back.CaptureNewByDefault);
        Assert.False(back.ShowMasterIcon);
        var app = Assert.Single(back.Apps);
        Assert.Equal("c:\\a.exe", app.Key);
        Assert.Equal(seen, app.LastSeenUtc);
        Assert.Equal(RuleMode.Captured, app.Mode);
        Assert.False(app.Popups);
        var icon = Assert.Single(app.Icons);
        Assert.Equal(RuleMode.Show, icon.Mode);
        Assert.Null(icon.Popups);
        Assert.Contains("\"Captured\"", File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void SettingsStore_SaveCreatesMissingDirectoryAndOverwrites()
    {
        var p = Path.Combine(_dir, "sub", "settings.json");
        SettingsStore.Save(p, new AppSettings { Hotkey = "Ctrl+1" });
        SettingsStore.Save(p, new AppSettings { Hotkey = "Ctrl+2" });
        Assert.Equal("Ctrl+2", SettingsStore.Load(p).Hotkey);
    }

    [Fact]
    public void SettingsStore_MissingFileGivesDefaults()
    {
        var s = SettingsStore.Load(SettingsPath);
        Assert.Equal("Ctrl+Alt+T", s.Hotkey);
        Assert.True(s.ShowMasterIcon);
        Assert.False(s.CaptureNewByDefault);
        Assert.True(s.StartWithWindows);
        Assert.Empty(s.Apps);
    }

    [Fact]
    public void SettingsStore_CorruptFileFallsBackAndBacksUp()
    {
        File.WriteAllText(SettingsPath, "{not json");
        var s = SettingsStore.Load(SettingsPath);
        Assert.Equal("Ctrl+Alt+T", s.Hotkey);
        Assert.True(File.Exists(SettingsPath + ".bad"));
        Assert.Equal("{not json", File.ReadAllText(SettingsPath + ".bad"));
    }

    [Fact]
    public void SettingsStore_NullCollectionsNormalized()
    {
        File.WriteAllText(SettingsPath, """
            {
              "Hotkey": null,
              "Apps": [
                { "Key": "a.exe", "Icons": null },
                { "Key": null },
                { "Key": "" },
                { "Key": "b.exe", "Icons": [ { "IconPart": null, "Mode": "Show" }, { "IconPart": "uid:1" } ] }
              ]
            }
            """);
        var s = SettingsStore.Load(SettingsPath);

        Assert.Equal("Ctrl+Alt+T", s.Hotkey);
        Assert.Equal(new[] { "a.exe", "b.exe" }, s.Apps.Select(a => a.Key).ToArray());
        Assert.Empty(s.Apps[0].Icons);
        Assert.Equal("uid:1", Assert.Single(s.Apps[1].Icons).IconPart);

        File.WriteAllText(SettingsPath, "{ \"Apps\": null }");
        Assert.Empty(SettingsStore.Load(SettingsPath).Apps);
    }

    [Fact]
    public void SettingsStore_OutOfRangeModeClamped()
    {
        File.WriteAllText(SettingsPath, """
            { "Apps": [ { "Key": "a.exe", "Mode": 5, "Icons": [ { "IconPart": "uid:1", "Mode": 9 } ] } ] }
            """);
        var app = Assert.Single(SettingsStore.Load(SettingsPath).Apps);
        Assert.Equal(RuleMode.Show, app.Mode);
        Assert.Null(app.Icons[0].Mode);
    }

    [Fact]
    public void SettingsStore_LockedFileGivesDefaults()
    {
        File.WriteAllText(SettingsPath, "{}");
        using var hold = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var s = SettingsStore.Load(SettingsPath);
        Assert.Equal("Ctrl+Alt+T", s.Hotkey);
        Assert.False(File.Exists(SettingsPath + ".bad"));
    }

    [Fact]
    public void SettingsStore_UnknownModeNameClamped()
    {
        File.WriteAllText(SettingsPath, """
            { "Apps": [
              { "Key": "a.exe", "Mode": "Bogus", "Icons": [ { "IconPart": "uid:1", "Mode": "Nope" } ] },
              { "Key": "b.exe", "Mode": "Captured", "Icons": [ { "IconPart": "uid:2", "Mode": "Show" } ] }
            ] }
            """);
        var r = SettingsStore.LoadWithStatus(SettingsPath);

        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.False(File.Exists(SettingsPath + ".bad"));
        Assert.Equal(RuleMode.Show, r.Settings.Apps[0].Mode);
        Assert.Null(r.Settings.Apps[0].Icons[0].Mode);
        Assert.Equal(RuleMode.Captured, r.Settings.Apps[1].Mode);
        Assert.Equal(RuleMode.Show, r.Settings.Apps[1].Icons[0].Mode);
    }

    [Fact]
    public void SettingsStore_EmptyIconPartDropped()
    {
        File.WriteAllText(SettingsPath, """
            { "Apps": [ { "Key": "a.exe", "Icons": [ { "IconPart": "" }, { "IconPart": "uid:1" } ] } ] }
            """);
        Assert.Equal("uid:1", Assert.Single(SettingsStore.Load(SettingsPath).Apps[0].Icons).IconPart);
    }

    [Fact]
    public void SettingsStore_PanelOrderRoundTrips()
    {
        var s = new AppSettings { PanelOrder = { "c:\\a.exe#uid:2", "c:\\b.exe#{3b3905d1-afd0-ca7e-3ca2-c9da5791fd04}" } };

        SettingsStore.Save(SettingsPath, s);

        Assert.Equal(s.PanelOrder, SettingsStore.Load(SettingsPath).PanelOrder);
    }

    [Fact]
    public void SettingsStore_FileWithoutPanelOrderLoadsEmpty()
    {
        File.WriteAllText(SettingsPath, """
            { "Version": 1, "Hotkey": "Ctrl+1", "Apps": [ { "Key": "a.exe" } ] }
            """);
        var r = SettingsStore.LoadWithStatus(SettingsPath);

        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal("Ctrl+1", r.Settings.Hotkey);
        Assert.Empty(r.Settings.PanelOrder);
    }

    [Fact]
    public void SettingsStore_PanelOrderNormalized()
    {
        File.WriteAllText(SettingsPath, """{ "PanelOrder": null }""");
        Assert.Empty(SettingsStore.Load(SettingsPath).PanelOrder);

        File.WriteAllText(SettingsPath, """{ "PanelOrder": [ "a#uid:1", null, "", "b#uid:1", "a#uid:1" ] }""");
        Assert.Equal(new[] { "a#uid:1", "b#uid:1" }, SettingsStore.Load(SettingsPath).PanelOrder);
    }

    [Fact]
    public void SettingsStore_LoadWithStatusReportsEachOutcome()
    {
        Assert.Equal(SettingsLoadStatus.Missing, SettingsStore.LoadWithStatus(SettingsPath).Status);

        SettingsStore.Save(SettingsPath, new AppSettings { Hotkey = "Ctrl+1" });
        var ok = SettingsStore.LoadWithStatus(SettingsPath);
        Assert.Equal(SettingsLoadStatus.Loaded, ok.Status);
        Assert.Equal("Ctrl+1", ok.Settings.Hotkey);

        File.WriteAllText(SettingsPath, "{not json");
        Assert.Equal(SettingsLoadStatus.CorruptBackedUp, SettingsStore.LoadWithStatus(SettingsPath).Status);

        File.WriteAllText(SettingsPath, "{}");
        using var hold = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var locked = SettingsStore.LoadWithStatus(SettingsPath);
        Assert.Equal(SettingsLoadStatus.Unreadable, locked.Status);
        Assert.Equal("Ctrl+Alt+T", locked.Settings.Hotkey);
    }
}
