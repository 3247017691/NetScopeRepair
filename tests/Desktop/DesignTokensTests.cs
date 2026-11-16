// tests/Desktop/DesignTokensTests.cs
using System.Windows.Media;
using NetScope.Desktop.ViewModels;

namespace NetScope.Desktop.Tests;

public sealed class DesignTokensTests
{
    private static byte[] Rgb(Brush b) => new[]
    {
        ((SolidColorBrush)b).Color.R, ((SolidColorBrush)b).Color.G, ((SolidColorBrush)b).Color.B
    };

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
}
