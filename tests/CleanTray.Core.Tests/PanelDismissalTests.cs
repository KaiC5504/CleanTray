namespace CleanTray.Core.Tests;

public sealed class PanelDismissalTests
{
    const uint App = 4242;
    const uint Other = 77;
    const uint Shell = 19;

    static readonly ForegroundInfo None = default;
    static readonly ForegroundInfo Overflow = new(Shell, IsTray: true, Visible: true, Popup: true, Caption: false, Window: 0x10);
    static readonly ForegroundInfo Taskbar = new(Shell, IsTray: true, Visible: true, Popup: true, Caption: false, Window: 0x11);
    static readonly ForegroundInfo Desktop = new(Shell, IsTray: false, Visible: true, Popup: true, Caption: false, Window: 0x12);
    static readonly ForegroundInfo OtherApp = new(Other, IsTray: false, Visible: true, Popup: false, Caption: true, Window: 0x20);
    static readonly ForegroundInfo AppHiddenOwner = new(App, IsTray: false, Visible: false, Popup: false, Caption: true, Window: 0x30);
    static readonly ForegroundInfo AppPopupMenu = new(App, IsTray: false, Visible: true, Popup: true, Caption: false, Window: 0x40);
    static readonly ForegroundInfo AppMainWindow = new(App, IsTray: false, Visible: true, Popup: false, Caption: true, Window: 0x50);
    static readonly ForegroundInfo AppHiddenPopup = AppPopupMenu with { Visible = false };

    static readonly ClickPoint OnTray = new(OnTray: true);
    static readonly ClickPoint Elsewhere = new(OnTray: false);
    static readonly ClickPoint OnTaskbar = new(OnTray: true, OnTaskbar: true);

    static PanelDismissal Open(bool overflow = false)
    {
        var d = new PanelDismissal();
        d.Opened(overflow ? Overflow.Window : OtherApp.Window, overflowVisible: overflow, atCursor: false);
        return d;
    }

    static PanelDismissal AfterClick(bool overflow = false)
    {
        var d = Open(overflow);
        d.Clicked(App);
        return d;
    }

    [Fact]
    public void Foreground_TrayOrNoneKeepsOpen()
    {
        var d = Open();
        Assert.False(d.ForegroundChanged(Overflow));
        Assert.False(d.ForegroundChanged(Taskbar));
        Assert.False(d.ForegroundChanged(None));
    }

    [Fact]
    public void Foreground_AnyOtherWindowClosesWhenNothingWasClicked()
    {
        Assert.True(Open(overflow: true).ForegroundChanged(OtherApp));
        Assert.True(Open(overflow: true).ForegroundChanged(Desktop));
        Assert.True(Open().ForegroundChanged(AppHiddenOwner));
    }

    [Fact]
    public void Foreground_EventForTheWindowAlreadyInFrontAtOpenIsNoChange()
    {
        var d = Open();
        Assert.False(d.ForegroundChanged(OtherApp));
        Assert.True(d.ForegroundChanged(OtherApp with { Window = 0x200 }));
    }

    [Fact]
    public void Foreground_ComingBackToTheOpeningWindowIsAChange()
    {
        var d = Open();
        Assert.False(d.ForegroundChanged(Taskbar));
        Assert.True(d.ForegroundChanged(OtherApp));
    }

    [Fact]
    public void Click_OutsideTheTrayClosesAndInsideKeeps()
    {
        var d = Open(overflow: true);
        Assert.False(d.ButtonDown(OnTray, Overflow));
        Assert.True(d.ButtonDown(Elsewhere, Overflow));
    }

    [Fact]
    public void TaskbarClick_ClosesAtOnceWhenOpenedOverTheOverflow()
    {
        Assert.True(Open(overflow: true).ButtonDown(OnTaskbar, Overflow));
        Assert.True(Open(overflow: true).ButtonDown(OnTaskbar, Taskbar));
        Assert.True(AfterClick(overflow: true).ButtonDown(OnTaskbar, AppHiddenOwner));
    }

