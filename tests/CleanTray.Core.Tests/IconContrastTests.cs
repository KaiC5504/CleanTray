using CleanTray.Core;

namespace CleanTray.Core.Tests;

public sealed class IconContrastTests
{
    static byte[] Pixels(params (byte B, byte G, byte R, byte A)[] pixels) =>
        pixels.SelectMany(p => new[] { p.B, p.G, p.R, p.A }).ToArray();

    [Fact]
    public void WhiteGlyphOnLightTurnsDarkKeepingAlpha()
    {
        var icon = Pixels((255, 255, 255, 255), (250, 250, 250, 180), (0, 0, 0, 0));
        Assert.Equal(Pixels((0x1C, 0x1C, 0x1C, 255), (0x1C, 0x1C, 0x1C, 180), (0x1C, 0x1C, 0x1C, 0)),
            IconContrast.ForBackground(icon, darkBackground: false));
    }

    [Fact]
    public void BlackGlyphOnDarkTurnsLight()
    {
        var icon = Pixels((10, 10, 10, 255), (0, 0, 0, 200));
        Assert.Equal(Pixels((0xF2, 0xF2, 0xF2, 255), (0xF2, 0xF2, 0xF2, 200)),
            IconContrast.ForBackground(icon, darkBackground: true));
    }

    [Fact]
    public void GlyphAlreadyContrastingIsUntouched()
    {
        var white = Pixels((255, 255, 255, 255));
        var black = Pixels((0, 0, 0, 255));
        Assert.Same(white, IconContrast.ForBackground(white, darkBackground: true));
        Assert.Same(black, IconContrast.ForBackground(black, darkBackground: false));
    }

    [Fact]
    public void ColouredIconIsUntouched()
    {
        var icon = Pixels((255, 255, 255, 255), (255, 120, 40, 255));
        Assert.Same(icon, IconContrast.ForBackground(icon, darkBackground: false));
    }

    [Fact]
    public void FaintColourInSeeThroughPixelsDoesNotCount()
    {
        var icon = Pixels((255, 255, 255, 255), (0, 0, 255, 40));
        Assert.NotSame(icon, IconContrast.ForBackground(icon, darkBackground: false));
    }

    [Fact]
    public void FullyTransparentIconIsUntouched()
    {
        var icon = Pixels((255, 255, 255, 0));
        Assert.Same(icon, IconContrast.ForBackground(icon, darkBackground: false));
    }
}
