namespace CleanTray.Core.Tests;

public class RulesTableWriterTests
{
    static AppSettings OneApp(IconRule icon, bool appPopups = false)
    {
        var s = new AppSettings { CaptureNewByDefault = false };
        s.Apps.Add(new AppRule
        {
            Key = "c:\\a\\b.exe",
            Mode = RuleMode.Captured,
            Popups = appPopups,
            Icons = { icon },
        });
        return s;
    }

    [Fact]
    public void RulesTableWriter_MatchesFixture()
    {
        var s = OneApp(new IconRule { IconPart = "uid:7", Mode = RuleMode.Show, Popups = true });
        Assert.Equal(Fixtures.Load("rules_v1.hex"), RulesTableWriter.Build(s));
    }

    [Fact]
    public void RulesTableWriter_IconInheritsAppPopups()
    {
        var s = OneApp(new IconRule { IconPart = "uid:7", Mode = RuleMode.Show, Popups = null });
        var bytes = RulesTableWriter.Build(s);
        // Second entry starts after the 24-byte header and the first entry (4 + 20 bytes).
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 16));
        Assert.Equal(0, bytes[24 + 24 + 3]);
    }

    [Fact]
    public void RulesTableWriter_IconWithoutOverrideWritesNoEntry()
    {
        var s = OneApp(new IconRule { IconPart = "uid:7", Mode = null, Popups = null });
        Assert.Equal(1u, BitConverter.ToUInt32(RulesTableWriter.Build(s), 16));
    }

    [Fact]
    public void RulesTableWriter_CaptureNewByDefaultSetsFlagBit()
    {
        var bytes = RulesTableWriter.Build(new AppSettings { CaptureNewByDefault = true });
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, 12));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 16));
        Assert.Equal(24, bytes.Length);
    }

    [Fact]
    public void RulesTableWriter_KeysAreLowercasedAndSortedOrdinal()
    {
        var s = new AppSettings();
        s.Apps.Add(new AppRule { Key = "C:\\Z.exe" });
        s.Apps.Add(new AppRule { Key = "c:\\a.exe" });
        var bytes = RulesTableWriter.Build(s);
        var firstKey = System.Text.Encoding.Unicode.GetString(bytes, 28, 2 * BitConverter.ToUInt16(bytes, 24));
        Assert.Equal("c:\\a.exe", firstKey);
    }

    [Fact]
    public void RulesTableWriter_DuplicateKeysFirstWins()
    {
        var s = new AppSettings();
        s.Apps.Add(new AppRule { Key = "C:\\A.exe", Mode = RuleMode.Captured, Popups = false });
        s.Apps.Add(new AppRule { Key = "c:\\a.exe", Mode = RuleMode.Show, Popups = true });
        var bytes = RulesTableWriter.Build(s);
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, 16));
        Assert.Equal(1, bytes[26]);
        Assert.Equal(0, bytes[27]);
    }

    [Fact]
    public void RulesTableWriter_OverlongKeysAreSkipped()
    {
        var s = new AppSettings();
        s.Apps.Add(new AppRule { Key = new string('x', 4097) });
        s.Apps.Add(new AppRule { Key = new string('y', 4096) });
        var bytes = RulesTableWriter.Build(s);
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, 16));
        Assert.Equal(4096, BitConverter.ToUInt16(bytes, 24));
    }
}
