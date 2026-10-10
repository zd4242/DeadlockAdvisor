using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Tests;

/// <summary>WCAG contrast of the text colours on the surfaces they sit on: AA wants 4.5:1 for small text.</summary>
public class PaletteTests
{
    private const double SmallText = 4.5;

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (high, low) = (Luminance(a), Luminance(b));
        if (high < low)
            (high, low) = (low, high);
        return (high + 0.05) / (low + 0.05);
    }

    [Fact]
    public void TheContrastFormulaMatchesTheKnownExtremes()
    {
        Assert.Equal(21, Contrast(Colors.White, Colors.Black), 6);
        Assert.Equal(1, Contrast(Palette.Bg, Palette.Bg), 6);
    }

    [Fact]
    public void TextAndDimTextReadOnEverySurfaceTheyAreUsedOn()
    {
        foreach (var surface in new[] { Palette.Bg, Palette.Surface, Palette.Surface2, Palette.Surface3 })
        {
            Assert.True(Contrast(Palette.Text, surface) >= SmallText, $"Text on {Palette.Hex(surface)}");
            Assert.True(Contrast(Palette.TextDim, surface) >= SmallText, $"TextDim on {Palette.Hex(surface)}");
        }
    }

    [Fact]
    public void FaintTextReadsOnTheBackgroundAndTheCards()
    {
        foreach (var surface in new[] { Palette.Bg, Palette.Surface, Palette.Surface2 })
            Assert.True(Contrast(Palette.TextFaint, surface) >= SmallText, $"TextFaint on {Palette.Hex(surface)}");
    }

    [Fact]
    public void FaintTextStaysBelowDimTextSoTheHierarchyReads()
    {
        foreach (var surface in new[] { Palette.Bg, Palette.Surface2 })
            Assert.True(Contrast(Palette.TextDim, surface) - Contrast(Palette.TextFaint, surface) >= 1, Palette.Hex(surface));
    }

    [Fact]
    public void TheAccentAndTeamColoursReadOnTheBackground()
    {
        foreach (var color in new[] { Palette.Accent, Palette.Ally, Palette.Enemy, Palette.Data, Palette.DataSelf })
            Assert.True(Contrast(color, Palette.Bg) >= SmallText, Palette.Hex(color));
    }
}
