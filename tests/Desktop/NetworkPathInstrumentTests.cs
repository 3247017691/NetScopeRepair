// tests/Desktop/NetworkPathInstrumentTests.cs
using System.Reflection;
using System.Windows.Media;
using System.Xml.Linq;
using NetScope.Desktop.Controls;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class NetworkPathInstrumentTests
{
    [Fact]
    public void BuildTraceGeometry_all_ok_connects_full_path_to_internet()
    {
        var (healthy, fault) = Build(Model(PathStationState.Ok, PathStationState.Ok, PathStationState.Ok));

        Assert.Equal(FirstHopZigzag() + " L500 64 L740 64", healthy);
        Assert.Null(fault);
    }

    [Fact]
    public void BuildTraceGeometry_internet_unknown_stops_trace_at_egress()
    {
        var (healthy, fault) = Build(Model(PathStationState.Ok, PathStationState.Ok, PathStationState.Unknown));

        Assert.Equal(FirstHopZigzag() + " L500 64", healthy);
        Assert.Null(fault);
    }

    [Fact]
    public void BuildTraceGeometry_egress_unknown_stops_trace_at_proxy()
    {
        var (healthy, fault) = Build(Model(PathStationState.Ok, PathStationState.Unknown, PathStationState.Unknown));

        Assert.Equal(FirstHopZigzag(), healthy);
        Assert.Null(fault);
    }

    [Fact]
    public void BuildTraceGeometry_proxy_unknown_draws_no_trace_at_all()
    {
        var (healthy, fault) = Build(NetworkPathModel.Unmeasured);

        Assert.Null(healthy);
        Assert.Null(fault);
    }

    [Fact]
    public void BuildTraceGeometry_proxy_fault_keeps_upstream_zigzag_and_faults_from_proxy()
    {
        var (healthy, fault) = Build(Model(PathStationState.Fault, PathStationState.Unknown, PathStationState.Unknown));

        Assert.Equal(FirstHopZigzag(), healthy);
        Assert.Equal("M232 64 L740 64", fault);
    }

    [Fact]
    public void BuildTraceGeometry_internet_fault_with_healthy_seg2_faults_last_hop_only()
    {
        var (healthy, fault) = Build(Model(PathStationState.Ok, PathStationState.Ok, PathStationState.Fault));

        Assert.Equal(FirstHopZigzag() + " L500 64", healthy);
        Assert.Equal("M500 64 L740 64", fault);
    }

    [Fact]
    public void BuildTraceGeometry_internet_fault_with_unknown_egress_draws_no_fault_line()
    {
        var (healthy, fault) = Build(Model(PathStationState.Ok, PathStationState.Unknown, PathStationState.Fault));

        Assert.Equal(FirstHopZigzag(), healthy);
        Assert.Null(fault);
    }

    private static (string? HealthyData, string? FaultData) Build(NetworkPathModel model)
    {
        var mi = typeof(NetworkPathInstrument).GetMethod("BuildTraceGeometry", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(mi);
        return ((string? HealthyData, string? FaultData))mi!.Invoke(null, new object[] { model })!;
    }

    private static NetworkPathModel Model(PathStationState proxy, PathStationState egress, PathStationState internet) => new(
        new PathStation("本机", "本机", PathStationState.Ok, ""),
        new PathStation("代理端口", "7897", proxy, ""),
        new PathStation("出口", "WLAN", egress, ""),
        new PathStation("互联网", "可达", internet, ""),
        "7897", "—");

    // Read the live first-hop geometry from the control XAML so the composed trace cannot drift from it.
    private static string FirstHopZigzag()
    {
        XNamespace pres = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "NetScope.Desktop", "Controls", "NetworkPathInstrument.xaml"));
        return doc.Descendants(pres + "Path")
            .First(e => e.Attribute(x + "Name")?.Value == "HealthyPath")
            .Attribute("Data")!.Value;
    }

    [Fact]
    public void ArcLength_measures_real_zigzag_stroke_not_bounds_width()
    {
        var mi = typeof(NetworkPathInstrument).GetMethod("ArcLength", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(mi);

        // Read the live HealthyPath geometry from the control XAML so the test cannot drift from it.
        XNamespace pres = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "NetScope.Desktop", "Controls", "NetworkPathInstrument.xaml"));
        var data = doc.Descendants(pres + "Path")
            .First(e => e.Attribute(x + "Name")?.Value == "HealthyPath")
            .Attribute("Data")!.Value;
        var geom = Geometry.Parse(data);

        var len = (double)mi!.Invoke(null, new object[] { geom })!;

        // Hand-computed sum of per-segment Euclidean lengths ≈ 418.5987.
        Assert.InRange(len, 418.0, 419.2);
        // The original defect: Bounds.Width (212) underestimates the zigzag stroke, revealing only half.
        Assert.True(len > geom.Bounds.Width);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetScopeRepair.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}