namespace CleanTray.Core.Tests;

public class HotkeyParserTests
{
    const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8;

    [Fact]
    public void HotkeyParser_RoundTripsCtrlAltT()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+T", out var mods, out var vk));
        Assert.Equal(ModControl | ModAlt, mods);
        Assert.Equal(0x54u, vk);
        Assert.Equal("Ctrl+Alt+T", HotkeyParser.Format(mods, vk));
    }

    [Fact]
    public void HotkeyParser_RejectsNoModifier()
    {
        Assert.False(HotkeyParser.TryParse("T", out _, out _));
    }

    [Theory]
    [InlineData("Ctrl+Shift+F5", ModControl | ModShift, 0x74u)]
    [InlineData("Win+Alt+7", ModWin | ModAlt, 0x37u)]
    [InlineData("ctrl + space", ModControl, 0x20u)]
    public void HotkeyParser_ParsesVariants(string text, uint mods, uint vk)
    {
        Assert.True(HotkeyParser.TryParse(text, out var m, out var k));
        Assert.Equal(mods, m);
        Assert.Equal(vk, k);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+T+U")]
    [InlineData("Ctrl+Bogus")]
    public void HotkeyParser_RejectsMalformed(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out var mods, out var vk));
        Assert.Equal(0u, mods);
        Assert.Equal(0u, vk);
    }

    [Fact]
    public void HotkeyParser_FormatsFunctionKeysInStableModifierOrder()
    {
        Assert.Equal("Ctrl+Alt+Shift+Win+F12", HotkeyParser.Format(ModWin | ModShift | ModAlt | ModControl, 0x7B));
    }
}
