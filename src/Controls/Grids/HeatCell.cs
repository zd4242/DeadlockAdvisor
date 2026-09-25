using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls.Grids;

/// <summary>
/// A number cell shaded by magnitude, gold for positive and red for negative, so which cells are
/// filled in reads at a glance. Shared by the hero-traits and coefficient grids.
/// </summary>
public static class HeatCell
{
    public static Color Background(bool alternate) => alternate ? Palette.Surface2 : Palette.Surface;

    /// <summary>The shade for <paramref name="value"/> on a scale reaching ±<paramref name="limit"/>, or null for an empty cell.</summary>
    public static Color? Shade(double value, double limit, bool alternate)
    {
        if (value == 0 || limit == 0)
            return null;
        var strength = Math.Min(1.0, Math.Abs(value) / limit);
        return Palette.Mix(Background(alternate), value > 0 ? Palette.Accent : Palette.Enemy, 0.16 + 0.52 * strength);
    }

    /// <summary>Background, shade and, for the current cell, the gold frame. The caller draws the text.</summary>
    public static void Paint(DrawingContext context, Rect rect, double value, double limit, bool alternate, bool selected)
    {
        context.FillRectangle(new SolidColorBrush(Background(alternate)), rect);
        if (Shade(value, limit, alternate) is { } shade)
            context.FillRectangle(new SolidColorBrush(shade), rect.Deflate(1));
        if (selected)
            context.DrawRectangle(new Pen(new SolidColorBrush(Palette.Accent), 2), rect.Deflate(2));
    }
}
