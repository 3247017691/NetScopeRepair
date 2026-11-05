using System.ComponentModel;
using System.Windows;

namespace NetScope.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Set by the tray/exit flow so the close button can hide instead of exiting.</summary>
    public bool AllowRealClose { get; set; }

    public event EventHandler? HideRequested;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowRealClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }
}
