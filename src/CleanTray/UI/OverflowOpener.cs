using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using CleanTray.Core;
using CleanTray.Native;

namespace CleanTray.UI;

/// <summary>Opens the Windows tray overflow again by invoking the chevron on the taskbar.</summary>
static class OverflowOpener
{
    internal const string WindowClass = "TopLevelWindowForOverflowXamlIsland";
    const string ButtonClass = "SystemTray.NormalButton";
    // Reserved by Windows and ignored by every app, so pressing it has no effect.
    const byte UnassignedKey = 0xE8;
    const uint KeyUp = 0x2;

    // Explorer shows the flyout within a frame or two of the invoke; this leaves room for a busy
    // Explorer while a panel left floating alone for longer than half a second would be noticed.
    static readonly TimeSpan VerifyWindow = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    // UI Automation calls into Explorer and can block for as long as it does.
    static readonly TimeSpan Overall = TimeSpan.FromSeconds(3);

    /// <summary>The overflow window when it is showing, otherwise why not. Never carries personal data.</summary>
    internal readonly record struct Result(IntPtr Window, string Detail);

    /// <param name="taskbars">The taskbar windows, one of which holds the chevron.</param>
    /// <param name="lastOverflow">Where the overflow was showing; it opens directly above its chevron.</param>
    internal static async Task<Result> ReopenAsync(IReadOnlyList<IntPtr> taskbars, Win32.RECT lastOverflow)
    {
        var work = Task.Run(() => Reopen(taskbars, lastOverflow));
        if (await Task.WhenAny(work, Task.Delay(Overall)) != work) return new(IntPtr.Zero, "timed out");
        return await work;
    }

    static Result Reopen(IReadOnlyList<IntPtr> taskbars, Win32.RECT lastOverflow)
    {
        try
        {
            if (taskbars.Count == 0) return new(IntPtr.Zero, "no taskbar");
            // The click that got us here may not count as input to this process (a menu swallowed it), and
            // Explorer's flyout light-dismisses within ~150 ms when it is not allowed to take the foreground.
            // Sending input ourselves makes this the process that has last received some.
            Win32.keybd_event(UnassignedKey, 0, 0, 0);
            Win32.keybd_event(UnassignedKey, 0, KeyUp, 0);

            Win32.GetWindowThreadProcessId(taskbars[0], out var explorer);
            // Lets Explorer take the foreground for the flyout.
            var allowed = Win32.AllowSetForegroundWindow(explorer);
            var allowDetail = allowed ? "foreground allowed" : $"foreground not allowed, error {Marshal.GetLastPInvokeError()}";

            var chevron = FindChevron(taskbars, lastOverflow);
            if (chevron is null) return new(IntPtr.Zero, $"chevron not found, {allowDetail}");
            ((InvokePattern)chevron.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

            var deadline = DateTime.UtcNow + VerifyWindow;
            while (true)
            {
                var w = Win32.FindWindowW(WindowClass, null);
                if (w != IntPtr.Zero && Win32.IsWindowVisible(w)) return new(w, allowDetail);
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(PollInterval);
            }
            return new(IntPtr.Zero, $"overflow not shown, {allowDetail}");
        }
        catch (Exception ex)
        {
            return new(IntPtr.Zero, LogText.Error(ex));
        }
    }

    // The button's name is localized and other tray buttons share its class and automation id, so
    // the chevron is the invokable button nearest the old overflow.
    static AutomationElement? FindChevron(IReadOnlyList<IntPtr> taskbars, Win32.RECT lastOverflow)
    {
        var cx = (lastOverflow.Left + lastOverflow.Right) / 2.0;
        var cy = (lastOverflow.Top + lastOverflow.Bottom) / 2.0;
        var isButton = new PropertyCondition(AutomationElement.ClassNameProperty, ButtonClass);
        AutomationElement? best = null;
        var bestDistance = double.MaxValue;
        foreach (var taskbar in taskbars)
        {
            foreach (AutomationElement button in AutomationElement.FromHandle(taskbar).FindAll(TreeScope.Descendants, isButton))
            {
                if (!(bool)button.GetCurrentPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)) continue;
                Rect r = button.Current.BoundingRectangle;
                if (r.IsEmpty) continue;
                var dx = r.Left + r.Width / 2 - cx;
                var dy = r.Top + r.Height / 2 - cy;
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                best = button;
                bestDistance = distance;
            }
        }
        return best;
    }
}
