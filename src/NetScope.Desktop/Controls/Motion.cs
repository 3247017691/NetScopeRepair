using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NetScope.Desktop.Controls;

/// <summary>
/// Shared motion gate for the signal-instrument UI. Every animation honors the OS "show animations"
/// setting (<see cref="SystemParameters.ClientAreaAnimation"/>) and only runs once the element is
/// presented (<see cref="FrameworkElement.IsLoaded"/>), so off-screen renders and reduced-motion stay
/// static with nothing hidden. The gate is read at each start; a mid-session OS toggle needs a restart
/// (same limitation as <see cref="Spinner"/>). Loops are stopped by their owners on Unloaded/exit.
/// </summary>
internal static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Reveal an element: opacity <paramref name="fromOpacity"/>→1 and optional TranslateY
    /// <paramref name="fromY"/>→0. Gate off or not loaded leaves the visible base (Opacity 1) untouched.
    /// On completion the clock is removed and the base restored so re-entry starts clean.
    /// </summary>
    public static void FadeIn(FrameworkElement e, double seconds, double fromY = 0, double fromOpacity = 0,
        IEasingFunction? easing = null)
    {
        e.BeginAnimation(UIElement.OpacityProperty, null);
        e.Opacity = 1;
        if (!Enabled || !e.IsLoaded) return;

        easing ??= new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromSeconds(seconds));
        var fade = new DoubleAnimation(fromOpacity, 1, duration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
        };
        e.BeginAnimation(UIElement.OpacityProperty, fade);

        if (fromY == 0) return;
        var translate = EnsureTranslate(e);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = 0;
        var slide = new DoubleAnimation(fromY, 0, duration) { EasingFunction = easing };
        slide.Completed += (_, _) =>
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
        };
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Remove an animation clock from a property (caller resets the base value).</summary>
    public static void Stop(FrameworkElement e, DependencyProperty property) => e.BeginAnimation(property, null);

    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverLiftChanged));

    public static bool GetHoverLift(DependencyObject obj) => (bool)obj.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject obj, bool value) => obj.SetValue(HoverLiftProperty, value);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.MouseEnter += OnLiftEnter;
            fe.MouseLeave += OnLiftLeave;
            fe.PreviewMouseDown += OnLiftDown;
            fe.PreviewMouseUp += OnLiftUp;
            fe.Unloaded += OnLiftUnloaded;
        }
        else
        {
            fe.MouseEnter -= OnLiftEnter;
            fe.MouseLeave -= OnLiftLeave;
            fe.PreviewMouseDown -= OnLiftDown;
            fe.PreviewMouseUp -= OnLiftUp;
            fe.Unloaded -= OnLiftUnloaded;
            ResetLift(fe);
        }
    }

    private static void OnLiftEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && Enabled && fe.IsEnabled) LiftTo(fe, -1, 0.14);
    }

    private static void OnLiftLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe) LiftTo(fe, 0, 0.08);
    }

    private static void OnLiftDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe) LiftTo(fe, 0, 0.08);
    }

    private static void OnLiftUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && Enabled && fe.IsEnabled && fe.IsMouseOver) LiftTo(fe, -1, 0.14);
    }

    private static void OnLiftUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) ResetLift(fe);
    }

    private static void LiftTo(FrameworkElement fe, double y, double seconds)
    {
        if (!Enabled) { ResetLift(fe); return; }
        var translate = EnsureTranslate(fe);
        var from = (double)translate.GetValue(TranslateTransform.YProperty);
        var anim = new DoubleAnimation(from, y, new Duration(TimeSpan.FromSeconds(seconds)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) =>
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = y;
        };
        translate.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private static void ResetLift(FrameworkElement fe)
    {
        if (fe.RenderTransform is not TranslateTransform translate) return;
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = 0;
    }

    private static TranslateTransform EnsureTranslate(FrameworkElement e)
    {
        if (e.RenderTransform is TranslateTransform existing) return existing;
        var translate = new TranslateTransform();
        e.RenderTransform = translate;
        return translate;
    }
}
