using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CleanTray.Native;

namespace CleanTray.Tray;

/// <summary>CleanTray's own notification icon and its menu.</summary>
sealed class MasterIcon : IDisposable
{
    const uint Uid = 1;
    const uint CallbackMsg = Win32.WM_APP + 1;
    const uint CmdSettings = 1, CmdPause = 2, CmdExit = 3;

    readonly HwndSource _window;
    readonly uint _taskbarCreated;
    readonly IntPtr _icon;
    string _tip = "CleanTray";
    bool _visible;
    bool _added;

    public MasterIcon()
    {
        // Top-level rather than message-only: only top-level windows get the TaskbarCreated broadcast.
        _window = new HwndSource(new HwndSourceParameters("CleanTray")
        {
            WindowStyle = Win32.WS_POPUP,
            ExtendedWindowStyle = Win32.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");
        // Lets an elevated CleanTray still hear a medium-integrity Explorer.
        Win32.ChangeWindowMessageFilterEx(Handle, _taskbarCreated, Win32.MSGFLT_ALLOW, IntPtr.Zero);
        var size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, Win32.GetDpiForWindow(Handle));
        _icon = LoadEmbeddedIcon(size);
    }

    public IntPtr Handle => _window.Handle;

    public bool Paused { get; set; }

    public void AddMessageHook(HwndSourceHook hook) => _window.AddHook(hook);

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action? TaskbarCreated;
    public event Action? SettingsClicked;
    public event Action? PauseClicked;
    public event Action? ExitClicked;
    public event Action? SessionEnding;

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) Add();
        else Remove();
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        var data = NewData(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    /// <summary>The icon's rect in physical pixels, or a 1x1 rect at the cursor if the shell can't say.</summary>
    public Rect GetScreenRect()
    {
        var id = new Win32.NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONIDENTIFIER>(),
            hWnd = Handle,
            uID = Uid,
        };
        if (_added && Win32.Shell_NotifyIconGetRect(ref id, out var r) == 0 && r.Width > 0 && r.Height > 0)
            return new Rect(r.Left, r.Top, r.Width, r.Height);
        Win32.GetCursorPos(out var pt);
        return new Rect(pt.X, pt.Y, 1, 1);
    }

    public void Dispose()
    {
        Remove();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
    }

    void Add()
    {
        if (_added) return;
        var data = NewData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        if (!Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data))
        {
            // Left over from an Explorer that restarted before we saw TaskbarCreated.
            Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
            if (!Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data))
            {
                Log.Write("master icon: NIM_ADD failed");
                return;
            }
        }
        data.uVersion = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIconW(Win32.NIM_SETVERSION, ref data);
        _added = true;
    }

    void Remove()
    {
        if (!_added) return;
        var data = NewData(0);
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        _added = false;
    }

    Win32.NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = Uid,
        uFlags = flags,
        uCallbackMessage = CallbackMsg,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == _taskbarCreated)
        {
            _added = false;
            if (_visible) Add();
            TaskbarCreated?.Invoke();
            return IntPtr.Zero;
        }

        switch ((uint)msg)
        {
            case CallbackMsg:
                // Version 4: the event is in LOWORD(lParam), the anchor point in wParam.
                var ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev is Win32.NIN_SELECT or Win32.NIN_KEYSELECT)
                {
                    LeftClick?.Invoke();
                }
                else if (ev == Win32.WM_CONTEXTMENU)
                {
                    RightClick?.Invoke();
                    var w = wParam.ToInt64();
                    ShowMenu((short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF));
                }
                handled = true;
                break;
            case Win32.WM_ENDSESSION:
                // Synchronous: the process may be ended as soon as this returns. Handlers must only
                // release the hook here and defer anything that disposes this window.
                if (wParam != IntPtr.Zero) SessionEnding?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    void ShowMenu(int x, int y)
    {
        var menu = Win32.CreatePopupMenu();
        try
        {
            Win32.AppendMenuW(menu, Win32.MF_STRING, CmdSettings, "Settings…");
            Win32.AppendMenuW(menu, Win32.MF_STRING | (Paused ? Win32.MF_CHECKED : 0), CmdPause, "Pause");
            Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenuW(menu, Win32.MF_STRING, CmdExit, "Exit");

            // Without the foreground the menu would not close when the user clicks elsewhere.
            Win32.SetForegroundWindow(Handle);
            var cmd = Win32.TrackPopupMenuEx(menu,
                Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY, x, y, Handle, IntPtr.Zero);
            Win32.PostMessageW(Handle, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            // Deferred: Exit disposes this window, which must not happen inside its own WndProc.
            Action? handler = cmd switch
            {
                CmdSettings => SettingsClicked,
                CmdPause => PauseClicked,
                CmdExit => ExitClicked,
                _ => null,
            };
            if (handler is not null) _window.Dispatcher.BeginInvoke(handler);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    // Picks the smallest image at least `size` px wide from the embedded .ico (the largest if none is).
    static IntPtr LoadEmbeddedIcon(int size)
    {
        using var stream = typeof(MasterIcon).Assembly.GetManifestResourceStream("CleanTray.ico");
        if (stream is null) return IntPtr.Zero;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        int best = -1, bestWidth = 0;
        for (var i = 0; i < count; i++)
        {
            var width = bytes[6 + 16 * i] == 0 ? 256 : bytes[6 + 16 * i];
            var better = best < 0
                || (width >= size && (bestWidth < size || width < bestWidth))
                || (width < size && bestWidth < size && width > bestWidth);
            if (better)
            {
                best = i;
                bestWidth = width;
            }
        }
        if (best < 0) return IntPtr.Zero;

        var entry = bytes.AsSpan(6 + 16 * best);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        var icon = Win32.CreateIconFromResourceEx(ref bytes[offset], length, true, 0x00030000, size, size, 0);
        if (icon == IntPtr.Zero) Log.Write($"master icon: CreateIconFromResourceEx failed, error {Marshal.GetLastPInvokeError()}");
        return icon;
    }
}
