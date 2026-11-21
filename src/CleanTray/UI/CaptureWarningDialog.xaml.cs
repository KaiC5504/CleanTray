using System.Windows;

namespace CleanTray.UI;

public partial class CaptureWarningDialog : Window
{
    public CaptureWarningDialog() => InitializeComponent();

    void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
