using System.Globalization;

namespace CleanTray.Core;

public static class HotkeyParser
{
    const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    static readonly (string Name, uint Vk)[] NamedKeys =
    {
        ("Space", 0x20), ("Tab", 0x09), ("Enter", 0x0D), ("Esc", 0x1B), ("Backspace", 0x08),
        ("Insert", 0x2D), ("Delete", 0x2E), ("Home", 0x24), ("End", 0x23),
        ("PageUp", 0x21), ("PageDown", 0x22),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
    };

    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        var ok = TryParseCore(text, out modifiers, out vk);
        if (!ok)
        {
            modifiers = 0;
            vk = 0;
        }
        return ok;
    }

    static bool TryParseCore(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var haveKey = false;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            var mod = ModifierFor(part);
            if (mod != 0)
            {
                modifiers |= mod;
                continue;
            }
            if (haveKey || !TryParseKey(part, out vk)) return false;
            haveKey = true;
        }
        return haveKey && modifiers != 0;
    }

    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    static uint ModifierFor(string s) => s.ToLowerInvariant() switch
    {
        "ctrl" or "control" => ModControl,
        "alt" => ModAlt,
        "shift" => ModShift,
        "win" or "windows" => ModWin,
        _ => 0,
    };

    static bool TryParseKey(string s, out uint vk)
    {
        vk = 0;
        if (s.Length == 1 && char.IsAsciiLetterOrDigit(s[0]))
        {
            vk = char.ToUpperInvariant(s[0]);
            return true;
        }
        if (s.Length is 2 or 3 && (s[0] is 'F' or 'f')
            && int.TryParse(s.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
        {
            vk = (uint)(0x6F + n);
            return true;
        }
        foreach (var (name, code) in NamedKeys)
        {
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase))
            {
                vk = code;
                return true;
            }
        }
        return false;
    }

    static string KeyName(uint vk)
    {
        if (vk is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A)) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        foreach (var (name, code) in NamedKeys)
            if (code == vk) return name;
        return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }
}
