using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NetScope.Desktop.Controls;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Views;

public partial class ShellView : UserControl
{
    private MainViewModel? _viewModel;
    private Ellipse? _liveDot;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Detach();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The dot lives in the repair-entry Button's ControlTemplate, so resolve it by template name
        // once the template is applied rather than expecting a root-level generated field.
        _liveDot ??= RepairEntryButton.Template?.FindName("LiveDot", RepairEntryButton) as Ellipse;
        UpdatePulse();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void Detach()
    {
        StopPulse();
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedSection):
                // Content swaps instantly via the binding; the fade is a short overlay, never a delay.
                // The initial selection happens before this subscription, so first load never fades.
                Motion.FadeIn(PageHost, 0.14);
                break;
            case nameof(MainViewModel.StateText):
                UpdatePulse();
                break;
        }
    }

    private void UpdatePulse()
    {
        StopPulse();
        // WHY string coupling: "观察中" is the shell's own display string for the live observing state
        // (MainViewModel state map). Demo shows "演示" and paused "已暂停", so neither pulses.
        if (_liveDot is null || !Motion.Enabled || !IsLoaded || _viewModel?.StateText != "观察中") return;
        var anim = new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromSeconds(1.2)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        _liveDot.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void StopPulse()
    {
        if (_liveDot is null) return;
        _liveDot.BeginAnimation(UIElement.OpacityProperty, null);
        _liveDot.Opacity = 1;
    }
}
