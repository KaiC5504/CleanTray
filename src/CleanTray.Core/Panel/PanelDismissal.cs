namespace CleanTray.Core;

/// <summary>
/// The foreground window as the panel sees it. Pid 0 means Windows reported none, as it does
/// mid-switch. <see cref="IsTray"/> covers the panel itself, the taskbars and the overflow flyout.
/// </summary>
public readonly record struct ForegroundInfo(uint Pid, bool IsTray, bool Visible, bool Popup, bool Caption, nint Window = 0);

/// <summary>
/// Where a mouse button went down, by window rect: the panel, a taskbar or the overflow; one of the
/// clicked app's tracked Win32 menus; the foreground window. They can overlap, because apps open
/// their menu at the forwarded click point, which is on the panel. <c>OnTaskbar</c> is a taskbar
/// that is neither the panel nor the overflow, and implies <c>OnTray</c>.
/// </summary>
public readonly record struct ClickPoint(bool OnTray, bool OnMenu = false, bool OnForeground = false, bool OnTaskbar = false);

/// <summary>What the panel does once the overflow it opened with has gone.</summary>
public enum OverflowOutcome { Keep, Reopen, Close }

/// <summary>
/// Decides when the panel closes. The panel never takes activation, so the Windows overflow it was
/// opened from keeps the foreground and stays open beside it. The panel goes away on a click outside
/// it and the tray, when something other than the tray takes the foreground, or when the overflow it
/// opened with closes while the pointer is elsewhere. With the pointer over the panel the overflow is
/// reopened once per click instead, and the panel closes if that fails. The exception is the menu of the app whose icon
/// was just clicked: a Win32 menu (tracked through its popup start/end events) or an uncaptioned
/// popup window such as Steam's. A captioned window of that app means the user picked something that
/// opened a real window, so the panel closes. Picking an item that opens nothing, such as Exit, keeps it:
/// a quitting app hands the foreground to some other window, so foreground changes shortly after an
/// item are held until it is clear whether the app went away (<see cref="ClickedAppLeft"/>).
/// </summary>
public sealed class PanelDismissal
{
    nint _openedOver;
    bool _overflowAtOpen;
    uint _clickedPid;
    int _menuDepth;
    bool _lastMenuClickOutside;
    // One reopen per click inside the panel, so an overflow that keeps closing can't loop.
    bool _reopenArmed;
    bool _reopenPending;
    bool _itemChosen;
    bool _clickedLeft;
    bool _closeHeld;

    /// <param name="foreground">The window in front just before the panel showed.</param>
    /// <param name="overflowVisible">Whether the Windows overflow flyout was showing then.</param>
    /// <param name="atCursor">Opened by the hotkey, which has nothing to do with the overflow.</param>
    public void Opened(nint foreground, bool overflowVisible, bool atCursor)
    {
        _openedOver = foreground;
        _overflowAtOpen = overflowVisible && !atCursor;
        _reopenArmed = true;
        _reopenPending = false;
        _itemChosen = _clickedLeft = _closeHeld = false;
    }

    /// <summary>Whether the overflow closing can still close the panel, so its hide event is worth hooking.</summary>
    public bool WatchesOverflow => _overflowAtOpen;

    public void Clicked(uint pid)
    {
        _clickedPid = pid;
        _reopenArmed = true;
        _menuDepth = 0;
        _lastMenuClickOutside = false;
        _itemChosen = _clickedLeft = _closeHeld = false;
    }

    /// <summary>
    /// An item of the clicked app's menu was clicked, and nothing has yet shown whether the app is quitting.
    /// The caller ends the wait with <see cref="ExitWaitEnded"/> or <see cref="ClickedAppLeft"/>.
    /// </summary>
    public bool AwaitingExit => _itemChosen && !_clickedLeft;

    /// <summary>The clicked app's last icon has gone, as it does when the app quits.</summary>
    /// <returns>True when <paramref name="pid"/> is the clicked app and this is news.</returns>
    public bool ClickedAppLeft(uint pid)
    {
        if (!IsClickedApp(pid) || _clickedLeft) return false;
        _clickedLeft = true;
        _closeHeld = false;
        return true;
    }

    /// <returns>True when a close held during the wait still stands, because the app did not quit.</returns>
    public bool ExitWaitEnded()
    {
        var close = _closeHeld && !_clickedLeft;
        _itemChosen = _closeHeld = false;
        return close;
    }

    /// <returns>True when the menu belongs to the clicked app and is now tracked.</returns>
    public bool MenuStarted(uint pid)
    {
        if (!IsClickedApp(pid)) return false;
        if (_menuDepth == 0) _lastMenuClickOutside = false;
        _menuDepth++;
        return true;
    }

