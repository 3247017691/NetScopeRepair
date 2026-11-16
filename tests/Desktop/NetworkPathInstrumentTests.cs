// tests/Desktop/NetworkPathInstrumentTests.cs
using System.Reflection;
using System.Windows.Media;
using System.Xml.Linq;
using NetScope.Desktop.Controls;

namespace NetScope.Desktop.Tests;

public sealed class NetworkPathInstrumentTests
{
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