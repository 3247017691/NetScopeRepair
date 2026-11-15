using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Views;

public partial class QuickRepairView : UserControl
{
    private QuickRepairViewModel? _viewModel;

    public QuickRepairView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _viewModel = e.NewValue as QuickRepairViewModel;
        if (_viewModel is not null) _viewModel.LogLines.CollectionChanged += OnLogChanged;
    }

    private void Detach()
    {
        if (_viewModel is not null) _viewModel.LogLines.CollectionChanged -= OnLogChanged;
        _viewModel = null;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // Scroll after the new line is measured so the latest output stays visible.
        Dispatcher.BeginInvoke(new Action(() => LogScroll.ScrollToEnd()), DispatcherPriority.Loaded);
    }
}
