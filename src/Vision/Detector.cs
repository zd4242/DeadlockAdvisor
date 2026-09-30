namespace DeadlockAdvisor.Vision;

/// <param name="Ranked">The five best heroes for the slot, with their scores.</param>
public sealed record SlotReading(int Index, string? HeroId, double Score, double Margin, string? RunnerUp, Box Box,
    IReadOnlyList<(string HeroId, double Score)> Ranked)
{
    public bool IsConfident => HeroId is not null && Score >= Matcher.MinScore && Margin >= Matcher.ClearMargin;
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

    // Your slot has to clear this floor outright and stand this far above the median slot. Against
    // the runner-up was wrong: during laning the game lights all four lane players, so the runner-up
    // is a lane-mate close behind. Against the median, your slot led by 7× to 37× on every capture.
    public const double SelfMinScore = 2.0;
    public const double SelfBaselineRatio = 4.0;

    // Where the backplate is sampled: two heights above the art (characters overflow their box by
    // about a tenth, so any closer reads hair and hats) and four points across the slot at each.
    private static readonly double[] _selfProbeLifts = [0.16, 0.24];
    private static readonly double[] _selfProbeFractions = [0.10, 0.35, 0.65, 0.90];

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
    /// How much each slot looks like yours. Your slot is backed by an opaque rectangle in your team's
    /// colour; every other slot shows the game world above it, and eleven of the twelve showing the
    /// map makes the median the map. So: colour with brightness thrown away, deviation from the median
    /// weighted by saturation (a slot over deep shadow is uniformly unsaturated, not a backplate), and
    /// divided by how much the eight sample points disagree (a flat backplate agrees with itself; one
    /// bright respawn timer doesn't).
    /// </summary>
    public static List<double> SelfSlotScores(RgbImage image, Geometry geometry)
    {
        var artHeight = geometry.ArtHeight;
        var size = Math.Max(2, (int)Math.Round(geometry.Pitch * 0.10));
        var means = new List<(float A, float B)>();
        var spreads = new List<double>();

        foreach (var box in geometry.Boxes())
        {
            var centerX = box.CenterX;
            var chroma = new List<(float A, float B)>();
            foreach (var lift in _selfProbeLifts)
            {
                var top = (int)Math.Round(Math.Max(0.0, box.Y - artHeight * lift));
                foreach (var fraction in _selfProbeFractions)
                {
                    var at = (int)Math.Round(centerX - geometry.Pitch / 2.0 + geometry.Pitch * fraction);
                    var (mean, _) = ImageOps.MeanColor(image, at - size / 2, top, at + size / 2 + 1, top + size);
                    if (mean.Any(v => v != 0))
                        chroma.Add(Chroma(mean));
                }
            }
            if (chroma.Count == 0)
            {
                means.Add((0f, 0f));
                spreads.Add(1e6);
                continue;
            }
            // A column mean in numpy (axis 0) sums row by row, not pairwise.
            float sumA = 0f, sumB = 0f;
            foreach (var (a, b) in chroma)
            {
                sumA += a;
                sumB += b;
            }
            var centre = (A: sumA / chroma.Count, B: sumB / chroma.Count);
            means.Add(centre);
            spreads.Add(NumpyMath.Mean(chroma.Select(c => Norm(c.A - centre.A, c.B - centre.B)).ToList()));
        }

        var background = (A: NumpyMath.Median(means.Select(m => m.A).ToList()), B: NumpyMath.Median(means.Select(m => m.B).ToList()));
        double backgroundStrength = Norm(background.A, background.B);
        var scores = new List<double>();
        foreach (var (a, b) in means)
        {
            double deviation = Norm(a - background.A, b - background.B);
            double strength = Norm(a, b);
            var saturation = strength / (strength + backgroundStrength + 1e-6);
            scores.Add(deviation * saturation / (1.0 + spreads[scores.Count]));
        }
        return scores;
    }

    /// <summary>RGB → the two opponent-colour axes, dropping brightness.</summary>
    private static (float A, float B) Chroma(float[] rgb) => (rgb[0] - rgb[1], 0.5f * (rgb[0] + rgb[1]) - rgb[2]);

    /// <summary>A float32 vector length, as np.linalg.norm gives for float32 input.</summary>
    private static float Norm(float x, float y) => MathF.Sqrt(x * x + y * y);

    /// <summary>Without a clear winner, say nothing: a wrong You silently mislabels both teams.</summary>
    public static (int? Slot, double Score) FindSelfSlot(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return (null, 0.0);
        var best = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] > values[best])
                best = i;
        }
        double baseline = NumpyMath.Median(values.Select(value => (float)value).ToList());
        if (values[best] < SelfMinScore || values[best] < baseline * SelfBaselineRatio)
            return (null, values[best]);
        return (best, values[best]);
    }
}
