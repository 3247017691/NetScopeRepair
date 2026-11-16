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

    public NetworkPathInstrument() => InitializeComponent();

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NetworkPathInstrument)d).Apply((NetworkPathModel?)e.NewValue);

    private void Apply(NetworkPathModel? model)
    {
        if (model is null) return;
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
        // 故障平线只在代理故障时显示；代理未测得时健康脉冲隐藏，未知不冒充健康
        FaultPath.Visibility = model.Proxy.State == PathStationState.Fault ? Visibility.Visible : Visibility.Collapsed;
        HealthyPath.Visibility = model.Proxy.State == PathStationState.Unknown ? Visibility.Collapsed : Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation && HealthyPath.Visibility == Visibility.Visible)
        {
            DrawIn(HealthyPath);
        }
        else
        {
            HealthyPath.BeginAnimation(System.Windows.Shapes.Path.StrokeDashOffsetProperty, null);
            HealthyPath.StrokeDashArray = null;
            HealthyPath.StrokeDashOffset = 0;
        }
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

    private static void DrawIn(System.Windows.Shapes.Path path)
    {
        if (path.Data is not { } data) return;
        // 真实弧长：Bounds.Width 会低估折线脉冲的笔画长度，导致揭示不完整
        var len = ArcLength(data);
        path.StrokeDashArray = new DoubleCollection { len, len };
        path.StrokeDashOffset = len;
        var anim = new DoubleAnimation(len, 0, new Duration(TimeSpan.FromSeconds(0.9)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
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
