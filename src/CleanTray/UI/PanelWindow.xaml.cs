using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CleanTray.Core;
using CleanTray.Native;
using Microsoft.Win32;

namespace CleanTray.UI;

public partial class PanelWindow : Window
{
    const int GapDip = 8;
    // Long enough for an overflow closed by our own click to be told apart from one the user left.
    static readonly TimeSpan OverflowSettle = TimeSpan.FromMilliseconds(150);

    readonly IconRegistry _registry;
    readonly Action<byte[]> _send;
    readonly Func<Rect> _anchor;
    readonly Func<IReadOnlyList<string>> _savedOrder;
    readonly Func<IReadOnlyList<string>, SaveOutcome> _commitOrder;
    readonly Func<Rect>? _placeAt;
    readonly PanelDismissal _dismissal = new();
    readonly PanelDrag _drag;
    // Win32 menus of the clicked app, by popup window: its pid can't be looked up once the menu is gone.
    readonly Dictionary<IntPtr, uint> _menus = new();
    // Kept in fields: the hooks call back through them for as long as they are installed.
    readonly Win32.WinEventProc _onWinEvent;
    readonly Win32.HookProc _onMouse;
    readonly List<IntPtr> _taskbars = new();
    Dictionary<ulong, Button> _buttons = new();
    IntPtr _hwnd;
    IntPtr _overflow;
    Win32.RECT _overflowRect;
    IntPtr _winEventHook;
    IntPtr _overflowHook;
    IntPtr _mouseHook;
    DispatcherTimer? _overflowCheck;
    bool _dark;
    bool _suppressClick;
    bool _closing;

