using System.Runtime.InteropServices;
using CleanTray.Core;
using CleanTray.Native;

namespace CleanTray.Hook;

/// <summary>CleanTrayHook.dll loaded into CleanTray itself, from the staged copy that Explorer gets too.</summary>
static class HookModule
{
    const string DllName = "CleanTrayHook.dll";

    static readonly object Gate = new();
    static IntPtr _module;

    /// <summary>The module handle, or zero if the DLL can't be loaded (logged). A failure is retried on the next call.</summary>
    public static IntPtr Load()
    {
        lock (Gate)
        {
            if (_module != IntPtr.Zero) return _module;
            var shipped = Path.Combine(AppContext.BaseDirectory, DllName);
            if (!File.Exists(shipped))
            {
                Log.Write($"inject: {shipped} is missing");
                return IntPtr.Zero;
            }
            var path = StageCopy(shipped) ?? shipped;
            _module = Win32.LoadLibraryExW(path, IntPtr.Zero, 0);
            if (_module == IntPtr.Zero)
                Log.Write($"inject: LoadLibrary({path}) failed, error {Marshal.GetLastPInvokeError()}");
            return _module;
        }
    }

    /// <summary>The export's address, or zero (logged).</summary>
    public static IntPtr Export(string name)
    {
        var module = Load();
        if (module == IntPtr.Zero) return IntPtr.Zero;
        var proc = Win32.GetProcAddress(module, name);
        if (proc == IntPtr.Zero) Log.Write($"inject: {name} not exported by {DllName}");
        return proc;
    }

    // Explorer pins the DLL until it restarts, so injecting the shipped file would lock it
    // against rebuilds and updates. Each build gets its own content-addressed copy instead.
    static string? StageCopy(string shipped)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanTray", "hook");
        try
        {
            return FileStaging.Stage(shipped, root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"inject: staging the DLL failed ({LogText.Error(e)}), using the shipped copy");
            return null;
        }
    }
}
