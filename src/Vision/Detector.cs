namespace DeadlockAdvisor.Vision;

/// <param name="Ranked">The five best heroes for the slot, with their scores.</param>
public sealed record SlotReading(int Index, string? HeroId, double Score, double Margin, string? RunnerUp, Box Box,
    IReadOnlyList<(string HeroId, double Score)> Ranked)
{
    public bool IsConfident => Matcher.IsConfident(HeroId is not null, Score, Margin);
}

/// <summary>What a screenshot says: a reading per slot, the grid it was read with, and which slot is you.</summary>
public sealed record Detection(IReadOnlyList<SlotReading> Slots, Geometry Geometry, int? SelfSlot, double SelfScore,
    RgbImage? Image, IReadOnlyList<double> SelfScores)
{
    private int OwnBase => SelfSlot < Layout.PerTeam ? 0 : Layout.PerTeam;

    public List<int> AllySlots => SelfSlot is not { } self
        ? []
        : Enumerable.Range(OwnBase, Layout.PerTeam).Where(i => i != self).ToList();

    public List<int> EnemySlots => SelfSlot is null ? [] : Enumerable.Range(Layout.PerTeam - OwnBase, Layout.PerTeam).ToList();

    public string? HeroAt(int slot) => slot >= 0 && slot < Slots.Count ? Slots[slot].HeroId : null;

    /// <summary>
    /// The slot as the grid frames it, the same for whoever is in it. The box the winning hero scored
    /// best in drifts with the hero (and with whatever overlay it latched onto), so it's no frame to
    /// learn from or show.
    /// </summary>
    public RgbImage? CropOf(int slot) => Image is { } image ? Layout.Crop(image, Geometry.Boxes()[slot]) : null;

    public int ConfidentCount => Slots.Count(slot => slot.IsConfident);

    /// <summary>
    /// How well the grid fits: the mean of its best slots' top scores, ignoring the worst third (the
    /// dead). A right grid reads 0.8 or more even with four players dead; a wrong one reads far less.
    /// </summary>
    public double Fit => Scores.Count == 0 ? 0 : Layout.TrimmedScore(Scores.Select(row => row.Max()).ToArray());

    /// <summary>The heroes the scores are for, in the bank's order.</summary>
    public IReadOnlyList<string> Heroes { get; init; } = [];

    /// <summary>Every hero's score in every slot, before any hero was assigned.</summary>
    public IReadOnlyList<float[]> Scores { get; init; } = [];
}

/// <summary>
/// One screenshot in, a read of the match out: find the grid (or reuse the cached one), refine every
/// slot in a wide window, refit the grid to where the confident slots landed, read each slot at its
/// own box, assign heroes under the no-duplicates rule, then read which slot is you off the
/// backplate. A pure function of the image.
/// </summary>
public static class Detector
{
    // The final read: every portrait is cut where the top bar crops its card, so each sits in the
    // slot's box and only needs looking for a few per cent either way. Hundreds of tries per hero
    // would let a wrong hero's best of them crowd out the right one.
    public static readonly double[] FinalScales = [0.95, 1.0, 1.05];
    public const double FinalSpan = 0.04;
    public const int FinalSamples = 5;

    /// <summary>How much lower the final read may score than the wide one before the refit behind it is distrusted.</summary>
    public const double RefitTolerance = 0.1;

    // Your slot is backed by a flat rectangle in your team's colour, amber or sapphire, filling the
    // strip above your portrait. During laning your lane partner's is lit too, but at about half the
    // brightness (0.3-0.45 against your 0.67-0.82 on the labelled captures); the map shows above the
    // rest, and was never both that saturated a team colour and that bright. Anything else, such as
    // a red critical backdrop or the spectator strip, isn't a team hue at all.
    public const double SelfMinBrightness = 0.6;
    public const double SelfMinSaturation = 0.45;
    public const double SelfLead = 0.15;
    private static readonly (double From, double To)[] _teamHues = [(25, 45), (210, 230)];

