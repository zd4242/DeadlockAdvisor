namespace DeadlockAdvisor.Vision.Items;

/// <summary>
/// The three bars under a portrait, left to right weapon, spirit and vitality: each is a dark track with a pale fill rising from
/// its foot. Their lengths are in pitches from the grid's top, so they hold at any resolution. A bar's fill is how much a hero
/// has put into its category, but not a sum: it rises and flattens, and is full only past about 28k souls.
/// </summary>
public static class BarReader
{
    /// <summary>A full bar's height, in pitches.</summary>
    public const double TrackHeight = 0.27;

    /// <summary>
    /// How full a bar is for the souls a hero has put into its category: 1 − e^(−souls / 10000), the same for the three bars, on
    /// the 411 bars of the standard-mode boards measured (a mean error of 0.012, never over 0.19). A Street Brawl's bars follow
    /// another curve (about three times as steep, and not so clean), so they aren't checked.
    /// </summary>
    public static double ExpectedFill(int souls) => 1 - Math.Exp(-souls / 10000.0);

    /// <summary>A bar this far from what its category's souls say is flagged, as a share of a full bar.</summary>
    public const double Tolerance = 0.15;

    /// <summary>
    /// A hero's bars against the items read: where the souls are exact, a bar further than <see cref="Tolerance"/> from
    /// <see cref="ExpectedFill"/> disagrees; where a cell is unknown, the souls are only a least, and a bar emptier than that
    /// disagrees. A disagreement says a cell was read as the wrong tier or category, or a bar was misread, not which.
    /// </summary>
    public static IReadOnlyList<BarCheck> Check(HeroItems hero, ItemTemplates templates)
    {
        if (hero.Bars is not { } bars)
            return [];
        var checks = new List<BarCheck>();
        foreach (var category in Enum.GetValues<ShopCategory>())
        {
            var (souls, exact) = hero.Souls(templates, category);
            var expected = ExpectedFill(souls);
            var read = bars.Of(category) / TrackHeight;
            checks.Add(new BarCheck(category, souls, exact, expected, read, exact ? Math.Abs(read - expected) > Tolerance : read < expected - Tolerance));
        }
        return checks;
    }

    private static readonly double[] _offsets = [-0.125, 0.0, 0.135];

    // Where to look for a bar, in pitches below the grid's top: the track is 2.17 to 2.44 and the window reaches past either end.
    private const double _from = 2.05;
    private const double _to = 2.60;
    private const double _halfWidth = 0.02;
    private const double _litLuminance = 140;

    /// <param name="top">The grid's top, as the cells are placed from it.</param>
    /// <param name="pitch">The slot pitch the board is drawn at.</param>
    /// <param name="slotCenter">The slot's centre, across.</param>
    public static BarsRead? Read(RgbImage band, double top, double pitch, double slotCenter)
    {
        var heights = new double[3];
        for (var bar = 0; bar < heights.Length; bar++)
        {
            if (Height(band, top, pitch, slotCenter + _offsets[bar] * pitch) is not { } height)
                return null;
            heights[bar] = height;
        }
        return new BarsRead(heights[0], heights[1], heights[2]);
    }

    /// <summary>The longest run of rows in the bar's window that are mostly lit, in pitches; null when the window is off the image.</summary>
    private static double? Height(RgbImage band, double top, double pitch, double x)
    {
        int x0 = (int)Math.Round(x - _halfWidth * pitch), x1 = (int)Math.Round(x + _halfWidth * pitch);
        int y0 = (int)Math.Round(top + _from * pitch), y1 = (int)Math.Round(top + _to * pitch);
        if (x0 < 0 || x1 >= band.Width || y0 < 0 || y1 > band.Height)
            return null;
        int best = 0, run = 0;
        for (var y = y0; y < y1; y++)
        {
            var lit = 0;
            for (var px = x0; px <= x1; px++)
            {
                var (r, g, b) = band[px, y];
                lit += 0.299 * r + 0.587 * g + 0.114 * b >= _litLuminance ? 1 : 0;
            }
            run = lit * 2 > x1 - x0 + 1 ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return Math.Min(TrackHeight, best / pitch);
    }
}