    [Fact]
    public void TaskbarClick_KeepsOpenWhenNotOpenedOverTheOverflow()
    {
        Assert.False(Open().ButtonDown(OnTaskbar, Taskbar));
        var atCursor = new PanelDismissal();
        atCursor.Opened(Overflow.Window, overflowVisible: true, atCursor: true);
        Assert.False(atCursor.ButtonDown(OnTaskbar, Taskbar));
    }

    [Fact]
    public void TaskbarClick_KeepsOpenOnceTheOverflowHasGone()
    {
        var d = Open(overflow: true);
        d.OverflowClosed(pointerOverPanel: true, Taskbar);
        d.ReopenFailed();
        Assert.False(d.ButtonDown(OnTaskbar, Taskbar));
    }

    [Fact]
    public void Click_OnTheOverflowOrThePanelKeepsOpenEvenOverTheOverflow()
    {
        var d = Open(overflow: true);
        Assert.False(d.ButtonDown(OnTray, Overflow));
        Assert.False(d.ButtonDown(OnTray, OtherApp));
    }

    [Fact]
    public void TaskbarClick_DuringATrackedMenuFollowsTheMenuRules()
    {
        var d = AfterClick(overflow: true);
        d.MenuStarted(App);
        Assert.False(d.ButtonDown(OnTaskbar, AppHiddenOwner));
        Assert.False(d.MenuEnded(App, AppHiddenOwner));
    }

    [Fact]
    public void TaskbarClick_OverlappingTheAppsMenuIsAMenuClick()
    {
        var d = AfterClick(overflow: true);
        d.MenuStarted(App);
        Assert.False(d.ButtonDown(OnTaskbar with { OnMenu = true }, AppHiddenOwner));

        var popup = AfterClick(overflow: true);
        popup.ForegroundChanged(AppPopupMenu);
        Assert.False(popup.ButtonDown(OnTaskbar with { OnForeground = true }, AppPopupMenu));
        Assert.False(popup.ButtonDown(OnTaskbar, AppPopupMenu));
    }

    [Fact]
    public void ClickedApp_UncaptionedPopupKeepsOpen()
    {
        var d = AfterClick();
        Assert.False(d.ForegroundChanged(AppPopupMenu));
        // Choosing an item in the app's own popup menu is a click outside the tray.
        Assert.False(d.ButtonDown(Elsewhere, AppPopupMenu));
    }

    [Fact]
    public void ClickedApp_CaptionedWindowCloses()
    {
        var d = AfterClick();
        Assert.False(d.ForegroundChanged(AppPopupMenu));
        Assert.True(d.ForegroundChanged(AppMainWindow));
    }

    [Fact]
    public void ClickedApp_HiddenMenuOwnerKeepsOpenUntilAnOutsideClick()
    {
        var d = AfterClick();
        Assert.False(d.ForegroundChanged(AppHiddenOwner));
        Assert.True(d.ButtonDown(Elsewhere, AppHiddenOwner));
    }

    [Fact]
    public void ClickedApp_PopupThatHidItselfNoLongerHoldsThePanel()
    {
        var d = AfterClick();
        Assert.False(d.ForegroundChanged(AppPopupMenu));
        Assert.True(d.ButtonDown(Elsewhere, AppHiddenPopup));
    }

    [Fact]
    public void ClickedApp_PopupOfAnotherProcessCloses()
    {
        var d = AfterClick();
        Assert.True(d.ForegroundChanged(AppPopupMenu with { Pid = Other }));
    }

