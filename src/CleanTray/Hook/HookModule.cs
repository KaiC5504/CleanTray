using System.Runtime.InteropServices;
using CleanTray.Core;
using CleanTray.Native;

namespace CleanTray.Hook;

/// <summary>CleanTrayHook.dll loaded into CleanTray itself, from the staged copy that Explorer gets too.</summary>
static class HookModule
{
    const string DllName = "CleanTrayHook.dll";

    static readonly object Gate = new();
    static readonly HashSet<string> MissingExports = new();
    static IntPtr _module;
    static bool _loadFailed;

    /// <summary>The module handle, or zero if the DLL can't be loaded. A failure is logged once and then remembered.</summary>
    public static IntPtr Load()
    {
        lock (Gate)
        {
            if (_module != IntPtr.Zero || _loadFailed) return _module;
            _module = TryLoad();
            _loadFailed = _module == IntPtr.Zero;
            return _module;
        }
    }

    /// <summary>The export's address, or zero. A missing export is logged once.</summary>
    public static IntPtr Export(string name)
    {
        lock (Gate)
        {
            var module = Load();
            if (module == IntPtr.Zero || MissingExports.Contains(name)) return IntPtr.Zero;
            var proc = Win32.GetProcAddress(module, name);
            if (proc == IntPtr.Zero)
            {
                MissingExports.Add(name);
                Log.Write($"inject: {name} not exported by {DllName}");
            }
            return proc;
        }
    }

    // File names only in the log: full paths would name the user's folders.
    static IntPtr TryLoad()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, DllName);
        if (!File.Exists(shipped))
        {
            Log.Write($"inject: {DllName} is missing");
            return IntPtr.Zero;
        }
        var module = Win32.LoadLibraryExW(StageCopy(shipped) ?? shipped, IntPtr.Zero, 0);
        if (module == IntPtr.Zero)
            Log.Write($"inject: LoadLibrary({DllName}) failed, error {Marshal.GetLastPInvokeError()}");
        return module;
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
