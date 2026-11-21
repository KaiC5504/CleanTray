namespace CleanTray.Core.Tests;

public class PanelOrderTests
{
    static TrayIconInfo Icon(ulong id, string app, string part = "uid:1") =>
        new(id, 1, app, part, "C:\\Apps\\" + app, "tip", true, false, 0, 0, Array.Empty<byte>());

    static string Key(string app, string part = "uid:1") => app + "#" + part;

    static ulong[] Ids(IEnumerable<TrayIconInfo> icons) => icons.Select(i => i.Id).ToArray();

    [Fact]
    public void PanelOrder_ArrangeAllPlacedFollowsSavedOrder()
    {
        var icons = new[] { Icon(1, "a.exe"), Icon(2, "b.exe"), Icon(3, "c.exe") };
        var saved = new[] { Key("c.exe"), Key("a.exe"), Key("b.exe") };

        Assert.Equal(new ulong[] { 3, 1, 2 }, Ids(PanelOrder.Arrange(icons, saved)));
    }

    [Fact]
    public void PanelOrder_ArrangePutsUnplacedAfterPlacedByIdAscending()
    {
        var icons = new[] { Icon(9, "d.exe"), Icon(1, "a.exe"), Icon(4, "c.exe"), Icon(2, "b.exe") };
        var saved = new[] { Key("c.exe"), Key("a.exe") };

        Assert.Equal(new ulong[] { 4, 1, 2, 9 }, Ids(PanelOrder.Arrange(icons, saved)));
    }

    [Fact]
    public void PanelOrder_ArrangeWithEmptySavedOrderIsPureIdOrder()
    {
        var icons = new[] { Icon(7, "a.exe"), Icon(3, "b.exe"), Icon(5, "c.exe") };

        Assert.Equal(new ulong[] { 3, 5, 7 }, Ids(PanelOrder.Arrange(icons, Array.Empty<string>())));
    }

    [Fact]
    public void PanelOrder_ArrangeBreaksTiesOfDuplicateKeysById()
    {
        var icons = new[] { Icon(8, "a.exe"), Icon(2, "b.exe"), Icon(5, "a.exe") };
        var saved = new[] { Key("a.exe"), Key("b.exe") };

        Assert.Equal(new ulong[] { 5, 8, 2 }, Ids(PanelOrder.Arrange(icons, saved)));
    }

    [Fact]
    public void PanelOrder_ArrangeIgnoresKeysOfIconsThatAreNotThere()
    {
        var icons = new[] { Icon(1, "a.exe"), Icon(2, "b.exe") };
        var saved = new[] { Key("gone.exe"), Key("b.exe"), Key("also-gone.exe"), Key("a.exe") };

        Assert.Equal(new ulong[] { 2, 1 }, Ids(PanelOrder.Arrange(icons, saved)));
    }

    [Fact]
    public void PanelOrder_ArrangeUsesTheFirstOccurrenceOfARepeatedSavedKey()
    {
        var icons = new[] { Icon(1, "a.exe"), Icon(2, "b.exe") };
        var saved = new[] { Key("b.exe"), Key("a.exe"), Key("b.exe") };

        Assert.Equal(new ulong[] { 2, 1 }, Ids(PanelOrder.Arrange(icons, saved)));
    }

    [Fact]
    public void PanelOrder_ArrangeKeepsEveryIconAndLeavesTheInputAlone()
    {
        var icons = new[] { Icon(3, "c.exe"), Icon(1, "a.exe") };
        var saved = new[] { Key("c.exe") };

        var arranged = PanelOrder.Arrange(icons, saved);

        Assert.Equal(2, arranged.Count);
        Assert.Equal(new ulong[] { 3, 1 }, Ids(icons));
        Assert.Empty(PanelOrder.Arrange(Array.Empty<TrayIconInfo>(), saved));
    }

    [Fact]
    public void PanelOrder_ArrangeSeparatesIconsOfOneAppByIconPart()
    {
        var icons = new[] { Icon(1, "a.exe", "uid:1"), Icon(2, "a.exe", "uid:2") };
        var saved = new[] { Key("a.exe", "uid:2"), Key("a.exe", "uid:1") };

        Assert.Equal(new ulong[] { 2, 1 }, Ids(PanelOrder.Arrange(icons, saved)));
    }

    [Fact]
    public void PanelOrder_CommitWithEveryKeyVisibleIsTheVisibleOrder()
    {
        var saved = new[] { "a", "b", "c" };
        Assert.Equal(new[] { "c", "a", "b" }, PanelOrder.Commit(saved, new[] { "c", "a", "b" }));
    }

    [Fact]
    public void PanelOrder_CommitFromAnEmptySavedOrderIsTheVisibleOrder()
    {
        Assert.Equal(new[] { "b", "a" }, PanelOrder.Commit(Array.Empty<string>(), new[] { "b", "a" }));
    }

    [Fact]
    public void PanelOrder_CommitKeepsAnAbsentKeyAfterItsPredecessor()
    {
        var saved = new[] { "a", "x", "b" };
        Assert.Equal(new[] { "b", "a", "x" }, PanelOrder.Commit(saved, new[] { "b", "a" }));
    }

    [Fact]
    public void PanelOrder_CommitKeepsAChainOfAbsentKeysTogetherAfterTheSurvivor()
    {
        var saved = new[] { "a", "x", "y", "b" };
        Assert.Equal(new[] { "b", "a", "x", "y" }, PanelOrder.Commit(saved, new[] { "b", "a" }));
    }

    [Fact]
    public void PanelOrder_CommitPutsAnAbsentKeyWithNoPredecessorFirst()
    {
        var saved = new[] { "x", "a" };
        Assert.Equal(new[] { "x", "b", "a" }, PanelOrder.Commit(saved, new[] { "b", "a" }));
    }

    [Fact]
    public void PanelOrder_CommitKeepsAChainOfAbsentKeysAtTheFront()
    {
        var saved = new[] { "x", "y", "a" };
        Assert.Equal(new[] { "x", "y", "b", "a" }, PanelOrder.Commit(saved, new[] { "b", "a" }));
    }

    [Fact]
    public void PanelOrder_CommitKeepsKeysThatWereNeverSaved()
    {
        Assert.Equal(new[] { "b", "a" }, PanelOrder.Commit(new[] { "a" }, new[] { "b", "a" }));
    }

    [Fact]
    public void PanelOrder_CommitResultHasNoDuplicates()
    {
        var saved = new[] { "a", "x", "a", "x", "b" };
        var result = PanelOrder.Commit(saved, new[] { "a", "b", "a" });

        Assert.Equal(new[] { "a", "x", "b" }, result);
        Assert.Equal(result.Count, result.Distinct().Count());
    }

    [Fact]
    public void PanelOrder_CommitLeavesItsInputsAlone()
    {
        var saved = new[] { "a", "x", "b" };
        var visible = new[] { "b", "a" };

        PanelOrder.Commit(saved, visible);

        Assert.Equal(new[] { "a", "x", "b" }, saved);
        Assert.Equal(new[] { "b", "a" }, visible);
    }

    [Fact]
    public void PanelOrder_AnAbsentAppComesBackToItsSpot()
    {
        var saved = new[] { "a#uid:1", "x#uid:1", "b#uid:1" };
        var committed = PanelOrder.Commit(saved, new[] { "b#uid:1", "a#uid:1" });

        var back = new[] { Icon(1, "a"), Icon(2, "b"), Icon(3, "x") };
        Assert.Equal(new ulong[] { 2, 1, 3 }, Ids(PanelOrder.Arrange(back, committed)));
    }
}
