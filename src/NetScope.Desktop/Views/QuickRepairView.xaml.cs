using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NetScope.Desktop.Controls;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Views;

public partial class QuickRepairView : UserControl
{
    private QuickRepairViewModel? _viewModel;
    private RepairStage _lastStage;
    private string? _lastVerdict;

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
        if (_viewModel is null) return;
        _viewModel.LogLines.CollectionChanged += OnLogChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _lastStage = _viewModel.Stage;
        _lastVerdict = _viewModel.VerdictText;
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.LogLines.CollectionChanged -= OnLogChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;
        switch (e.PropertyName)
        {
            case nameof(QuickRepairViewModel.Stage):
            {
                var stage = _viewModel.Stage;
                if (stage == _lastStage) break;
                _lastStage = stage;
                FadeCurrentBadge(stage);
                break;
            }
            case nameof(QuickRepairViewModel.VerdictText):
            {
                var verdict = _viewModel.VerdictText;
                if (verdict == _lastVerdict) break;
                _lastVerdict = verdict;
                Motion.FadeIn(VerdictBlock, 0.4, fromY: 6);
                break;
            }
            case nameof(QuickRepairViewModel.HasPlan):
                if (_viewModel.HasPlan) Motion.FadeIn(PairingPanel, 0.4, fromY: 6);
                break;
        }
    }

    private void FadeCurrentBadge(RepairStage stage)
    {
        var badge = stage switch
        {
            RepairStage.Inspect => Badge1,
            RepairStage.Review => Badge2,
            RepairStage.Execute => Badge3,
            RepairStage.Verify => Badge4,
            _ => null,
        };
        if (badge is not null)
            Motion.FadeIn(badge, 0.25, fromOpacity: 0.4, easing: new QuadraticEase { EasingMode = EasingMode.EaseOut });
    }

    private void OnStepRowLoaded(object sender, RoutedEventArgs e) => Motion.FadeIn((FrameworkElement)sender, 0.16);

    private void OnLogLineLoaded(object sender, RoutedEventArgs e) => Motion.FadeIn((FrameworkElement)sender, 0.14);

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // Scroll after the new line is measured so the latest output stays visible.
        Dispatcher.BeginInvoke(new Action(() => LogScroll.ScrollToEnd()), DispatcherPriority.Loaded);
    }
}
