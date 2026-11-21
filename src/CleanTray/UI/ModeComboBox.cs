using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace CleanTray.UI;

/// <summary>
/// A ComboBox whose choice UI Automation can read and set through the Value pattern. A plain WPF
/// ComboBox only lists its items while the drop-down is open, and the drop-down closes as soon as
/// a background automation client hands focus back, so its choice could not be set without the mouse.
/// </summary>
sealed class ModeComboBox : ComboBox
{
    public ModeComboBox() => SetResourceReference(StyleProperty, typeof(ComboBox));

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        var peer = UIElementAutomationPeer.FromElement(this);
        if (peer is null) return;
        var old = e.RemovedItems.Count > 0 ? e.RemovedItems[0]?.ToString() ?? "" : "";
        peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, old, SelectedItem?.ToString() ?? "");
    }

    sealed class Peer(ModeComboBox owner) : ComboBoxAutomationPeer(owner), IValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        string IValueProvider.Value => owner.SelectedItem?.ToString() ?? "";

        bool IValueProvider.IsReadOnly => !owner.IsEnabled;

        void IValueProvider.SetValue(string value)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            for (var i = 0; i < owner.Items.Count; i++)
            {
                if (!string.Equals(owner.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase)) continue;
                owner.SetCurrentValue(SelectedIndexProperty, i);
                return;
            }
            throw new ArgumentException("Not one of the choices.", nameof(value));
        }
    }
}