    [Fact]
    public void Win32Menu_ClicksDuringTheMenuNeverClose()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        Assert.False(d.ButtonDown(Elsewhere, AppHiddenOwner));
        Assert.False(d.ForegroundChanged(AppHiddenOwner));
    }

    [Fact]
    public void Win32Menu_ClickingOutsideTheMenuClosesWhenItEnds()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        d.ButtonDown(Elsewhere, AppHiddenOwner);
        Assert.True(d.MenuEnded(App, AppHiddenOwner));
    }

    [Fact]
    public void Win32Menu_DismissedWithoutAClickKeepsOpen()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        Assert.False(d.MenuEnded(App, AppHiddenOwner));
        Assert.True(d.ButtonDown(Elsewhere, AppHiddenOwner));
    }

    [Fact]
    public void Win32Menu_DismissedByClickingThePanelKeepsOpen()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        d.ButtonDown(Elsewhere, AppHiddenOwner);
        d.ButtonDown(OnTray, AppHiddenOwner);
        Assert.False(d.MenuEnded(App, AppHiddenOwner));
    }

    [Fact]
    public void Win32Menu_SubmenusEndTogether()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        d.MenuStarted(App);
        d.ButtonDown(Elsewhere, AppHiddenOwner);
        Assert.False(d.MenuEnded(App, AppHiddenOwner));
        Assert.True(d.MenuEnded(App, AppHiddenOwner));
    }

    [Fact]
    public void Win32Menu_EndingIntoACaptionedWindowCloses()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        Assert.True(d.MenuEnded(App, AppMainWindow));
    }

    [Fact]
    public void Win32Menu_OtherProcessesMenusAreIgnored()
    {
        var d = AfterClick();
        d.MenuStarted(Other);
        Assert.True(d.ButtonDown(Elsewhere, AppHiddenOwner));
        Assert.False(d.MenuEnded(Other, AppHiddenOwner));
    }

    [Fact]
    public void Win32Menu_UnmatchedEndIsIgnored()
    {
        var d = AfterClick();
        Assert.False(d.MenuEnded(App, AppHiddenOwner));
    }

    [Fact]
    public void Win32Menu_AnotherProcessTakingTheForegroundCloses()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        Assert.True(d.ForegroundChanged(OtherApp with { Window = 0x21 }));
    }

    [Fact]
    public void NoClick_MenusAreIgnored()
    {
        var d = Open();
        d.MenuStarted(0);
        Assert.True(d.ButtonDown(Elsewhere, None));
    }

    [Fact]
    public void Clicked_AnotherIconStartsOver()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        d.Clicked(Other);
        Assert.True(d.ForegroundChanged(AppPopupMenu));
        Assert.False(d.ForegroundChanged(AppPopupMenu with { Pid = Other, Window = 0x41 }));
    }

    [Fact]
    public void OverflowClosed_PointerElsewhereCloses()
    {
        Assert.Equal(OverflowOutcome.Close, Open(overflow: true).OverflowClosed(pointerOverPanel: false, Taskbar));
    }

    [Fact]
    public void OverflowClosed_PointerOverThePanelAsksToReopenOnce()
    {
        var d = Open(overflow: true);
        Assert.Equal(OverflowOutcome.Reopen, d.OverflowClosed(pointerOverPanel: true, Taskbar));
        d.Reopened();
        Assert.True(d.WatchesOverflow);
        Assert.Equal(OverflowOutcome.Close, d.OverflowClosed(pointerOverPanel: true, Taskbar));
    }

    [Fact]
    public void OverflowClosed_ASecondCloseWithoutANewClickClosesEvenAwayFromThePanel()
    {
        var d = Open(overflow: true);
        d.OverflowClosed(pointerOverPanel: true, Taskbar);
        d.Reopened();
        Assert.Equal(OverflowOutcome.Close, d.OverflowClosed(pointerOverPanel: false, Taskbar));
    }

    [Fact]
    public void OverflowClosed_ANewClickInThePanelRearmsTheReopen()
    {
        var d = Open(overflow: true);
        d.OverflowClosed(pointerOverPanel: true, Taskbar);
        d.Reopened();
        d.ButtonDown(OnTray, Overflow);
        Assert.Equal(OverflowOutcome.Reopen, d.OverflowClosed(pointerOverPanel: true, Taskbar));
    }

    [Fact]
    public void OverflowClosed_ClickingAnIconRearmsTheReopen()
    {
        var d = AfterClick(overflow: true);
        d.OverflowClosed(pointerOverPanel: true, Taskbar);
        d.Reopened();
        d.Clicked(App);
        Assert.Equal(OverflowOutcome.Reopen, d.OverflowClosed(pointerOverPanel: true, Taskbar));
    }

    [Fact]
    public void OverflowClosed_AClickThatDismissesAMenuInThePanelRearmsTheReopen()
    {
        var d = AfterClick(overflow: true);
        d.OverflowClosed(pointerOverPanel: true, Taskbar);
        d.Reopened();
        d.MenuStarted(App);
        Assert.False(d.ButtonDown(OnTray, AppHiddenOwner));
        d.MenuEnded(App, AppHiddenOwner);
        Assert.Equal(OverflowOutcome.Reopen, d.OverflowClosed(pointerOverPanel: true, AppHiddenOwner));
    }

    [Fact]
    public void OverflowClosed_AClickOutsideTheTrayDoesNotRearmTheReopen()
    {
        var d = Open(overflow: true);
        d.OverflowClosed(pointerOverPanel: true, Taskbar);
        d.Reopened();
        d.ButtonDown(Elsewhere, AppPopupMenu);
        Assert.Equal(OverflowOutcome.Close, d.OverflowClosed(pointerOverPanel: true, Taskbar));
    }

    [Fact]
    public void OverflowClosed_WhileAReopenIsPendingKeepsOpen()
    {
        var d = Open(overflow: true);
        d.OverflowClosed(pointerOverPanel: true, Taskbar);
        Assert.Equal(OverflowOutcome.Keep, d.OverflowClosed(pointerOverPanel: true, Taskbar));
        d.Reopened();
        Assert.Equal(OverflowOutcome.Close, d.OverflowClosed(pointerOverPanel: true, Taskbar));
    }

    [Fact]
    public void ReopenFailed_ClosesThePanelAndStopsWatching()
    {
        var d = Open(overflow: true);
        d.OverflowClosed(pointerOverPanel: true, Taskbar);
        Assert.True(d.ReopenFailed());
        Assert.False(d.WatchesOverflow);
    }

    [Fact]
    public void OverflowClosed_DuringAWin32MenuWithThePointerOverThePanelDoesNotReopen()
    {
        var d = AfterClick(overflow: true);
        d.MenuStarted(App);
        Assert.Equal(OverflowOutcome.Keep, d.OverflowClosed(pointerOverPanel: true, AppHiddenOwner));
    }

    [Fact]
    public void OverflowClosed_UnderTheClickedAppsPopupDoesNotReopen()
    {
        var d = AfterClick(overflow: true);
        Assert.Equal(OverflowOutcome.Keep, d.OverflowClosed(pointerOverPanel: true, AppPopupMenu));
    }

    [Fact]
    public void OverflowClosed_AfterTheClickedAppTookTheForegroundReopens()
    {
        var d = AfterClick(overflow: true);
        Assert.Equal(OverflowOutcome.Reopen, d.OverflowClosed(pointerOverPanel: true, AppHiddenOwner));
    }

    [Fact]
    public void OverflowClosed_IntoTheClickedAppsMainWindowWithThePointerOverThePanelCloses()
    {
        var d = AfterClick(overflow: true);
        Assert.Equal(OverflowOutcome.Close, d.OverflowClosed(pointerOverPanel: true, AppMainWindow));
        Assert.False(d.WatchesOverflow);
    }

    [Fact]
    public void OverflowClosed_NeverReopensForAPanelNotOpenedOverTheOverflow()
    {
        Assert.Equal(OverflowOutcome.Keep, Open().OverflowClosed(pointerOverPanel: true, Taskbar));
        var atCursor = new PanelDismissal();
        atCursor.Opened(Overflow.Window, overflowVisible: true, atCursor: true);
        Assert.Equal(OverflowOutcome.Keep, atCursor.OverflowClosed(pointerOverPanel: true, Taskbar));
    }

    [Fact]
    public void OverflowClosed_IgnoredWhenThePanelDidNotOpenWithIt()
    {
        Assert.Equal(OverflowOutcome.Keep, Open().OverflowClosed(pointerOverPanel: false, Taskbar));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverflowClosed_ByTheClickedAppsMenuKeepsOpen(bool popup)
    {
        var d = AfterClick(overflow: true);
        Assert.Equal(OverflowOutcome.Keep, d.OverflowClosed(pointerOverPanel: false, popup ? AppPopupMenu : AppHiddenOwner));
    }

    [Fact]
    public void OverflowClosed_DuringAWin32MenuKeepsOpen()
    {
        var d = AfterClick(overflow: true);
        d.MenuStarted(App);
        Assert.Equal(OverflowOutcome.Keep, d.OverflowClosed(pointerOverPanel: false, AppHiddenOwner));
    }

    [Fact]
    public void OverflowClosed_IntoTheClickedAppsMainWindowCloses()
    {
        var d = AfterClick(overflow: true);
        Assert.Equal(OverflowOutcome.Close, d.OverflowClosed(pointerOverPanel: false, AppMainWindow));
    }

    [Fact]
    public void OverflowClosed_NotAppliedForAHotkeyOpen()
    {
        var d = new PanelDismissal();
        d.Opened(Overflow.Window, overflowVisible: true, atCursor: true);
        Assert.False(d.WatchesOverflow);
        Assert.Equal(OverflowOutcome.Keep, d.OverflowClosed(pointerOverPanel: false, Taskbar));
    }

    [Fact]
    public void WatchesOverflow_OnlyWhenOpenedFromTheOverflow()
    {
        Assert.True(Open(overflow: true).WatchesOverflow);
        Assert.False(Open().WatchesOverflow);
    }

    [Fact]
    public void MenuStarted_TracksOnlyTheClickedAppsMenus()
    {
        var d = AfterClick();
        Assert.False(d.MenuStarted(Other));
        Assert.False(d.MenuStarted(0));
        Assert.True(d.MenuStarted(App));
        Assert.False(Open().MenuStarted(App));
    }

    [Fact]
    public void Win32Menu_ItemOverlappingThePanelCountsAsAMenuClick()
    {
        // Apps open their menu at the forwarded click point, which is the panel button's centre.
        var d = AfterClick();
        d.MenuStarted(App);
        Assert.False(d.ButtonDown(new ClickPoint(OnTray: true, OnMenu: true), AppHiddenOwner));
        Assert.True(d.AwaitingExit);
        Assert.False(d.MenuEnded(App, AppHiddenOwner));
    }

    [Fact]
    public void AppPopup_ClickOverlappingThePanelIsAMenuClickAndKeepsOpen()
    {
        var d = AfterClick();
        d.ForegroundChanged(AppPopupMenu);
        Assert.False(d.ButtonDown(new ClickPoint(OnTray: true, OnForeground: true), AppPopupMenu));
        Assert.False(d.ButtonDown(new ClickPoint(OnTray: false, OnForeground: true), AppPopupMenu));
    }

    [Fact]
    public void Click_OnThePanelAboveSomeOtherForegroundWindowIsInside()
    {
        var d = AfterClick();
        Assert.False(d.ButtonDown(new ClickPoint(OnTray: true, OnForeground: true), OtherApp));
        Assert.False(d.ButtonDown(new ClickPoint(OnTray: true, OnForeground: true), AppMainWindow));
    }

    [Fact]
    public void Win32Menu_ClickOnThePanelAboveTheHiddenOwnerIsStillInside()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        d.ButtonDown(new ClickPoint(OnTray: true, OnForeground: true), AppHiddenOwner);
        Assert.False(d.MenuEnded(App, AppHiddenOwner));
    }

    [Fact]
    public void ClickedApp_CaptionedPopupCloses()
    {
        var d = AfterClick();
        Assert.True(d.ForegroundChanged(AppPopupMenu with { Caption = true, Window = 0x42 }));
    }

    [Fact]
    public void Win32Menu_NoForegroundDuringTheMenuKeepsOpen()
    {
        var d = AfterClick();
        d.MenuStarted(App);
        Assert.False(d.ForegroundChanged(None));
        Assert.False(d.MenuEnded(App, None));
    }

    // Not the window the panel opened over, which a foreground event may repeat.
    static readonly ForegroundInfo NextApp = OtherApp with { Window = 0x21 };
    static readonly ClickPoint OnMenuItem = new(OnTray: false, OnMenu: true);

    static PanelDismissal AfterMenuItem(bool overflow = false)
    {
        var d = AfterClick(overflow);
        d.MenuStarted(App);
        Assert.False(d.ButtonDown(OnMenuItem, AppHiddenOwner));
        return d;
    }

    [Fact]
    public void MenuItem_ChoosingOneKeepsThePanelOpen()
    {
        var d = AfterMenuItem();
        Assert.False(d.MenuEnded(App, AppHiddenOwner));
        Assert.False(d.ExitWaitEnded());
        Assert.False(d.AwaitingExit);
    }

    [Fact]
    public void MenuItem_OpeningTheAppsWindowClosesAtOnce()
    {
        Assert.True(AfterMenuItem().MenuEnded(App, AppMainWindow));
        var d = AfterMenuItem();
        d.MenuEnded(App, AppHiddenOwner);
        Assert.True(d.ForegroundChanged(AppMainWindow));
    }

    [Fact]
    public void MenuItem_AnotherAppTakingTheForegroundClosesOnceTheWaitEnds()
    {
        var d = AfterMenuItem();
        d.MenuEnded(App, AppHiddenOwner);
        Assert.False(d.ForegroundChanged(NextApp));
        Assert.True(d.ExitWaitEnded());
    }

    [Fact]
    public void MenuItem_MenuEndingIntoAnotherAppClosesOnceTheWaitEnds()
    {
        var d = AfterMenuItem();
        Assert.False(d.MenuEnded(App, NextApp));
        Assert.True(d.ExitWaitEnded());
    }

    [Fact]
    public void MenuItem_AppQuittingAfterTheForegroundMovedOnKeepsOpen()
    {
        var d = AfterMenuItem();
        d.MenuEnded(App, AppHiddenOwner);
        Assert.False(d.ForegroundChanged(NextApp));
        Assert.True(d.ClickedAppLeft(App));
        Assert.False(d.AwaitingExit);
        Assert.False(d.ExitWaitEnded());
    }

    [Fact]
    public void MenuItem_ForegroundMovingOnAfterTheAppQuitKeepsOpen()
    {
        var d = AfterMenuItem();
        d.MenuEnded(App, AppHiddenOwner);
        Assert.True(d.ClickedAppLeft(App));
        Assert.False(d.ForegroundChanged(NextApp));
        Assert.False(d.ForegroundChanged(Desktop));
        Assert.False(d.ExitWaitEnded());
    }

    [Fact]
    public void MenuItem_OutsideClickStillCloses()
    {
        var d = AfterMenuItem();
        d.MenuEnded(App, AppHiddenOwner);
        d.ClickedAppLeft(App);
        Assert.True(d.ButtonDown(Elsewhere, NextApp));
    }

    [Fact]
    public void MenuItem_ChosenFromTheAppsPopupWindowWaitsToo()
    {
        var d = AfterClick();
        d.ForegroundChanged(AppPopupMenu);
        Assert.False(d.ButtonDown(new ClickPoint(OnTray: false, OnForeground: true), AppPopupMenu));
        Assert.True(d.AwaitingExit);
        Assert.False(d.ForegroundChanged(NextApp));
        Assert.True(d.ClickedAppLeft(App));
        Assert.False(d.ExitWaitEnded());
    }

    [Fact]
    public void ClickedAppLeft_AnotherProcessIsIgnored()
    {
        var d = AfterClick();
        Assert.False(d.ClickedAppLeft(Other));
        Assert.True(d.ForegroundChanged(NextApp));
    }

    [Fact]
    public void ClickedAppLeft_ClickingAnotherIconStartsOver()
    {
        var d = AfterMenuItem();
        d.ClickedAppLeft(App);
        d.Clicked(Other);
        Assert.False(d.AwaitingExit);
        Assert.True(d.ForegroundChanged(Desktop));
    }

    [Fact]
    public void OverflowClosed_WhileWaitingIsLeftForTheWaitsEnd()
    {
        var d = AfterMenuItem(overflow: true);
        d.MenuEnded(App, AppHiddenOwner);
        Assert.Equal(OverflowOutcome.Keep, d.OverflowClosed(pointerOverPanel: false, NextApp));
        Assert.True(d.WatchesOverflow);
        d.ExitWaitEnded();
        Assert.Equal(OverflowOutcome.Close, d.OverflowClosed(pointerOverPanel: false, NextApp));
    }

    [Fact]
    public void OverflowClosed_AfterTheClickedAppQuitReopensWhereverThePointerIs()
    {
        var d = AfterMenuItem(overflow: true);
        d.MenuEnded(App, AppHiddenOwner);
        d.ClickedAppLeft(App);
        Assert.Equal(OverflowOutcome.Reopen, d.OverflowClosed(pointerOverPanel: false, NextApp));
    }
}