    /// <param name="anchor">The master icon's rect, handed back to the DLL when the panel closes.</param>
    /// <param name="savedOrder">The saved icon order, read afresh each time since the settings can be replaced.</param>
    /// <param name="commitOrder">Saves the order the user dropped the icons in.</param>
    /// <param name="placeAt">Where to open instead of at <paramref name="anchor"/>, such as the cursor.</param>
    internal PanelWindow(IconRegistry registry, Action<byte[]> send, Func<Rect> anchor,
        Func<IReadOnlyList<string>> savedOrder, Func<IReadOnlyList<string>, SaveOutcome> commitOrder, Func<Rect>? placeAt = null)
    {
        InitializeComponent();
        _registry = registry;
        _send = send;
        _anchor = anchor;
        _savedOrder = savedOrder;
        _commitOrder = commitOrder;
        _placeAt = placeAt;
        _onWinEvent = OnWinEvent;
        _onMouse = OnMouse;
        _dark = SystemUsesDarkTheme();
        if (_dark) ApplyDarkTheme();

        _drag = new PanelDrag(Items, this);
        _drag.Lifted += b => b.Style = (Style)FindResource("IconButtonLifted");
        _drag.Ended += OnDragEnded;
        _registry.Changed += OnRegistryChanged;
        Rebuild();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            MakeNonActivating();
            ApplyFrame();
            InstallHooks();
        };
        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        Closing += (_, _) => _closing = true;
        Closed += OnClosed;
    }

    // Taking activation would make the Windows overflow, which the master icon usually sits in,
    // light-dismiss itself and leave the panel floating alone. Clicks still reach the buttons.
    void MakeNonActivating()
    {
        var ex = Win32.GetWindowLongPtrW(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if ((uint)msg != Win32.WM_MOUSEACTIVATE) return IntPtr.Zero;
            handled = true;
            return new IntPtr(Win32.MA_NOACTIVATE);
        });
    }

    void SafeClose()
    {
        if (!_closing) Close();
    }

    void OnClosed(object? sender, EventArgs e)
    {
        RemoveHooks();
        _drag.Dispose();
        _registry.Changed -= OnRegistryChanged;
        var r = _anchor();
        _send(CommandWriter.SetAnchor((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom));
    }

    // A drag leaves the panel alone until it ends, then rebuilds once. An icon that goes away under
    // the pointer ends it at once.
    void OnRegistryChanged()
    {
        if (_drag.Dragged is not { } dragged)
        {
            Rebuild();
            return;
        }
        var id = ((TrayIconInfo)dragged.Tag).Id;
        if (!_registry.PanelIcons.Any(i => i.Id == id)) _drag.Cancel();
    }

    void OnDragEnded(Button b, bool dropped)
    {
        b.Style = (Style)FindResource("IconButton");
        if (_closing) return;
        if (dropped) SaveOrder();
        Rebuild();
    }

    void SaveOrder()
    {
        var shown = Items.Children.Cast<Button>().Select(b => ((TrayIconInfo)b.Tag).Key).ToList();
        var saved = _savedOrder();
        var next = PanelOrder.Commit(saved, shown);
        // Two icons with one key can swap places without the saved order changing.
        if (next.SequenceEqual(saved)) return;
        if (_commitOrder(next) == SaveOutcome.Saved) Log.Write($"panel: reordered {shown.Count} icons");
    }

    // Registry changes fire for every icon in the tray, so buttons are kept and updated in place:
    // recreating them would drop hover and any UI Automation reference to them.
    void Rebuild()
    {
        var icons = PanelOrder.Arrange(_registry.PanelIcons, _savedOrder());
        var next = new Dictionary<ulong, Button>();
        foreach (var icon in icons)
        {
            if (_buttons.TryGetValue(icon.Id, out var b)) Update(b, icon);
            else b = MakeButton(icon);
            next[icon.Id] = b;
        }

        var order = icons.Select(i => next[i.Id]).ToList();
        if (!Items.Children.Cast<UIElement>().SequenceEqual(order))
        {
            Items.Children.Clear();
            foreach (var b in order) Items.Children.Add(b);
        }
        _buttons = next;
        Empty.Visibility = Items.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Update(Button b, TrayIconInfo icon)
    {
        var old = (TrayIconInfo)b.Tag;
        b.Tag = icon;
        if (old.Tip != icon.Tip || old.ExePath != icon.ExePath) SetText(b, icon);
        if (old.Width != icon.Width || old.Height != icon.Height || !old.Bgra.AsSpan().SequenceEqual(icon.Bgra))
            b.Content = MakeContent(icon);
    }

    void SetText(Button b, TrayIconInfo icon)
    {
        var name = DisplayName(icon);
        if (b.ToolTip is not ToolTip label) b.ToolTip = label = MakeLabel();
        label.Content = string.IsNullOrEmpty(icon.Tip) ? name : icon.Tip;
        AutomationProperties.SetName(b, name);
    }

    // The brushes are set here rather than looked up by the template: a tooltip's popup is not
    // in this window's tree, and the panel's theme is fixed once the window exists.
    ToolTip MakeLabel() => new()
    {
        Style = (Style)FindResource("HoverLabel"),
        Foreground = (Brush)FindResource("PanelText"),
        Background = (Brush)FindResource("PanelBackground"),
        BorderBrush = (Brush)FindResource("LabelBorder"),
    };

    static object MakeContent(TrayIconInfo icon)
    {
        var name = DisplayName(icon);
        return MakeImage(icon) ?? (object)new TextBlock
        {
            Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
            FontWeight = FontWeights.SemiBold,
        };
    }

    Button MakeButton(TrayIconInfo icon)
    {
        var b = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Tag = icon,
            Content = MakeContent(icon),
        };
        SetText(b, icon);
        b.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1) return;
            _suppressClick = false;
            _drag.Press(b, e);
        };
        b.Click += (_, _) =>
        {
            // The second click of a double-click was already sent as Double.
            if (_suppressClick)
            {
                _suppressClick = false;
                return;
            }
            SendClick(b, ClickKind.Left);
        };
        b.MouseDoubleClick += (_, e) =>
        {
            e.Handled = true;
            _suppressClick = true;
            SendClick(b, ClickKind.Double);
        };
        b.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            SendClick(b, ClickKind.Right);
        };
        return b;
    }

    void SendClick(Button b, ClickKind kind)
    {
        var icon = (TrayIconInfo)b.Tag;
        // Physical pixels: the process is per-monitor aware, so PointToScreen does not scale.
        var center = b.PointToScreen(new Point(b.ActualWidth / 2, b.ActualHeight / 2));
        // The app usually shows a window or menu in response; let it take the foreground from us.
        if (!Win32.AllowSetForegroundWindow(icon.Pid))
            Log.Write($"panel: AllowSetForegroundWindow({icon.Pid}) failed, error {Marshal.GetLastPInvokeError()}");
        _dismissal.Clicked(icon.Pid);
        _menus.Clear();
        _send(CommandWriter.Click(icon.Id, kind, (int)Math.Round(center.X), (int)Math.Round(center.Y)));
        Log.Write($"panel: {kind} on icon {icon.Id} at {center.X:F0},{center.Y:F0}");
    }

    void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var self)) return;

        var a = (_placeAt ?? _anchor)();
        var anchor = new Win32.RECT
        {
            Left = (int)a.Left, Top = (int)a.Top, Right = (int)Math.Max(a.Right, a.Left + 1), Bottom = (int)Math.Max(a.Bottom, a.Top + 1),
        };
        // The overflow stays open now, so open beside it rather than over its icons.
        if (IsShown(_overflow, out var overflow) && Contains(overflow, (anchor.Left + anchor.Right) / 2, (anchor.Top + anchor.Bottom) / 2))
            anchor = overflow;
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfoW(Win32.MonitorFromRect(ref anchor, Win32.MONITOR_DEFAULTTONEAREST), ref info)) return;
        var work = info.rcWork;
        var gap = (int)Math.Round(GapDip * VisualTreeHelper.GetDpi(this).DpiScaleX);
        int w = self.Width, h = self.Height;
        int cx = (anchor.Left + anchor.Right) / 2, cy = (anchor.Top + anchor.Bottom) / 2;

        int x = cx - w / 2, y;
        if (anchor.Top >= work.Bottom) y = work.Bottom - h - gap;
        else if (anchor.Bottom <= work.Top) y = work.Top + gap;
        else if (anchor.Left >= work.Right)
        {
            x = work.Right - w - gap;
            y = cy - h / 2;
        }
        else if (anchor.Right <= work.Left)
        {
            x = work.Left + gap;
            y = cy - h / 2;
        }
        else y = anchor.Top - h - gap;

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        if (x != self.Left || y != self.Top)
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        SendAnchor();
    }

    /// <summary>Tells the DLL to answer captured icons' rect queries with this panel's rect.</summary>
    internal void SendAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && Win32.GetWindowRect(hwnd, out var r))
            _send(CommandWriter.SetAnchor(r.Left, r.Top, r.Right, r.Bottom));
    }

    // Activation can't tell the panel when to go: it never has it. The WinEvent hook catches
    // keyboard switches and anything taking the foreground; the mouse hook catches clicks that change
    // nothing, such as on the desktop when it is already in front. Both are out-of-context and called
    // on this thread, so nothing polls.
    void InstallHooks()
    {
        FindTray();
        _dismissal.Opened(Win32.GetForegroundWindow(), Win32.IsWindowVisible(_overflow), atCursor: _placeAt is not null);
        var overflowOpen = _dismissal.WatchesOverflow;
        _winEventHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_MENUPOPUPEND,
            IntPtr.Zero, _onWinEvent, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        if (overflowOpen)
        {
            // Hide events are frequent system-wide; only Explorer's matter here.
            Win32.GetWindowThreadProcessId(_overflow, out var shell);
            _overflowHook = Win32.SetWinEventHook(Win32.EVENT_OBJECT_HIDE, Win32.EVENT_OBJECT_HIDE,
                IntPtr.Zero, _onWinEvent, shell, 0, Win32.WINEVENT_OUTOFCONTEXT);
        }
        _mouseHook = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _onMouse, Win32.GetModuleHandleW(null), 0);
        if (_winEventHook == IntPtr.Zero || _mouseHook == IntPtr.Zero || (overflowOpen && _overflowHook == IntPtr.Zero))
            Log.Write($"panel: hooks missing (foreground {_winEventHook != IntPtr.Zero}, overflow {_overflowHook != IntPtr.Zero}, mouse {_mouseHook != IntPtr.Zero})");
    }

    void FindTray()
    {
        _overflow = Win32.FindWindowW(OverflowOpener.WindowClass, null);
        IsShown(_overflow, out _overflowRect);
        var main = Win32.FindWindowW("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) _taskbars.Add(main);
        for (var w = IntPtr.Zero; (w = Win32.FindWindowExW(IntPtr.Zero, w, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero;)
            _taskbars.Add(w);
    }

    void RemoveHooks()
    {
        _overflowCheck?.Stop();
        if (_winEventHook != IntPtr.Zero) Win32.UnhookWinEvent(_winEventHook);
        if (_overflowHook != IntPtr.Zero) Win32.UnhookWinEvent(_overflowHook);
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        _winEventHook = _overflowHook = _mouseHook = IntPtr.Zero;
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_closing) return;
        try
        {
            switch (ev)
            {
                case Win32.EVENT_SYSTEM_FOREGROUND:
                    var fg = CurrentForeground();
                    if (_dismissal.ForegroundChanged(fg)) CloseSoon("foreground change", fg);
                    break;
                case Win32.EVENT_SYSTEM_MENUPOPUPSTART when hwnd != IntPtr.Zero:
                    Win32.GetWindowThreadProcessId(hwnd, out var pid);
                    if (_dismissal.MenuStarted(pid)) _menus[hwnd] = pid;
                    break;
                case Win32.EVENT_SYSTEM_MENUPOPUPEND when _menus.Remove(hwnd, out var menuPid):
                    var after = CurrentForeground();
                    if (_dismissal.MenuEnded(menuPid, after)) CloseSoon("menu end", after);
                    break;
                case Win32.EVENT_OBJECT_HIDE when hwnd == _overflow && idObject == Win32.OBJID_WINDOW:
                    _overflowCheck ??= NewOverflowCheck();
                    _overflowCheck.Stop();
                    _overflowCheck.Start();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"panel: foreground hook failed: {LogText.Error(ex)}");
        }
    }

    DispatcherTimer NewOverflowCheck()
    {
        var timer = new DispatcherTimer { Interval = OverflowSettle };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_closing || Win32.IsWindowVisible(_overflow)) return;
            Win32.GetCursorPos(out var pt);
            var fg = CurrentForeground();
            switch (_dismissal.OverflowClosed(IsShown(_hwnd, out var self) && Contains(self, pt.X, pt.Y), fg))
            {
                case OverflowOutcome.Close:
                    CloseSoon("overflow closed", fg);
                    break;
                case OverflowOutcome.Reopen:
                    ReopenOverflow();
                    break;
            }
        };
        return timer;
    }

    // Opening the app's menu can take the foreground and close the overflow, which would leave the
    // panel floating alone. Bring the overflow back, or close with it if that does not work.
    async void ReopenOverflow()
    {
        Log.Write("panel: overflow closed with the pointer over the panel, reopening it");
        OverflowOpener.Result result;
        try
        {
            result = await OverflowOpener.ReopenAsync(_taskbars.ToArray(), _overflowRect);
        }
        catch (Exception ex)
        {
            result = new(IntPtr.Zero, LogText.Error(ex));
        }
        if (_closing) return;
        if (result.Window == IntPtr.Zero)
        {
            Log.Write($"panel: reopening the overflow failed: {result.Detail}");
            if (_dismissal.ReopenFailed()) CloseSoon($"overflow closed (reopen failed: {result.Detail})", CurrentForeground());
            return;
        }
        _overflow = result.Window;
        IsShown(_overflow, out _overflowRect);
        _dismissal.Reopened();
        Log.Write($"panel: overflow reopened ({result.Detail})");
        Reposition();
        // The overflow can light-dismiss itself again before its hide event is watched for: check once more.
        _overflowCheck ??= NewOverflowCheck();
        _overflowCheck.Stop();
        _overflowCheck.Start();
    }

    // Runs for every mouse event system-wide while the panel is open, so it only reads window state
    // (no messages, nothing that can block) and defers the close.
    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var msg = (uint)wParam.ToInt64();
            if (code >= 0 && !_closing
                && msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN)
            {
                int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4);
                var fg = CurrentForeground();
                var at = new ClickPoint(IsOverTray(x, y), IsOverMenu(x, y), IsShown(fg.Window, out var r) && Contains(r, x, y), IsOverTaskbar(x, y));
                if (_dismissal.ButtonDown(at, fg)) CloseSoon(at.OnTaskbar ? "taskbar click" : "outside click", fg);
            }
        }
        catch (Exception ex)
        {
            var error = LogText.Error(ex);
            Dispatcher.BeginInvoke(() => Log.Write($"panel: mouse hook failed: {error}"));
        }
        return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    bool IsOverTray(int x, int y)
    {
        if (IsShown(_hwnd, out var r) && Contains(r, x, y)) return true;
        if (IsShown(_overflow, out r) && Contains(r, x, y)) return true;
        foreach (var t in _taskbars)
            if (IsShown(t, out r) && Contains(r, x, y)) return true;
        return false;
    }

    // The taskbars can extend under the overflow and the panel, which sit above them.
    bool IsOverTaskbar(int x, int y)
    {
        if (IsShown(_hwnd, out var r) && Contains(r, x, y)) return false;
        if (IsShown(_overflow, out r) && Contains(r, x, y)) return false;
        foreach (var t in _taskbars)
            if (IsShown(t, out r) && Contains(r, x, y)) return true;
        return false;
    }

    bool IsOverMenu(int x, int y)
    {
        foreach (var menu in _menus.Keys)
            if (IsShown(menu, out var r) && Contains(r, x, y)) return true;
        return false;
    }

    static bool IsShown(IntPtr hwnd, out Win32.RECT r)
    {
        r = default;
        return hwnd != IntPtr.Zero && Win32.IsWindowVisible(hwnd) && Win32.GetWindowRect(hwnd, out r);
    }

    static bool Contains(Win32.RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    // Deferred, logging included: the hooks must return quickly and must not close the window from inside a callback.
    void CloseSoon(string reason, ForegroundInfo fg) => Dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        Log.Write($"panel: closing on {reason}, foreground pid {fg.Pid} tray={fg.IsTray} visible={fg.Visible} popup={fg.Popup} caption={fg.Caption}");
        SafeClose();
    });

    ForegroundInfo CurrentForeground()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return default;
        Win32.GetWindowThreadProcessId(fg, out var pid);
        var style = Win32.GetWindowLongPtrW(fg, Win32.GWL_STYLE).ToInt64();
        var tray = fg == _hwnd || fg == _overflow || _taskbars.Contains(fg);
        return new ForegroundInfo(pid, tray, Win32.IsWindowVisible(fg),
            (style & Win32.WS_POPUP) != 0, (style & Win32.WS_CAPTION) == Win32.WS_CAPTION, fg);
    }

    // The Windows 11 tray overflow look: rounded corners with the DWM border and shadow. No acrylic
    // backdrop: it only shows on an active window, and this one never is.
    void ApplyFrame()
    {
        var round = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var dark = _dark ? 1 : 0;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        // A borderless window gets the DWM shadow only with some frame extended into it.
        var margins = new Win32.MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        Win32.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
    }

    static string DisplayName(TrayIconInfo icon)
    {
        var tip = icon.Tip.Split('\n', 2)[0].Trim();
        return tip.Length > 0 ? tip : Path.GetFileNameWithoutExtension(icon.ExePath);
    }

    static Image? MakeImage(TrayIconInfo icon)
    {
        if (icon.Width <= 0 || icon.Height <= 0 || icon.Bgra.Length != icon.Width * icon.Height * 4) return null;
        // The DLL sends straight (not premultiplied) alpha, which is what Bgra32 means.
        var source = BitmapSource.Create(icon.Width, icon.Height, 96, 96, PixelFormats.Bgra32, null, icon.Bgra, icon.Width * 4);
        source.Freeze();
        var image = new Image { Source = source, Width = 16, Height = 16 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    // The taskbar follows the system theme, not the apps theme.
    static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light == 0;
    }

    void ApplyDarkTheme()
    {
        Resources["PanelBackground"] = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C));
        Resources["PanelText"] = new SolidColorBrush(Colors.White);
        Resources["ItemHover"] = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        Resources["ItemPressed"] = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
        Resources["LabelBorder"] = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    }
}
