// tests/Desktop/DesignTokensTests.cs
using System.Windows.Media;
using System.Xml.Linq;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class DesignTokensTests
{
    private static byte[] Rgb(Brush b) => new[]
    {
        ((SolidColorBrush)b).Color.R, ((SolidColorBrush)b).Color.G, ((SolidColorBrush)b).Color.B
    };

    private static byte[] HexBytes(string hex)
    {
        var h = hex.TrimStart('#');
        return new[]
        {
            Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16)
        };
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetScopeRepair.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Signal_accent_is_teal()
    {
        Assert.Equal(new byte[] { 0x0E, 0x7C, 0x66 }, Rgb(UiText.AccentBrush));
    }

    [Fact]
    public void Status_brushes_are_aa_tuned()
    {
        Assert.Equal(new byte[] { 0x17, 0x7A, 0x4C }, Rgb(UiText.OkBrush));
        Assert.Equal(new byte[] { 0xB4, 0x53, 0x09 }, Rgb(UiText.WarnBrush));
        Assert.Equal(new byte[] { 0xC0, 0x39, 0x2B }, Rgb(UiText.BadBrush));
    }

    [Fact]
    public void Soft_background_mapping_still_resolves_by_reference()
    {
        Assert.Same(UiText.OkBgBrush, UiText.SoftBackground(UiText.OkBrush));
        Assert.Same(UiText.AccentBgBrush, UiText.SoftBackground(UiText.AccentBrush));
        Assert.Same(UiText.GapBgBrush, UiText.SoftBackground(UiText.GapBrush));
    }

    [Fact]
    public void Theme_xaml_palette_matches_UiText()
    {
        XNamespace pres = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "NetScope.Desktop", "Resources", "Theme.xaml"));
        var colors = doc.Descendants(pres + "Color")
            .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Value.Trim());

        // 15 shared tokens: the XAML Color must equal the UiText brush view-models hand to rows.
        var parity = new (string Key, Brush Brush)[]
        {
            ("AccentColor", UiText.AccentBrush),
            ("AccentLightColor", UiText.AccentBgBrush),
            ("AccentBorderColor", UiText.AccentBorderBrush),
            ("OkColor", UiText.OkBrush),
            ("OkBgColor", UiText.OkBgBrush),
            ("OkBorderColor", UiText.OkBorderBrush),
            ("WarnColor", UiText.WarnBrush),
            ("WarnBgColor", UiText.WarnBgBrush),
            ("WarnBorderColor", UiText.WarnBorderBrush),
            ("BadColor", UiText.BadBrush),
            ("BadBgColor", UiText.BadBgBrush),
            ("BadBorderColor", UiText.BadBorderBrush),
            ("GapColor", UiText.GapBrush),
            ("GapBgColor", UiText.GapBgBrush),
            ("GapBorderColor", UiText.GapBorderBrush),
        };
        foreach (var (key, brush) in parity)
            Assert.Equal(HexBytes(colors[key]), Rgb(brush));

        // 17 XAML-only tokens (no UiText counterpart): pin hex. OnAccent is T8's white-on-teal ink.
        var xamlOnly = new (string Key, string Hex)[]
        {
            ("CanvasColor", "#FFFFFF"),
            ("RailColor", "#F7F8FA"),
            ("RailMutedColor", "#474E55"),
            ("SurfaceColor", "#FFFFFF"),
            ("SubtleSurfaceColor", "#F8F9FB"),
            ("HoverSurfaceColor", "#F0F2F5"),
            ("LineColor", "#E8EAED"),
            ("LineSubtleColor", "#EFF1F4"),
            ("InkColor", "#1F2329"),
            ("MutedColor", "#6B7280"),
            ("SubtleTextColor", "#9AA0A8"),
            ("AccentInteractiveColor", "#0C6B58"),
            ("AccentDeepColor", "#0A5A4A"),
            ("HoverBorderColor", "#D3D8DE"),
            ("ScrollThumbHoverColor", "#B6BCC4"),
            ("UnknownColor", "#C4C9D0"),
            ("OnAccentColor", "#FFFFFF"),
        };
        foreach (var (key, hex) in xamlOnly)
            Assert.Equal(HexBytes(hex), HexBytes(colors[key]));

        // Referential integrity: every SolidColorBrush's StaticResource Color key must exist.
        foreach (var brush in doc.Descendants(pres + "SolidColorBrush"))
        {
            var refAttr = brush.Attribute("Color")?.Value;
            if (refAttr is null || !refAttr.StartsWith("{StaticResource ", StringComparison.Ordinal)) continue;
            var key = refAttr["{StaticResource ".Length..^1].Trim();
            Assert.True(colors.ContainsKey(key), $"SolidColorBrush references missing Color key '{key}'.");
        }

        // The Color key set is exactly these 32; adding/removing a token must consciously update this test.
        var expectedKeys = parity.Select(p => p.Key).Concat(xamlOnly.Select(p => p.Key)).ToHashSet();
        Assert.Equal(expectedKeys, colors.Keys.ToHashSet());
    }

    [Fact]
    public void Plex_sans_resolves_four_weights()
    {
        var typefaces = EmbeddedFontFamily("IBM Plex Sans").GetTypefaces();
        var weights = typefaces.Select(t => t.Weight).ToHashSet();
        Assert.Contains(System.Windows.FontWeights.Normal, weights);
        Assert.Contains(System.Windows.FontWeights.Medium, weights);
        Assert.Contains(System.Windows.FontWeights.SemiBold, weights);
        Assert.Contains(System.Windows.FontWeights.Bold, weights);
        var weightFile = new Dictionary<System.Windows.FontWeight, string>
        {
            [System.Windows.FontWeights.Normal] = "ibmplexsans-regular.ttf",
            [System.Windows.FontWeights.Medium] = "ibmplexsans-medium.ttf",
            [System.Windows.FontWeights.SemiBold] = "ibmplexsans-semibold.ttf",
            [System.Windows.FontWeights.Bold] = "ibmplexsans-bold.ttf",
        };
        Assert.All(typefaces, t =>
        {
            Assert.True(t.TryGetGlyphTypeface(out var glyph));
            Assert.Contains("ibmplexsans-", glyph.FontUri.ToString());
            // WPF synthesizes simulated bold/oblique faces; the weight→file contract is about real embedded faces.
            if (t.IsBoldSimulated || t.IsObliqueSimulated) return;
            Assert.True(weightFile.TryGetValue(t.Weight, out var file), "Unexpected Plex Sans weight " + t.Weight);
            Assert.EndsWith(file!, glyph.FontUri.ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Plex_mono_resolves_regular_and_medium()
    {
        var typefaces = EmbeddedFontFamily("IBM Plex Mono").GetTypefaces();
        var weights = typefaces.Select(t => t.Weight).ToHashSet();
        Assert.Contains(System.Windows.FontWeights.Normal, weights);
        Assert.Contains(System.Windows.FontWeights.Medium, weights);
        var weightFile = new Dictionary<System.Windows.FontWeight, string>
        {
            [System.Windows.FontWeights.Normal] = "ibmplexmono-regular.ttf",
            [System.Windows.FontWeights.Medium] = "ibmplexmono-medium.ttf",
        };
        Assert.All(typefaces, t =>
        {
            Assert.True(t.TryGetGlyphTypeface(out var glyph));
            Assert.Contains("ibmplexmono-", glyph.FontUri.ToString());
            // Only Regular/Medium are embedded; WPF synthesizes Bold/Oblique, which share a real file.
            if (t.IsBoldSimulated || t.IsObliqueSimulated) return;
            Assert.True(weightFile.TryGetValue(t.Weight, out var file), "Unexpected Plex Mono weight " + t.Weight);
            Assert.EndsWith(file!, glyph.FontUri.ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }

    // The xUnit host registers neither the pack scheme nor WPF's application model, and its
    // entry assembly is testhost; both touches are MTA-safe and ;component targets Desktop explicitly.
    private static System.Windows.Media.FontFamily EmbeddedFontFamily(string name)
    {
        _ = System.Windows.Application.ResourceAssembly;
        _ = System.IO.Packaging.PackUriHelper.Create(new Uri("http://localhost/"));
        return new System.Windows.Media.FontFamily(
            new Uri("pack://application:,,,/NetScope.Desktop;component/Resources/Fonts/"), "./#" + name);
    }
}
