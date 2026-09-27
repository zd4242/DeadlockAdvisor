using System.Globalization;
using Avalonia.Media;

namespace DeadlockAdvisor.Theme;

/// <summary>Text for the custom-drawn controls, in the app's font.</summary>
public static class Fonts
{
    public const string Family = "Segoe UI";

    public static readonly Typeface Regular = new(Family);
    public static readonly Typeface Bold = new(Family, FontStyle.Normal, FontWeight.Bold);

    public static FormattedText Text(string text, double size, Color color, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Regular, size, new SolidColorBrush(color));

    /// <summary>A single line trimmed with an ellipsis to <paramref name="width"/>, centred in it.</summary>
    public static FormattedText Centered(string text, double size, Color color, double width, bool bold = false)
    {
        var formatted = Text(text, size, color, bold);
        formatted.MaxTextWidth = Math.Max(1, width);
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        formatted.TextAlignment = TextAlignment.Center;
        return formatted;
    }

    /// <summary>
    /// Where to draw <paramref name="text"/> so its glyphs sit centred in <paramref name="box"/>. Centring the
    /// line box instead leaves text without descenders, like numbers, riding high.
    /// </summary>
    public static Point InkCentered(FormattedText text, Rect box)
    {
        var ink = text.BuildGeometry(default)?.Bounds ?? new Rect(0, 0, text.Width, text.Height);
        return new Point(box.X + (box.Width - ink.Width) / 2 - ink.X, box.Y + (box.Height - ink.Height) / 2 - ink.Y);
    }
}
