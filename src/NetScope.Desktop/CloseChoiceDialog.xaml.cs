using System.Windows;

namespace NetScope.Desktop;

public partial class CloseChoiceDialog : Window
{
    public CloseChoiceDialog()
    {
        InitializeComponent();
    }

    /// <summary>True when the user chose to stop and close, false when hiding to the tray.</summary>
    public bool ExitSelected { get; private set; }

    public bool Remember { get; private set; }

    private void OnHide(object sender, RoutedEventArgs e) => Finish(exit: false);

    private void OnExit(object sender, RoutedEventArgs e) => Finish(exit: true);

    private void Finish(bool exit)
    {
        ExitSelected = exit;
        Remember = RememberCheck.IsChecked == true;
        DialogResult = true;
    }
}
