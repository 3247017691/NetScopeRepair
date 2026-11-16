using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Controls;

public partial class NetworkPathInstrument : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(NetworkPathModel), typeof(NetworkPathInstrument),
        new PropertyMetadata(null, OnModelChanged));

    public NetworkPathModel? Model
    {
        get => (NetworkPathModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public NetworkPathInstrument()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateMotion();
        Unloaded += (_, _) => StopMotion();
        // Keep the variable-width proxy note centered under its node (x=232) as the text changes.
        ProxyNote.SizeChanged += (_, _) => Canvas.SetLeft(ProxyNote, Math.Max(0, 232 - ProxyNote.ActualWidth / 2));
    }

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NetworkPathInstrument)d).Apply((NetworkPathModel?)e.NewValue);

    private void Apply(NetworkPathModel? model)
    {
        if (model is null) return;
        var previousPort = PortReadout.Text;
        PortReadout.Text = model.PortReadout;
        PortReadout.Foreground = StateTextBrush(model.Proxy.State);
        LocalNode.Fill = StationBrush(model.Local.State);
        ProxyNode.Fill = StationBrush(model.Proxy.State);
        EgressNode.Fill = StationBrush(model.Egress.State);
        InternetNode.Fill = StationBrush(model.Internet.State);
        ProxyLabel.Foreground = StateTextBrush(model.Proxy.State);
        ProxyNote.Foreground = StateTextBrush(model.Proxy.State);
        ProxyNote.Text = model.Proxy.Note;
        LocalLabel.Foreground = StateTextBrush(model.Local.State);
        EgressLabel.Foreground = StateTextBrush(model.Egress.State);
        InternetLabel.Foreground = StateTextBrush(model.Internet.State);
        EgressNote.Visibility = model.Egress.State == PathStationState.Unknown ? Visibility.Visible : Visibility.Collapsed;
        InternetNote.Visibility = model.Internet.State == PathStationState.Unknown ? Visibility.Visible : Visibility.Collapsed;
        var (healthyData, faultData) = BuildTraceGeometry(model);
        HealthyPath.Data = healthyData is null ? null : Geometry.Parse(healthyData);
        HealthyPath.Visibility = healthyData is null ? Visibility.Collapsed : Visibility.Visible;
        FaultPath.Data = faultData is null ? null : Geometry.Parse(faultData);
        FaultPath.Visibility = faultData is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateMotion();
        if (PortReadout.Text != previousPort) SettleReadout();
    }

    // First-hop signature zigzag; must stay identical to the HealthyPath Data default in the XAML.
    private const string FirstHopZigzag = "M20 64 L70 64 L82 64 L90 30 L100 98 L110 64 L150 64 L162 64 L170 34 L180 94 L190 64 L232 64";

    // Per-segment trace composition (spec §6.2): healthy segments extend the teal trace, the first
    // fault station starts the red flat line, unknown segments keep the gray dotted baseline.
    internal static (string? HealthyData, string? FaultData) BuildTraceGeometry(NetworkPathModel model)
    {
        var seg2Healthy = model.Proxy.State == PathStationState.Ok && model.Egress.State == PathStationState.Ok;
        var seg3Healthy = seg2Healthy && model.Internet.State == PathStationState.Ok;

        string? healthy = null;
        if (model.Proxy.State != PathStationState.Unknown)
        {
            healthy = FirstHopZigzag;
            if (seg2Healthy) healthy += " L500 64";
            if (seg3Healthy) healthy += " L740 64";
        }

        string? fault = null;
        if (model.Proxy.State == PathStationState.Fault) fault = "M232 64 L740 64";
        else if (model.Internet.State == PathStationState.Fault && seg2Healthy) fault = "M500 64 L740 64";

        return (healthy, fault);
    }

    // Reveal-not-loading: data is already shown statically; motion only plays when gated on AND presented.
    // Off-screen renders never connect a PresentationSource (IsLoaded=false), so they stay static.
    private void UpdateMotion()
    {
        StopMotion();
        if (!Motion.Enabled || !IsLoaded) return;
        if (HealthyPath.Visibility == Visibility.Visible)
        {
            DrawIn(HealthyPath, 0.9, 0);
            StartBreathe();
            StartScan();
        }
        if (FaultPath.Visibility == Visibility.Visible) DrawIn(FaultPath, 0.5, 0.9);
    }

    // Every Forever clock and one-shot is removed here, then base values restored, so re-Apply,
    // condition-exit (Proxy→Unknown/fault) and Unloaded never leave a running clock or hidden content.
    private void StopMotion()
    {
        HealthyPath.BeginAnimation(UIElement.OpacityProperty, null);
        HealthyPath.Opacity = 1;
        HealthyPath.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty, null);
        HealthyPath.StrokeDashArray = null;
        HealthyPath.StrokeDashOffset = 0;
        FaultPath.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty, null);
        FaultPath.StrokeDashArray = null;
        FaultPath.StrokeDashOffset = 0;
        ScanDot.BeginAnimation(Canvas.LeftProperty, null);
        ScanDot.BeginAnimation(Canvas.TopProperty, null);
        ScanDot.Visibility = Visibility.Collapsed;
        PortReadout.BeginAnimation(UIElement.OpacityProperty, null);
        PortReadout.Opacity = 1;
    }

    private void StartBreathe()
    {
        var anim = new DoubleAnimation(1, 0.5, new Duration(TimeSpan.FromSeconds(1.6)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        HealthyPath.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void StartScan()
    {
        if (HealthyPath.Data is not { } data) return;
        var geometry = PathGeometry.CreateFromGeometry(data);
        var duration = new Duration(TimeSpan.FromSeconds(3.2));
        ScanDot.Visibility = Visibility.Visible;
        var x = new DoubleAnimationUsingPath
        { PathGeometry = geometry, Source = PathAnimationSource.X, Duration = duration, RepeatBehavior = RepeatBehavior.Forever };
        var y = new DoubleAnimationUsingPath
        { PathGeometry = geometry, Source = PathAnimationSource.Y, Duration = duration, RepeatBehavior = RepeatBehavior.Forever };
        ScanDot.BeginAnimation(Canvas.LeftProperty, x);
        ScanDot.BeginAnimation(Canvas.TopProperty, y);
    }

    private void SettleReadout()
    {
        if (!Motion.Enabled || !IsLoaded) { PortReadout.Opacity = 1; return; }
        var anim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(0.3)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) =>
        {
            PortReadout.BeginAnimation(UIElement.OpacityProperty, null);
            PortReadout.Opacity = 1;
        };
        PortReadout.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private Brush StationBrush(PathStationState state) => state switch
    {
        PathStationState.Ok => (Brush)FindResource("Ok"),
        PathStationState.Fault => (Brush)FindResource("Bad"),
        _ => (Brush)FindResource("Unknown"),
    };

    private Brush StateTextBrush(PathStationState state) => state switch
    {
        PathStationState.Fault => (Brush)FindResource("Bad"),
        PathStationState.Unknown => (Brush)FindResource("SubtleText"),
        _ => (Brush)FindResource("Ink"),
    };

    private static void DrawIn(System.Windows.Shapes.Path path, double seconds, double delay)
    {
        if (path.Data is not { } data) return;
        // 真实弧长：Bounds.Width 会低估折线脉冲的笔画长度，导致揭示不完整
        var len = ArcLength(data);
        path.StrokeDashArray = new DoubleCollection { len, len };
        path.StrokeDashOffset = len;
        var anim = new DoubleAnimation(len, 0, new Duration(TimeSpan.FromSeconds(seconds)))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            path.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty, null);
            path.StrokeDashArray = null;
            path.StrokeDashOffset = 0;
        };
        path.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty, anim);
    }

    private static double ArcLength(Geometry geometry)
    {
        double length = 0;
        foreach (var figure in geometry.GetFlattenedPathGeometry().Figures)
        {
            var current = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        length += (line.Point - current).Length;
                        current = line.Point;
                        break;
                    case PolyLineSegment poly:
                        foreach (var point in poly.Points)
                        {
                            length += (point - current).Length;
                            current = point;
                        }
                        break;
                }
            }
        }
        return length;
    }
}
