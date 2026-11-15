using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NetScope.Desktop.Controls;

/// <summary>
/// A small indeterminate activity spinner: a 270° arc that rotates efficiently via a single
/// animated angle (no per-frame layout). Honors the OS "show animations" setting — when animations
/// are disabled it stays a static arc rather than spinning, while the determinate progress and step
/// status still convey that work is underway.
/// </summary>
public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(Brushes.SlateBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Spinner),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Spinner),
        new PropertyMetadata(true, OnIsActiveChanged));

    private static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(Spinner),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private double Angle
    {
        get => (double)GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public Spinner()
    {
        Width = 16;
        Height = 16;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Spinner)d).UpdateAnimation();

    private void UpdateAnimation()
    {
        if (!IsLoaded || !IsActive || !SystemParameters.ClientAreaAnimation)
        {
            StopAnimation();
            return;
        }
        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = null,
        };
        BeginAnimation(AngleProperty, animation);
    }

    private void StopAnimation()
    {
        BeginAnimation(AngleProperty, null);
        Angle = 0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var thickness = Math.Max(1, Thickness);
        var radius = (size / 2) - (thickness / 2);
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        // 270° arc starting at the top, leaving a gap that reads as motion.
        const double sweep = 270;
        var start = Polar(center, radius, -90);
        var end = Polar(center, radius, -90 + sweep);
        var figure = new PathFigure(start, new[]
        {
            new ArcSegment(end, new Size(radius, radius), sweep, true, SweepDirection.Clockwise, true),
        }, false);
        var geometry = new PathGeometry(new[] { figure });

        var pen = new Pen(Stroke, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.PushTransform(new RotateTransform(Angle, center.X, center.Y));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