    /// <returns>True when the panel should close.</returns>
    public bool MenuEnded(uint pid, ForegroundInfo fg)
    {
        if (!IsClickedApp(pid) || _menuDepth == 0) return false;
        if (--_menuDepth > 0) return false;
        // The click that ended the menu went somewhere other than the menu and the tray.
        return _lastMenuClickOutside || CloseFor(fg);
    }

    /// <returns>True when the panel should close.</returns>
    public bool ButtonDown(ClickPoint at, ForegroundInfo fg)
    {
        // The app's menu sits above the panel, so a click on it is a menu click even where the two overlap.
        var onMenu = at.OnMenu || (at.OnForeground && IsAppPopup(fg));
        var insideTray = at.OnTray && !onMenu;
        if (insideTray) _reopenArmed = true;
        if (onMenu) _itemChosen = true;
        if (_menuDepth > 0)
        {
            _lastMenuClickOutside = !insideTray && !onMenu;
            return false;
        }
        if (!insideTray) return !IsAppPopup(fg);
        // The overflow light-dismisses on a taskbar click anyway, but only after its close animation
        // has played; closing now keeps the panel from lingering beside the empty spot.
        return at.OnTaskbar && _overflowAtOpen && !IsAppPopup(fg);
    }

    /// <returns>True when the panel should close.</returns>
    public bool ForegroundChanged(ForegroundInfo fg)
    {
        // Windows can raise an event for the window that was already in front when the panel showed.
        if (fg.Window != 0 && fg.Window == _openedOver) return false;
        _openedOver = 0;
        // Another process taking over ends any menu too, so a missed end event can't pin the panel open.
        if (fg.Pid != 0 && !fg.IsTray && !IsClickedApp(fg.Pid)) return CloseFor(fg);
        return _menuDepth == 0 && CloseFor(fg);
    }

    /// <summary>The overflow the panel opened with has gone, checked a moment after it hid.</summary>
    /// <remarks>
    /// With the pointer over the panel the overflow was most likely closed by the clicked app taking the
    /// foreground, which leaves the panel floating alone, so it is reopened. <see cref="Reopened"/> or
    /// <see cref="ReopenFailed"/> must follow a <see cref="OverflowOutcome.Reopen"/>.
    /// </remarks>
    public OverflowOutcome OverflowClosed(bool pointerOverPanel, ForegroundInfo fg)
    {
        if (!_overflowAtOpen || _reopenPending || AwaitingExit) return OverflowOutcome.Keep;
        // An app that quit from its menu closed the overflow wherever the pointer had got to.
        if ((pointerOverPanel || _clickedLeft) && _menuDepth == 0 && !IsAppPopup(fg))
        {
            // A real window of the clicked app is what the user picked, so the panel goes with the overflow.
            if (!_reopenArmed || (IsClickedApp(fg.Pid) && fg.Visible && fg.Caption))
            {
                _overflowAtOpen = false;
                return OverflowOutcome.Close;
            }
            _reopenArmed = false;
            _reopenPending = true;
            return OverflowOutcome.Reopen;
        }
        _overflowAtOpen = false;
        if (pointerOverPanel) return OverflowOutcome.Keep;
        // The clicked app taking the foreground for its menu is what closed it.
        return _menuDepth == 0 && !IsAppPopup(fg) && !(IsClickedApp(fg.Pid) && !fg.Visible) ? OverflowOutcome.Close : OverflowOutcome.Keep;
    }

    /// <summary>The overflow is showing again; it is watched as before.</summary>
    public void Reopened() => _reopenPending = false;

    /// <returns>True when the panel should close.</returns>
    public bool ReopenFailed()
    {
        _reopenPending = false;
        _overflowAtOpen = false;
        return true;
    }

    bool IsClickedApp(uint pid) => pid != 0 && pid == _clickedPid;

    bool IsAppPopup(ForegroundInfo fg) => IsClickedApp(fg.Pid) && fg.Visible && fg.Popup && !fg.Caption;

    bool CloseFor(ForegroundInfo fg)
    {
        // Once the app has quit, Windows hands the foreground on to whatever window is next.
        if (fg.Pid == 0 || fg.IsTray || _clickedLeft) return false;
        if (!IsClickedApp(fg.Pid))
        {
            if (!AwaitingExit) return true;
            _closeHeld = true;
            return false;
        }
        // Apps put their hidden tray window in the foreground before showing a menu.
        if (!fg.Visible) return false;
        return !IsAppPopup(fg);
    }
}
