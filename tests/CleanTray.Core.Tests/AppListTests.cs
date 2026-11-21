namespace CleanTray.Core.Tests;

public sealed class AppListTests
{
    const string Own = @"d:\tools\cleantray\cleantray.exe";

    static AppRule Rule(string key, string name, RuleMode mode = RuleMode.Show) =>
        new() { Key = key, DisplayName = name, ExePath = key, Mode = mode };

    static KnownApp Known(string key, string name, byte[]? png = null) => new(key, key, name, png);

    [Fact]
    public void Build_SeenAppsFirstThenKnownOnlyEachAlphabetical()
    {
        var s = new AppSettings { Apps = [Rule("z", "Zed"), Rule("a", "alpha")] };
        var list = AppList.Build(s, [Known("k2", "Mango"), Known("k1", "banana")], Own);

        Assert.Equal(["alpha", "Zed", "banana", "Mango"], list.Select(e => e.Rule.DisplayName));
        Assert.Equal([AppListGroup.Seen, AppListGroup.Seen, AppListGroup.KnownOnly, AppListGroup.KnownOnly],
            list.Select(e => e.Group));
    }

    [Fact]
    public void Build_SeenWinsOverKnownWithTheSameKey()
    {
        var seen = Rule("k", "From settings", RuleMode.Captured);
        var png = new byte[] { 1 };
        var s = new AppSettings { Apps = [seen] };

        var entry = Assert.Single(AppList.Build(s, [Known("k", "From registry", png)], Own));

        Assert.Same(seen, entry.Rule);
        Assert.Equal(AppListGroup.Seen, entry.Group);
        Assert.Same(png, entry.IconPng);
    }

    [Fact]
    public void Build_NeverListsCleanTrayItself()
    {
        var s = new AppSettings { Apps = [Rule(Own, "CleanTray"), Rule("a", "A")] };
        var list = AppList.Build(s, [Known(Own, "CleanTray"), Known("b", "B")], Own);

        Assert.Equal(["a", "b"], list.Select(e => e.Rule.Key));
    }

    [Fact]
    public void Build_HidesOtherCopiesOfCleanTrayToo()
    {
        const string dev = @"c:\src\cleantray\src\cleantray\bin\release\cleantray.exe";
        var s = new AppSettings { Apps = [Rule(dev, "CleanTray"), Rule(@"c:\x\notcleantray.exe", "Other")] };
        var list = AppList.Build(s, [Known(dev, "CleanTray")], Own);

        Assert.Equal([@"c:\x\notcleantray.exe"], list.Select(e => e.Rule.Key));
        var empty = new AppSettings();
        AppList.Adopt(empty, Rule(dev, "CleanTray"), Own);
        Assert.Empty(empty.Apps);
    }

    [Fact]
    public void Build_KnownAppsBecomeShowDraftsOutsideTheSettings()
    {
        var s = new AppSettings { CaptureNewByDefault = true };
        var entry = Assert.Single(AppList.Build(s, [Known("k", "Name")], Own));

        Assert.Equal(RuleMode.Show, entry.Rule.Mode);
        Assert.True(entry.Rule.Popups);
        Assert.Equal("Name", entry.Rule.DisplayName);
        Assert.Equal("k", entry.Rule.ExePath);
        Assert.Equal(default, entry.Rule.LastSeenUtc);
        Assert.Empty(s.Apps);
    }

    [Fact]
    public void Build_NameFallsBackToTheFileName()
    {
        var s = new AppSettings { Apps = [new AppRule { Key = "b", ExePath = @"C:\x\Beta.exe" }, Rule("a", "Alpha")] };
        Assert.Equal(["a", "b"], AppList.Build(s, [], Own).Select(e => e.Rule.Key));
    }

    [Fact]
    public void Compare_GroupBeforeName()
    {
        Assert.True(AppList.Compare(AppListGroup.Seen, "Zed", AppListGroup.KnownOnly, "alpha") < 0);
        Assert.True(AppList.Compare(AppListGroup.Seen, "alpha", AppListGroup.Seen, "Zed") < 0);
        Assert.Equal(0, AppList.Compare(AppListGroup.KnownOnly, "abc", AppListGroup.KnownOnly, "ABC"));
    }

    [Fact]
    public void Adopt_AddsTheDraftOnce()
    {
        var s = new AppSettings();
        var draft = AppList.Build(s, [Known("k", "Name")], Own)[0].Rule;

        Assert.Same(draft, AppList.Adopt(s, draft, Own));
        Assert.Same(draft, AppList.Adopt(s, draft, Own));
        Assert.Same(draft, Assert.Single(s.Apps));
    }

    [Fact]
    public void Adopt_UsesTheSettingsRuleIfTheAppWasSeenMeanwhile()
    {
        var s = new AppSettings();
        var draft = AppList.Build(s, [Known("k", "Name")], Own)[0].Rule;
        var seen = Rule("k", "Seen");
        s.Apps.Add(seen);

        Assert.Same(seen, AppList.Adopt(s, draft, Own));
        Assert.Same(seen, Assert.Single(s.Apps));
    }

    [Fact]
    public void Adopt_RefusesCleanTraysOwnKey()
    {
        var s = new AppSettings();
        var draft = Rule(Own, "CleanTray");
        Assert.Same(draft, AppList.Adopt(s, draft, Own));
        Assert.Empty(s.Apps);
    }
}
