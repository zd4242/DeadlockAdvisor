using Avalonia.Media;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// What the match bar shows of a hero's build: how their souls divide among weapon, vitality and spirit (shares of one, in that
/// order), whether the read is old, and the tooltip's lines.
/// </summary>
/// <param name="Tip">The hero's items by name, flagging cells that weren't recognised, and how old the read is.</param>
public sealed record BuildSummary(double Weapon, double Vitality, double Spirit, bool IsOld, string Tip)
{
    /// <summary>"weapon 70%, vitality 10%, spirit 20%", for a screen reader and the tooltip.</summary>
    public string Described =>
        string.Join(", ", Parts.Select(part => FormattableString.Invariant($"{part.Name} {Math.Round(part.Share * 100):0}%")));

    internal IEnumerable<(string Name, double Share, Color Color)> Parts =>
    [
        ("weapon", Weapon, Palette.ShopWeapon),
        ("vitality", Vitality, Palette.ShopVitality),
        ("spirit", Spirit, Palette.ShopSpirit),
    ];
}

/// <summary>Three thin columns, weapon, vitality and spirit, each filled to its share of a hero's souls in the colour of its shop category. Painted into a slot rather than a control of their own, since there are twelve.</summary>
public static class BuildBars
{
    public const double Width = 6;
    public const double Gap = 3;
    public const double Height = 14;

    private static readonly IBrush _track = new SolidColorBrush(Palette.WithAlpha(Palette.Border, 160));

    /// <summary>Total width of the three columns.</summary>
    public static double Span => Width * 3 + Gap * 2;

    /// <summary>Draw the columns centred on <paramref name="centerX"/> with their foot at <paramref name="bottom"/>. An old read is drawn faded.</summary>
    public static void Paint(DrawingContext context, BuildSummary build, double centerX, double bottom)
    {
        var left = centerX - Span / 2;
        var index = 0;
        foreach (var (_, share, color) in build.Parts)
        {
            var column = new Rect(left + index++ * (Width + Gap), bottom - Height, Width, Height);
            context.DrawRectangle(_track, null, new RoundedRect(column, 1.5));
            var fill = share > 0 ? Math.Max(2, Math.Round(Height * Math.Clamp(share, 0, 1))) : 0;
            if (fill == 0)
                continue;
            var brush = new SolidColorBrush(build.IsOld ? Palette.WithAlpha(color, 110) : color);
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(column.X, column.Bottom - fill, Width, fill), 1.5));
        }
    }
}
