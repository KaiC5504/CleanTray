namespace CleanTray.Core;

/// <summary>
/// Windows' own tray icons (USB eject, volume, ...) are single-colour glyphs drawn for one taskbar
/// colour, and the taskbar recolours them to match its theme. This does the same for the panel.
/// </summary>
public static class IconContrast
{
    const byte Opaque = 128;
    const int MaxSpread = 24;
    const int LightGlyph = 200, DarkGlyph = 55;
    const byte OnLight = 0x1C, OnDark = 0xF2;

    /// <summary>
    /// A grey glyph that would vanish into the background comes back recoloured, keeping its alpha;
    /// anything else, coloured icons included, comes back as the same array.
    /// </summary>
    public static byte[] ForBackground(byte[] bgra, bool darkBackground)
    {
        long sum = 0, count = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < Opaque) continue;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > MaxSpread) return bgra;
            sum += (r + g + b) / 3;
            count++;
        }
        if (count == 0) return bgra;

        var mean = sum / count;
        if (darkBackground ? mean > DarkGlyph : mean < LightGlyph) return bgra;

        var shade = darkBackground ? OnDark : OnLight;
        var result = (byte[])bgra.Clone();
        for (var i = 0; i + 3 < result.Length; i += 4)
        {
            result[i] = result[i + 1] = result[i + 2] = shade;
        }
        return result;
    }
}
