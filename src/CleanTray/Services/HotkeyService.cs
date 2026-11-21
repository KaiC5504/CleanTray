using System.Runtime.InteropServices;
using System.Windows.Interop;
using CleanTray.Core;
using CleanTray.Native;
using CleanTray.Tray;

namespace CleanTray.Services;

/// <summary>A global shortcut delivered to the master icon's hidden window.</summary>
sealed class HotkeyService : IDisposable
{
    const int HotkeyId = 1;

    readonly MasterIcon _window;
    bool _registered;
    uint _modifiers;
    uint _vk;

    public HotkeyService(MasterIcon window)
    {
        _window = window;
        _window.AddMessageHook(WndProc);
    }

    public event Action? Pressed;

    public bool IsRegistered => _registered;

    /// <summary>
    /// Switches to the shortcut in <paramref name="text"/>. On failure the previous shortcut, if
    /// any, stays registered.
    /// </summary>
    public bool Register(string text)
    {
        if (!HotkeyParser.TryParse(text, out var modifiers, out var vk)) return false;
        if (_registered && modifiers == _modifiers && vk == _vk) return true;

        var previous = (_registered, _modifiers, _vk);
        Unregister();
        if (Win32.RegisterHotKey(_window.Handle, HotkeyId, modifiers | Win32.MOD_NOREPEAT, vk))
        {
            (_registered, _modifiers, _vk) = (true, modifiers, vk);
            Log.Write($"hotkey: registered {HotkeyParser.Format(modifiers, vk)}");
            return true;
        }

        Log.Write($"hotkey: cannot register {HotkeyParser.Format(modifiers, vk)}, error {Marshal.GetLastPInvokeError()}");
        if (previous._registered && Win32.RegisterHotKey(_window.Handle, HotkeyId, previous._modifiers | Win32.MOD_NOREPEAT, previous._vk))
            (_registered, _modifiers, _vk) = previous;
        return false;
    }

    public void Dispose() => Unregister();

    void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }
}
