namespace CleanTray.Core.Tests;

public class AppCatalogTests
{
    static readonly DateTime Now = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    static IconUpsertEvent Event(uint uid = 7, bool hasGuid = false, Guid guid = default, string tip = "Tip") =>
        new(1, 1000, 0x1234, uid, hasGuid, guid, 0x8001, 4, 0, true,
            "c:\\a\\b.exe", "C:\\A\\b.exe", tip, 0, 0, Array.Empty<byte>());

    [Theory]
    [InlineData(true, RuleMode.Captured)]
    [InlineData(false, RuleMode.Show)]
    public void AppCatalog_NewAppFollowsDefault(bool captureByDefault, RuleMode expected)
    {
        var s = new AppSettings { CaptureNewByDefault = captureByDefault };
        Assert.True(AppCatalog.RecordSeen(s, Event(), Now, _ => "B"));

        var app = Assert.Single(s.Apps);
        Assert.Equal("c:\\a\\b.exe", app.Key);
        Assert.Equal("B", app.DisplayName);
        Assert.Equal("C:\\A\\b.exe", app.ExePath);
        Assert.Equal(expected, app.Mode);
        Assert.Equal(Now, app.LastSeenUtc);
        var icon = Assert.Single(app.Icons);
        Assert.Equal("uid:7", icon.IconPart);
        Assert.Null(icon.Mode);
        Assert.Equal("Tip", icon.Tip);
    }

    [Fact]
    public void AppCatalog_SecondIconAddsIconRule()
    {
        var s = new AppSettings();
        AppCatalog.RecordSeen(s, Event(7), Now, _ => "B");
        Assert.True(AppCatalog.RecordSeen(s, Event(8), Now, _ => "B"));
        Assert.Single(s.Apps);
        Assert.Equal(2, s.Apps[0].Icons.Count);
    }

    [Fact]
    public void AppCatalog_RepeatSightingUpdatesTipAndTimeButAddsNothing()
    {
        var s = new AppSettings();
        AppCatalog.RecordSeen(s, Event(tip: "old"), Now, _ => "B");
        var later = Now.AddHours(1);
        Assert.False(AppCatalog.RecordSeen(s, Event(tip: "new"), later, _ => "B"));
        Assert.Equal(later, s.Apps[0].LastSeenUtc);
        Assert.Equal("new", s.Apps[0].Icons[0].Tip);
    }

    [Fact]
    public void AppCatalog_GuidIconPartUsesBracedLowercase()
    {
        var s = new AppSettings();
        var g = new Guid("3B3905D1-AFD0-CA7E-3CA2-C9DA5791FD04");
        AppCatalog.RecordSeen(s, Event(hasGuid: true, guid: g), Now, _ => "B");
        Assert.Equal("{3b3905d1-afd0-ca7e-3ca2-c9da5791fd04}", s.Apps[0].Icons[0].IconPart);
    }

    [Fact]
    public void AppCatalog_DisplayNameIsOnlyLookedUpForNewApps()
    {
        var s = new AppSettings();
        var calls = 0;
        string Name(string _) { calls++; return "B"; }
        AppCatalog.RecordSeen(s, Event(7), Now, Name);
        AppCatalog.RecordSeen(s, Event(8), Now, Name);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AppCatalog_NeverRecordsCleanTrayItself()
    {
        var s = new AppSettings { CaptureNewByDefault = true };
        Assert.False(AppCatalog.RecordSeen(s, Event(), Now, _ => "CleanTray", ownKey: "c:\\a\\b.exe"));
        Assert.Empty(s.Apps);
    }
}
