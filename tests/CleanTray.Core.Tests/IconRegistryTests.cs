namespace CleanTray.Core.Tests;

public class IconRegistryTests
{
    static IconUpsertEvent Icon(ulong id, string exe = "C:\\a.exe", bool captured = true, uint state = 0) =>
        new(id, 1, 1, (uint)id, false, default, 0x8001, 4, state, captured,
            exe.ToLowerInvariant(), exe, "tip", 0, 0, Array.Empty<byte>());

    [Fact]
    public void IconRegistry_PanelShowsCapturedVisibleOnly()
    {
        var r = new IconRegistry();
        r.Apply(Icon(1));
        r.Apply(Icon(2, captured: false));
        r.Apply(Icon(3, state: 1));

        Assert.Equal(3, r.All.Count);
        Assert.Equal(1UL, Assert.Single(r.PanelIcons).Id);
        Assert.True(r.All.Single(i => i.Id == 3).Hidden);
    }

    [Fact]
    public void IconRegistry_PanelOrdersByExePathThenId()
    {
        var r = new IconRegistry();
        r.Apply(Icon(5, "C:\\b.exe"));
        r.Apply(Icon(9, "C:\\a.exe"));
        r.Apply(Icon(2, "C:\\a.exe"));
        Assert.Equal(new ulong[] { 2, 9, 5 }, r.PanelIcons.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void IconRegistry_CaptureChangedOnlyForNewIconsAndFlips()
    {
        var r = new IconRegistry();
        Assert.True(r.CaptureChanged(Icon(1)));
        r.Apply(Icon(1));
        Assert.False(r.CaptureChanged(Icon(1)));
        Assert.True(r.CaptureChanged(Icon(1, captured: false)));
        r.Apply(new IconRemovedEvent(1));
        Assert.True(r.CaptureChanged(Icon(1)));
    }

    [Fact]
    public void IconRegistry_RemoveDropsIcon()
    {
        var r = new IconRegistry();
        r.Apply(Icon(1));
        r.Apply(new IconRemovedEvent(1));
        Assert.Empty(r.All);
    }

    [Fact]
    public void IconRegistry_UpsertReplacesSameId()
    {
        var r = new IconRegistry();
        r.Apply(Icon(1, captured: true));
        r.Apply(Icon(1, captured: false));
        Assert.Single(r.All);
        Assert.Empty(r.PanelIcons);
    }

    [Fact]
    public void IconRegistry_ChangedFiresOnChangeAndClear()
    {
        var r = new IconRegistry();
        var n = 0;
        r.Changed += () => n++;
        r.Apply(Icon(1));
        r.Apply(new IconRemovedEvent(1));
        r.Apply(new IconRemovedEvent(99));
        r.Apply(new HelloEvent(1, 1));
        r.Clear();
        Assert.Equal(2, n);

        r.Apply(Icon(2));
        r.Clear();
        Assert.Equal(4, n);
        Assert.Empty(r.All);
    }
}
