namespace CleanTray.Core;

public enum RuleMode
{
    Show = 0,
    Captured = 1,
}

public class AppSettings
{
    public int Version { get; set; } = 1;
    public bool ShowMasterIcon { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+T";
    public bool CaptureNewByDefault { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public List<AppRule> Apps { get; set; } = new();
}

public class AppRule
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ExePath { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
    public RuleMode Mode { get; set; }
    public bool Popups { get; set; } = true;
    public List<IconRule> Icons { get; set; } = new();
}

public class IconRule
{
    public string IconPart { get; set; } = "";
    public string Tip { get; set; } = "";

    // Null means inherit from the owning app rule.
    public RuleMode? Mode { get; set; }
    public bool? Popups { get; set; }
}
