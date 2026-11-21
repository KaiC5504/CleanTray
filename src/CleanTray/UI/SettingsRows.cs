using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanTray.Core;
using CleanTray.Native;

namespace CleanTray.UI;

abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

sealed class AppRow : Observable
{
    public static readonly string[] ModeChoices = ["Show", "Inside CleanTray"];

    readonly Func<AppRule, AppRule> _adopt;
    readonly Action _changed;
    readonly Dictionary<string, IconRow> _iconRows = new();
    AppRule _rule;
    string _path;
    byte[]? _snapshot;
    bool _draft;
    ImageSource? _icon;
    bool _fallbackTried;
    string _lastSeen = "";
    bool _expanded;
    TrayIconInfo? _shown;

    /// <param name="adopt">Puts a known app's draft rule into the settings, returning the rule to edit.</param>
    public AppRow(AppListEntry entry, Func<AppRule, AppRule> adopt, Action changed)
    {
        _rule = entry.Rule;
        _path = Resolved(_rule.ExePath);
        _draft = entry.Group == AppListGroup.KnownOnly;
        _snapshot = entry.IconPng;
        _adopt = adopt;
        _changed = changed;
        Group = entry.Group;
    }

    /// <summary>The group the row was listed in; it keeps its place even after the app is seen.</summary>
    public AppListGroup Group { get; }
    public string Key => _rule.Key;
    public string Name => AppList.Name(_rule);
    public string Path => _path;
    public string ModeAutomationName => Name + " mode";
    public string PopupsAutomationName => AutomationText.ForCheckBox(Name + " pop-ups");
    public ObservableCollection<IconRow> Icons { get; } = new();
    public bool HasSubRows => Icons.Count > 1;

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            Raise();
        }
    }

    public string LastSeen
    {
        get => _lastSeen;
        private set
        {
            if (_lastSeen == value) return;
            _lastSeen = value;
            Raise();
        }
    }

    public int ModeIndex
    {
        get => ModeChoice.AppIndex(_rule.Mode);
        set
        {
            if (!ModeChoice.TryApp(value, out var mode) || _rule.Mode == mode) return;
            Persist();
            _rule.Mode = mode;
            Raise();
            _changed();
        }
    }

    public bool Popups
    {
        get => _rule.Popups;
        set
        {
            if (_rule.Popups == value) return;
            Persist();
            _rule.Popups = value;
            Raise();
            _changed();
        }
    }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            Raise();
        }
    }

    /// <summary>Switches the row to the settings' rule for its app, e.g. once a known app has been seen.</summary>
    public void Bind(AppRule rule)
    {
        _draft = false;
        if (ReferenceEquals(rule, _rule)) return;
        _rule = rule;
        _path = Resolved(rule.ExePath);
        Raise(string.Empty);
    }

    /// <summary>Windows' own picture of the tray icon, shown until the app's live icon arrives.</summary>
    public void UseSnapshot(byte[]? png)
    {
        if (png is null || _snapshot is not null) return;
        _snapshot = png;
        if (_shown is null && IconImages.FromPng(png) is { } image) Icon = image;
    }

    // The tray reports some exe paths with a known-folder GUID in front.
    static string Resolved(string path) => KnownFolderPath.Resolve(path, Win32.KnownFolderPath) ?? path;

    // UI Automation names list items by their data's ToString.
    public override string ToString() => Name;

    // Known apps stay out of settings.json until the user changes one.
    void Persist()
    {
        if (_draft) Bind(_adopt(_rule));
    }

    /// <summary>Brings the row up to date with the rule and the app's running icons.</summary>
    public void Refresh(IEnumerable<TrayIconInfo> running, DateTime nowUtc)
    {
        LastSeen = LastSeenText.Format(_rule.LastSeenUtc, nowUtc);

        foreach (var icon in _rule.Icons)
        {
            if (_iconRows.TryGetValue(icon.IconPart, out var row))
            {
                row.Refresh();
                continue;
            }
            row = new IconRow(icon, Name, _changed);
            _iconRows[icon.IconPart] = row;
            Icons.Add(row);
            Raise(nameof(HasSubRows));
        }

        var best = running.FirstOrDefault(i => i.Bgra.Length > 0);
        if (best is not null && !SameImage(_shown, best))
        {
            _shown = best;
            Icon = IconImages.From(best);
        }
        else if (_icon is null && !_fallbackTried)
        {
            // Windows' snapshot is the tray icon itself, closer than the exe's icon.
            _fallbackTried = true;
            Icon = IconImages.FromPng(_snapshot) ?? IconImages.FromExe(_path);
        }
    }

    static bool SameImage(TrayIconInfo? a, TrayIconInfo b) =>
        a is not null && a.Width == b.Width && a.Height == b.Height && a.Bgra.AsSpan().SequenceEqual(b.Bgra);
}

sealed class IconRow : Observable
{
    public static readonly string[] ModeChoices = ["Same as app", "Show", "Inside CleanTray"];

    readonly IconRule _rule;
    readonly string _appName;
    readonly Action _changed;

    public IconRow(IconRule rule, string appName, Action changed)
    {
        _rule = rule;
        _appName = appName;
        _changed = changed;
    }

    public string Tip
    {
        get
        {
            var line = _rule.Tip.Split('\n', 2)[0].Trim();
            return line.Length > 0 ? line : "Icon " + _rule.IconPart;
        }
    }

    public string ModeAutomationName => $"{_appName}, {Tip} mode";
    public string PopupsAutomationName => AutomationText.ForCheckBox($"{_appName}, {Tip} pop-ups");

    public int ModeIndex
    {
        get => ModeChoice.IconIndex(_rule.Mode);
        set
        {
            if (!ModeChoice.TryIcon(value, out var mode) || _rule.Mode == mode) return;
            _rule.Mode = mode;
            Raise();
            _changed();
        }
    }

    public bool? Popups
    {
        get => _rule.Popups;
        set
        {
            if (_rule.Popups == value) return;
            _rule.Popups = value;
            Raise();
            Raise(nameof(PopupsLabel));
            _changed();
        }
    }

    public string PopupsLabel => _rule.Popups is null ? "Pop-ups (same as app)" : "Pop-ups";

    public void Refresh() => Raise(nameof(Tip));
}

static class AutomationText
{
    // A CheckBox's automation peer strips access-key underscores even from an explicit name.
    public static string ForCheckBox(string name) => name.Replace("_", "__");
}

static class IconImages
{
    static readonly Dictionary<string, ImageSource?> ExeCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? From(TrayIconInfo icon)
    {
        if (icon.Width <= 0 || icon.Height <= 0 || icon.Bgra.Length != icon.Width * icon.Height * 4) return null;
        // The DLL sends straight (not premultiplied) alpha, which is what Bgra32 means.
        var source = BitmapSource.Create(icon.Width, icon.Height, 96, 96, PixelFormats.Bgra32, null, icon.Bgra, icon.Width * 4);
        source.Freeze();
        return source;
    }

    /// <summary>Null when the bytes don't decode.</summary>
    public static ImageSource? FromPng(byte[]? png)
    {
        if (png is null) return null;
        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception e) when (e is NotSupportedException or FormatException or IOException or ArgumentException
                                       or InvalidOperationException or OverflowException or COMException)
        {
            return null;
        }
    }

    /// <summary>The exe's own icon, for apps that are not running right now.</summary>
    public static ImageSource? FromExe(string exePath)
    {
        if (ExeCache.TryGetValue(exePath, out var cached)) return cached;
        ImageSource? source = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
        ExeCache[exePath] = source;
        return source;
    }
}
