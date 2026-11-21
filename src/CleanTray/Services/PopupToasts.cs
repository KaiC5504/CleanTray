using CleanTray.Core;
using CleanTray.Native;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace CleanTray.Services;

/// <summary>
/// Shows a captured app's balloon as a toast and reports back whether the user clicked it, which
/// the DLL turns into the app's NIN_BALLOONUSERCLICK or NIN_BALLOONTIMEOUT.
/// </summary>
sealed class PopupToasts
{
    const string Group = "CleanTray";
    static readonly TimeSpan ToastLife = TimeSpan.FromMinutes(1);
    // A little past the toast's own expiry, for when its Dismissed event never arrives.
    static readonly TimeSpan TrackerExpiry = TimeSpan.FromSeconds(70);

    readonly Action<byte[]> _send;
    readonly PopupTracker _tracker;

    public PopupToasts(Action<byte[]> send)
    {
        _send = send;
        _tracker = new PopupTracker(OnResolved, TrackerExpiry);
        // Toast events arrive on thread-pool threads; clicks reach us here rather than on the toast object.
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    public void Show(PopupEvent e, string appName, uint pid)
    {
        string? token = null;
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                Log.Write($"toast: notifications are off for CleanTray ({notifier.Setting}), icon {e.IconId}");
                _send(CommandWriter.PopupResult(e.IconId, PopupOutcome.TimedOut));
                return;
            }

            var id = token = _tracker.Add(e.IconId, pid);
            var content = new ToastContentBuilder().AddArgument("popup", id).AddText(appName);
            if (e.Title.Length > 0) content.AddText(e.Title);
            if (e.Text.Length > 0) content.AddText(e.Text);
            var toast = new ToastNotification(content.GetToastContent().GetXml())
            {
                Tag = id,
                Group = Group,
                ExpirationTime = DateTimeOffset.Now + ToastLife,
            };
            toast.Activated += (_, _) => _tracker.Resolve(id, PopupOutcome.Clicked);
            toast.Dismissed += (_, _) => _tracker.Resolve(id, PopupOutcome.TimedOut);
            toast.Failed += (_, _) => _tracker.Resolve(id, PopupOutcome.TimedOut);
            notifier.Show(toast);
            Log.Write($"toast shown id={e.IconId}");
        }
        catch (Exception ex)
        {
            // The text is private, so only the exception type is logged.
            Log.Write($"toast: cannot show for icon {e.IconId}: {LogText.Error(ex)}");
            if (token is null) _send(CommandWriter.PopupResult(e.IconId, PopupOutcome.TimedOut));
            else _tracker.Resolve(token, PopupOutcome.TimedOut);
        }
    }

    /// <summary>Answers every open balloon as timed out and clears our toasts, before the DLL is told to release.</summary>
    public void Shutdown()
    {
        ToastNotificationManagerCompat.OnActivated -= OnActivated;
        _tracker.ResolveAll(PopupOutcome.TimedOut);
        _tracker.Dispose();
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception ex)
        {
            Log.Write($"toast: cannot clear history: {LogText.Error(ex)}");
        }
    }

    void OnActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        if (ToastArguments.Parse(args.Argument).TryGetValue("popup", out var token))
            _tracker.Resolve(token, PopupOutcome.Clicked);
    }

    void OnResolved(PopupResolution r)
    {
        // The app usually shows a window in response to the click, so it must be allowed the foreground first.
        if (r.Outcome == PopupOutcome.Clicked) Win32.AllowSetForegroundWindow(r.Pid);
        _send(CommandWriter.PopupResult(r.IconId, r.Outcome));
        Log.Write($"toast: icon {r.IconId} {r.Outcome}");
        if (r.Outcome == PopupOutcome.TimedOut) RemoveFromHistory(r.Token);
    }

    // A dismissed toast stays in the Action Center, where a late click would report a click for a balloon that is gone.
    static void RemoveFromHistory(string token)
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(token, Group);
        }
        catch (Exception ex)
        {
            Log.Write($"toast: cannot clear history: {LogText.Error(ex)}");
        }
    }
}
