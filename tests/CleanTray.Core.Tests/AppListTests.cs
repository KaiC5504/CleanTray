namespace CleanTray.Core.Tests;

public sealed class AppListTests
{
    const string Own = @"c:\apps\cleantray\cleantray.exe";

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
    public void Build_HidesCleanTrayByFileNameWhenItsOwnKeyIsUnknown()
    {
        const string dev = @"c:\src\cleantray\bin\cleantray.exe";
        var s = new AppSettings { Apps = [Rule(dev, "CleanTray"), Rule("a", "A")] };
        var list = AppList.Build(s, [Known(Own, "CleanTray"), Known("b", "B")], null);

        Assert.Equal(["a", "b"], list.Select(e => e.Rule.Key));
        var empty = new AppSettings();
        AppList.Adopt(empty, Rule(dev, "CleanTray"), null);
        Assert.Empty(empty.Apps);
    }

    [Fact]
    public void Build_NeverListsWindowsExplorer()
    {
        const string explorer = @"c:\windows\explorer.exe";
        var s = new AppSettings { Apps = [Rule(explorer, "Windows Explorer"), Rule("a", "A")] };
        var list = AppList.Build(s, [Known(explorer, "Windows Explorer"), Known(@"d:\x\Explorer.EXE", "Other"), Known("b", "B")], Own);

        Assert.Equal(["a", "b"], list.Select(e => e.Rule.Key));
    }

    [Fact]
    public void Build_ExplorerLookalikesStayListed()
    {
        var s = new AppSettings { Apps = [Rule(@"c:\x\explorer++.exe", "Explorer++"), Rule(@"c:\x\myexplorer.exe", "My")] };
        Assert.Equal(2, AppList.Build(s, [], Own).Count);
    }

    [Fact]
    public void Adopt_RefusesWindowsExplorer()
    {
        var s = new AppSettings();
        var draft = Rule(@"c:\windows\explorer.exe", "Windows Explorer");
        Assert.Same(draft, AppList.Adopt(s, draft, Own));
        Assert.Empty(s.Apps);
    }

    [Theory]
    [InlineData(@"c:\apps\cleantray\cleantray.exe", @"c:\apps\cleantray\cleantray.exe", true)]
    [InlineData(@"c:\dev\CleanTray.EXE", @"c:\apps\cleantray\cleantray.exe", true)]
    [InlineData(@"c:\dev\cleantray.exe", null, true)]
    [InlineData(@"c:\dev\notcleantray.exe", null, false)]
    [InlineData(@"c:\dev\cleantray.exe.bak", @"c:\apps\cleantray\cleantray.exe", false)]
    [InlineData(@"c:\dev\renamed.exe", @"c:\dev\renamed.exe", true)]
    [InlineData(@"c:\other\renamed.exe", @"c:\dev\renamed.exe", true)]
    [InlineData(@"c:\dev\other.exe", @"c:\dev\renamed.exe", false)]
    public void IsCleanTray_ByKeyOrFileName(string key, string? ownKey, bool expected)
    {
        Assert.Equal(expected, AppList.IsCleanTray(key, ownKey));
        Assert.Equal(expected, AppList.IsExcluded(key, ownKey));
    }

    [Theory]
    [InlineData(@"c:\windows\explorer.exe", true)]
    [InlineData(@"C:\WINDOWS\Explorer.EXE", true)]
    [InlineData(@"c:\windows\explorer.exe.mui", false)]
    [InlineData(@"c:\x\explorer++.exe", false)]
    public void IsExcluded_CoversWindowsExplorer(string key, bool expected) =>
        Assert.Equal(expected, AppList.IsExcluded(key, Own));

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
    public void Build_OrdersByTheSameComparisonTheRowsUseWhenInserted()
    {
        var s = new AppSettings { Apps = [Rule("s2", "beta"), Rule("s1", "Alpha"), Rule("s3", "ALPHA")] };
        var list = AppList.Build(s, [Known("k2", "zeta"), Known("k1", "Delta"), Known("k3", "DELTA")], Own);

        for (var i = 1; i < list.Count; i++)
        {
            var (a, b) = (list[i - 1], list[i]);
            Assert.True(AppList.Compare(a.Group, AppList.Name(a.Rule), b.Group, AppList.Name(b.Rule)) <= 0);
        }
        Assert.Equal(["s1", "s3", "s2", "k1", "k3", "k2"], list.Select(e => e.Rule.Key));
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
