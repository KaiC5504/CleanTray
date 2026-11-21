using System.Runtime.InteropServices;
using CleanTray.Native;

namespace CleanTray.Hook;

/// <summary>Installs CleanTrayHook's WH_GETMESSAGE hook on Explorer's tray thread. Not thread-safe.</summary>
sealed class Injector
{
    const string HookExport = "CleanTrayGetMsgProc";

    IntPtr _module;
    IntPtr _proc;
    IntPtr _hook;

    public uint TrayPid { get; private set; }

    public bool Inject()
    {
        Unhook();
        if (!EnsureLoaded()) return false;

        var tray = Win32.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero)
        {
            Log.Write("inject: no Shell_TrayWnd");
            return false;
        }
        var tid = Win32.GetWindowThreadProcessId(tray, out var pid);
        if (tid == 0) return false;

        _hook = Win32.SetWindowsHookExW(Win32.WH_GETMESSAGE, _proc, _module, tid);
        if (_hook == IntPtr.Zero)
        {
            Log.Write($"inject: SetWindowsHookEx failed, error {Marshal.GetLastPInvokeError()}");
            return false;
        }
        TrayPid = pid;
        // The DLL activates on the next message the tray thread pulls.
        Win32.PostMessageW(tray, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Log.Write($"inject: hooked explorer pid {pid} thread {tid}");
        return true;
    }

    public void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        if (!Win32.UnhookWindowsHookEx(_hook))
            Log.Write($"unhook failed, error {Marshal.GetLastPInvokeError()}");
        _hook = IntPtr.Zero;
    }

    bool EnsureLoaded()
    {
        if (_proc != IntPtr.Zero) return true;
        _module = HookModule.Load();
        if (_module == IntPtr.Zero) return false;
        _proc = HookModule.Export(HookExport);
        return _proc != IntPtr.Zero;
    }
}