    public static Detection? Detect(RgbImage image, TemplateBank bank, Geometry? geometry = null,
        (double Min, double Max)? pitchRange = null, (double Min, double Max)? topRange = null,
        int? screenHeight = null, IProgress<double>? progress = null)
    {
        if (bank.IsEmpty)
            return null;
        if (geometry is null)
        {
            if (Layout.Search(image, bank, pitchRange, topRange, screenHeight, progress) is not { } found)
                return null;
            geometry = found.Geometry;
        }

        // Pass one: wide window, then refit both where the slots are and how big the art is.
        var (boxes, scores) = Layout.RefineSlots(image, bank, geometry.Boxes(), Layout.SlotScales, Layout.SlotSpanX, Layout.SlotSpanY);
        var peaks = scores.Select(row => row.Max()).ToArray();
        var weights = peaks.Select(p => Math.Max(0.0, (double)p - Layout.RefitMinScore)).ToList();
        var refit = Layout.FitGeometry(boxes.Select(b => (double?)b.CenterX).ToList(), weights, geometry);
        refit = Layout.FitShape(boxes, weights, refit);

        // Pass two: each slot's own box on the corrected grid, give or take a few per cent. A read far
        // worse than the wide one means the refit went wrong, and the wide read stands.
        var (finalBoxes, finalScores) = Layout.RefineSlots(image, bank, refit.Boxes(), FinalScales, FinalSpan, FinalSpan, FinalSamples, FinalSamples);
        if (Layout.TrimmedScore(finalScores.Select(row => row.Max()).ToArray()) >= Layout.TrimmedScore(peaks) - RefitTolerance)
            (geometry, scores, boxes) = (refit, finalScores, finalBoxes);

        var readings = Matcher.Assign(scores).Select(assignment =>
        {
            var row = scores[assignment.Slot];
            return new SlotReading(
                assignment.Slot,
                assignment.HeroIndex is { } hero ? bank.Heroes[hero] : null,
                assignment.Score,
                assignment.Margin,
                assignment.RunnerUp is { } runner ? bank.Heroes[runner] : null,
                boxes[assignment.Slot],
                Matcher.Ranked(row).Take(5).Select(i => (bank.Heroes[i], (double)row[i])).ToList());
        }).ToList();

        var values = SelfSlotScores(image, geometry);
        var (selfSlot, selfScore) = FindSelfSlot(values);
        return new Detection(readings, geometry, selfSlot, selfScore, image, values) { Heroes = bank.Heroes, Scores = scores };
    }

    /// <summary>
    /// How much each slot looks like yours: the brightness of the strip above its portrait, from the
    /// top of the screen to a little above the art (characters overflow their box by about a tenth,
    /// so any lower reads hair and hats), if that's a saturated team colour, and nothing otherwise.
    /// </summary>
    public static List<double> SelfSlotScores(RgbImage image, Geometry geometry)
    {
        var bottom = Math.Max(4, (int)Math.Round(geometry.Top - 0.12 * geometry.ArtHeight));
        var half = geometry.Pitch * 0.3;
        return geometry.Centers().Select(center =>
        {
            if (BackplateColor(image, (int)Math.Round(center - half), (int)Math.Round(center + half), bottom) is not { } color)
                return 0.0;
            var (hue, saturation, value) = ImageOps.Hsv(color);
            return saturation >= SelfMinSaturation && _teamHues.Any(range => hue >= range.From && hue <= range.To) ? value : 0.0;
        }).ToList();
    }

    /// <summary>
    /// The mean colour of a strip from the top of the image, leaving out near-black pixels (a
    /// letterbox, or the edge of a crop): null when those are most of it.
    /// </summary>
    private static float[]? BackplateColor(RgbImage image, int left, int right, int bottom)
    {
        const int dark = 30;
        (left, right, bottom) = (Math.Max(0, left), Math.Min(image.Width, right), Math.Min(image.Height, bottom));
        var sum = new double[3];
        int counted = 0, total = 0;
        for (var y = 1; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                total++;
                var at = (y * image.Width + x) * 3;
                var (r, g, b) = (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
                if (Math.Max(r, Math.Max(g, b)) < dark)
                    continue;
                (sum[0], sum[1], sum[2]) = (sum[0] + r, sum[1] + g, sum[2] + b);
                counted++;
            }
        }
        return counted * 2 < total || counted == 0 ? null : sum.Select(channel => (float)(channel / counted)).ToArray();
    }

    /// <summary>
    /// The brightest team-coloured backplate, if it's bright enough and clearly brighter than the
    /// next. Without a clear winner, say nothing: a wrong You silently mislabels both teams.
    /// </summary>
    public static (int? Slot, double Score) FindSelfSlot(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return (null, 0.0);
        var ranked = values.Select((value, slot) => (Value: value, Slot: slot)).OrderByDescending(entry => entry.Value).ToList();
        var best = ranked[0];
        var next = ranked.Count > 1 ? ranked[1].Value : 0.0;
        if (best.Value < SelfMinBrightness || best.Value - next < SelfLead)
            return (null, best.Value);
        return (best.Slot, best.Value);
    }
}
