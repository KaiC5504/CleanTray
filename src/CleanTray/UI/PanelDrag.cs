using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using CleanTray.Native;

namespace CleanTray.UI;

/// <summary>
/// Press and hold an icon to lift it, then drag it to another slot of the panel. Releasing inside the
/// panel drops it, releasing outside or pressing Esc cancels. Moving the pointer before the hold is up
/// leaves the press alone, so it stays an ordinary click.
/// </summary>
sealed class PanelDrag : IDisposable
{
    static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan EscapePoll = TimeSpan.FromMilliseconds(30);

    readonly Panel _items;
    readonly FrameworkElement _frame;
    readonly DispatcherTimer _hold = new() { Interval = HoldTime };
    // The panel never has keyboard focus, so Esc can only be polled, and only while a drag needs it.
    readonly DispatcherTimer _escape = new() { Interval = EscapePoll };
    Button? _pressed;
    Point _pressedAt;
    int _liftedFrom;

    /// <param name="items">The panel holding the icon buttons.</param>
    /// <param name="frame">The window, whose bounds tell inside from outside.</param>
    public PanelDrag(Panel items, FrameworkElement frame)
    {
        _items = items;
        _frame = frame;
        _hold.Tick += (_, _) => Lift();
        _escape.Tick += (_, _) =>
        {
            if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0) Cancel();
        };
        // Tunnelling events pass through the panel for whichever element holds the capture.
        _items.PreviewMouseMove += OnMove;
        _items.PreviewMouseLeftButtonUp += OnUp;
        _items.LostMouseCapture += OnLostCapture;
    }

    /// <summary>The icon is lifted and following the pointer.</summary>
    public event Action<Button>? Lifted;

    /// <summary>The drag is over. True when the icon was dropped inside the panel in a new place.</summary>
    public event Action<Button, bool>? Ended;

    /// <summary>The lifted icon, or null when nothing is being dragged.</summary>
    public Button? Dragged { get; private set; }

    public void Press(Button button, MouseEventArgs e)
    {
        _pressed = button;
        _pressedAt = e.GetPosition(_items);
        _hold.Stop();
        _hold.Start();
    }

    public void Cancel() => End(drop: false);

    public void Dispose()
    {
        _hold.Stop();
        _escape.Stop();
        _items.PreviewMouseMove -= OnMove;
        _items.PreviewMouseLeftButtonUp -= OnUp;
        _items.LostMouseCapture -= OnLostCapture;
        Dragged = null;
        _pressed = null;
    }

    void Lift()
    {
        _hold.Stop();
        var button = _pressed;
        _pressed = null;
        if (button is null || Mouse.LeftButton != MouseButtonState.Pressed || !_items.Children.Contains(button)) return;

        // Taking the capture off the button cancels its press, so the release can't be a click. The
        // button's own LostMouseCapture passes through the panel, hence Dragged is set only after.
        if (!_items.CaptureMouse()) return;
        Dragged = button;
        _liftedFrom = _items.Children.IndexOf(button);
        _escape.Start();
        Lifted?.Invoke(button);
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(_items);
        if (Dragged is not { } button)
        {
            if (_pressed is not null && (Math.Abs(at.X - _pressedAt.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(at.Y - _pressedAt.Y) > SystemParameters.MinimumVerticalDragDistance))
                Forget();
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) Cancel();
        else Follow(button, at);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (Dragged is null) Forget();
        else End(drop: Inside(e));
    }

    void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (Dragged is not null && e.OriginalSource == _items) Cancel();
    }

    void Forget()
    {
        _hold.Stop();
        _pressed = null;
    }

    // Takes the slot of the icon under the pointer; the icons in between slide one place the other way.
    void Follow(Button button, Point at)
    {
        var children = _items.Children;
        var from = children.IndexOf(button);
        var to = -1;
        var nearest = double.MaxValue;
        for (var i = 0; i < children.Count; i++)
        {
            var slot = LayoutInformation.GetLayoutSlot((FrameworkElement)children[i]);
            var dx = Math.Max(Math.Max(slot.Left - at.X, at.X - slot.Right), 0);
            var dy = Math.Max(Math.Max(slot.Top - at.Y, at.Y - slot.Bottom), 0);
            if (dx * dx + dy * dy >= nearest) continue;
            nearest = dx * dx + dy * dy;
            to = i;
        }
        if (to < 0 || to == from) return;

        children.RemoveAt(from);
        children.Insert(to, button);
        // The next move is placed against the new layout, not the one before this move.
        _items.UpdateLayout();
    }

    bool Inside(MouseEventArgs e)
    {
        var at = e.GetPosition(_frame);
        return at.X >= 0 && at.Y >= 0 && at.X < _frame.ActualWidth && at.Y < _frame.ActualHeight;
    }

    void End(bool drop)
    {
        if (Dragged is not { } button) return;
        var moved = _items.Children.IndexOf(button) != _liftedFrom;
        Dragged = null;
        _escape.Stop();
        _items.ReleaseMouseCapture();
        Ended?.Invoke(button, drop && moved);
    }
}
