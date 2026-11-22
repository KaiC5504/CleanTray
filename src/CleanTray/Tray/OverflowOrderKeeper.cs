using System.Runtime.InteropServices;
using CleanTray.Core;
using CleanTray.Native;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CleanTray.Tray;

/// <summary>
/// Keeps the master icon first in Windows' saved order of the hidden-icons flyout. Explorer reads
/// that order only when it starts, so the effect shows from the next sign-in or Explorer restart.
/// </summary>
sealed class OverflowOrderKeeper : IDisposable
{
    const string KeyPath = @"Control Panel\NotifyIconSettings";
    const string OrderValue = "UIOrderList";
    const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    readonly string _exePath;
    readonly ManualResetEvent _stop = new(false);
    readonly Thread _thread;

    public OverflowOrderKeeper(string exePath)
    {
        _exePath = exePath;
        // Its own thread: an asynchronous registry notification ends when the thread that asked for it exits.
        _thread = new Thread(Run) { IsBackground = true, Name = "overflow order" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.Join(TimeSpan.FromSeconds(1))) _stop.Dispose();
    }

    void Run()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) return;
            using var changed = new AutoResetEvent(false);
            while (true)
            {
                // Armed before the check, so an Explorer write in between still wakes us.
                var error = RegNotifyChangeKeyValue(key.Handle, false, REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true);
                if (error != 0)
                {
                    Log.Write($"overflow order: cannot watch the registry, error {error}");
                    return;
                }
                KeepFirst(key);
                if (WaitHandle.WaitAny([_stop, changed]) == 0) return;
            }
        }
        // Anything escaping a thread's top level would end the whole app over a cosmetic feature.
        catch (Exception ex)
        {
            Log.Write($"overflow order: stopped: {LogText.Error(ex)}");
        }
    }

    void KeepFirst(RegistryKey key)
    {
        if (key.GetValue(OrderValue) is not byte[] order) return;
        var id = OverflowOrder.FindIcon(Entries(key), _exePath, MasterIcon.Uid);
        if (id is null) return;
        var moved = OverflowOrder.MoveToFront(order, id.Value);
        if (moved is null) return;
        key.SetValue(OrderValue, moved, RegistryValueKind.Binary);
        Log.Write("overflow order: moved CleanTray to the front");
    }

    static IEnumerable<NotifyIconEntry> Entries(RegistryKey key)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            if (!ulong.TryParse(name, out var id)) continue;
            using var sub = key.OpenSubKey(name, writable: false);
            if (sub is null) continue;
            var path = sub.GetValue("ExecutablePath") is string raw ? KnownFolderPath.Resolve(raw, Win32.KnownFolderPath) : null;
            uint? uid = sub.GetValue("UID") is int u ? (uint)u : null;
            yield return new NotifyIconEntry(id, path, uid, sub.GetValue("IconGuid") is not null);
        }
    }

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint filter, SafeWaitHandle evt, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
