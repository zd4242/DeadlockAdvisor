using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>The in-lane marker: a short accent bar along the bottom of a hero's portrait.</summary>
public static class LaneBar
{
    private const double _height = 3;
    private const double _inset = 3;

    public static void Paint(DrawingContext context, Rect portrait)
    {
        var width = portrait.Width * 0.55;
        var bar = new Rect(portrait.X + (portrait.Width - width) / 2, portrait.Bottom - _height - _inset, width, _height);
        context.DrawRectangle(new SolidColorBrush(Palette.Accent), null, new RoundedRect(bar, _height / 2));
    }
}
