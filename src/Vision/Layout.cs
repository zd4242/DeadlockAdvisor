using System.Text.Json.Nodes;

namespace DeadlockAdvisor.Vision;

/// <summary>A portrait's box on screen: left, top, width, height, in pixels.</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double CenterX => X + W / 2.0;
}

/// <summary>
/// Where the twelve portraits are: two blocks of six, evenly pitched, mirrored about the centre with
/// a wider gap for the clock. Four numbers (centre, pitch, middle gap, how much of a slot the art
/// fills) plus the top edge place every slot.
/// </summary>
public sealed record Geometry(double CenterX, double Pitch, double Top,
    double GapRatio = Layout.DefaultGapRatio, double WidthRatio = Layout.DefaultWidthRatio)
{
    public double ArtWidth => Pitch * WidthRatio;
    public double ArtHeight => ArtWidth * Layout.ArtAspect;

    public List<double> Centers()
    {
        var halfGap = Pitch * GapRatio;
        var centers = new List<double>(Layout.SlotCount);
        for (var i = 0; i < Layout.PerTeam; i++)
            centers.Add(CenterX - halfGap - (Layout.PerTeam - 1 - i) * Pitch);
        for (var j = 0; j < Layout.PerTeam; j++)
            centers.Add(CenterX + halfGap + j * Pitch);
        return centers;
    }

    public List<Box> Boxes()
    {
        var (w, h) = (ArtWidth, ArtHeight);
        return Centers().Select(cx => new Box(cx - w / 2.0, Top, w, h)).ToList();
    }

    public JsonObject ToJson() => new()
    {
        ["version"] = Layout.CacheVersion,
        ["center_x"] = CenterX,
        ["pitch"] = Pitch,
        ["top"] = Top,
        ["gap_ratio"] = GapRatio,
        ["width_ratio"] = WidthRatio,
    };

    /// <summary>
    /// A cached grid, or null for anything written by an older search: a grid from before a fix can
    /// be subtly wrong while still reading plausibly (the off-by-one-slot failure looked healthy).
    /// </summary>
    public static Geometry? FromJson(JsonObject? payload)
    {
        if (payload?["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != Layout.CacheVersion)
            return null;
        return Parse(payload);
    }

    /// <summary>A grid whatever search wrote it, or null when it doesn't parse.</summary>
    public static Geometry? Parse(JsonObject payload)
    {
        try
        {
            return new Geometry(
                payload["center_x"]!.GetValue<double>(),
                payload["pitch"]!.GetValue<double>(),
                payload["top"]!.GetValue<double>(),
                payload["gap_ratio"]?.GetValue<double>() ?? Layout.DefaultGapRatio,
                payload["width_ratio"]?.GetValue<double>() ?? Layout.DefaultWidthRatio);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }
}

/// <summary>
/// Finding the strip by search rather than measurement: project a candidate grid, match all twelve
/// slots against the template bank, keep whichever grid the heroes agree with best. A wrong grid
/// misaligns every slot at once and scores far below a right one, so the search validates itself.
/// Aggregates ignore the worst third of the slots: dead players are black silhouettes that match
/// nothing, and a plain mean would let four of them drag the geometry off the living.
/// </summary>
public static class Layout
{
    /// <summary>The reference art is 120×200, and the HUD scales it uniformly.</summary>
    public const double ArtAspect = 200.0 / 120.0;

    // Measured off a real capture; starting points for the search, not fixed truths.
    public const double DefaultGapRatio = 1.65;

    // The clock between the teams scales with the rest of the strip: every grid measured has had a gap
    // of 1.6 to 1.75 pitches. Letting it shrink much further lets a grid shifted a slot inward on both
    // sides pass for the real one, since ten of its twelve boxes still land on some portrait.
    public const double MinGapRatio = 1.35;
    public const double MaxGapRatio = 2.2;
    public const double DefaultWidthRatio = TopbarDerivation.WidthRatio;

    public const int SlotCount = 12;
    public const int PerTeam = 6;

    /// <summary>Bumped whenever a change to the search makes a cached grid untrustworthy.</summary>
    public const int CacheVersion = 3;

    public const double TrimFraction = 1.0 / 3.0;
    public const int PitchSteps = 20;
    public const int Finalists = 3;

    public static readonly double[] ScreenScales = [0.78, 0.90, 1.02, 1.15];
    public const double ScreenSpanX = 0.22;
    public const double ScreenSpanY = 0.24;

    public static readonly double[] RankScales = [0.82, 1.0, 1.18];
    public const double RankSpanX = 0.24;
    public const double RankSpanY = 0.26;
    public const int RankSamplesX = 5;
    public const int RankSamplesY = 7;

    /// <summary>A slot has to beat this before its position is trusted to steer a refit.</summary>
    public const double RefitMinScore = 0.32;

    /// <summary>Where to look, as fractions of the screen height (2560×1440 measured pitch 0.081, top 0.014).</summary>
    public const double PitchRatioMin = 0.050;
    public const double PitchRatioMax = 0.135;
    public const double TopRatioMax = 0.04;

    public const int ConvergeRounds = 2;

    public static readonly double[] SlotScales = [0.72, 0.79, 0.86, 0.93, 1.0, 1.08, 1.16, 1.26];
    public const double SlotSpanX = 0.30;
    public const double SlotSpanY = 0.30;
    public const int SlotSamplesX = 9;
    public const int SlotSamplesY = 11;

    public static RgbImage? Crop(RgbImage image, Box box)
    {
        var left = (int)Math.Round(box.X);
        var top = (int)Math.Round(box.Y);
        var right = (int)Math.Round(box.X + box.W);
        var bottom = (int)Math.Round(box.Y + box.H);
        if (left < 0 || top < 0 || right > image.Width || bottom > image.Height)
            return null;
        if (right - left < 8 || bottom - top < 8)
            return null;
        return image.Crop(left, top, right, bottom);
    }

    /// <summary>Mean of the best slots, ignoring the worst third.</summary>
    public static double TrimmedScore(IReadOnlyList<float> perSlot)
    {
        var keep = Math.Max(1, (int)Math.Round(perSlot.Count * (1.0 - TrimFraction)));
        var best = perSlot.OrderByDescending(v => v).Take(keep).ToArray();
        return NumpyMath.Mean(best);
    }

    /// <summary>
    /// Least-squares refit of the grid to where the slots landed. Each centre is linear in the
    /// unknowns (centre_i = center_x + side_i × half_gap + offset_i × pitch), so a handful of
    /// confidently placed slots pins the whole strip, including the middle gap the coarse search
    /// only approximates.
    /// </summary>
    public static Geometry FitGeometry(IReadOnlyList<double?> centers, IReadOnlyList<double> weights, Geometry template)
    {
        var rows = new List<double[]>();
        var targets = new List<double>();
        var sampleWeights = new List<double>();
        bool leftSeen = false, rightSeen = false;
        for (var index = 0; index < centers.Count && index < weights.Count; index++)
        {
            if (centers[index] is not { } center || weights[index] <= 0.0)
                continue;
            double side, offset;
            if (index < PerTeam)
            {
                (side, offset) = (-1.0, -(double)(PerTeam - 1 - index));
                leftSeen = true;
            }
            else
            {
                (side, offset) = (1.0, index - PerTeam);
                rightSeen = true;
            }
            rows.Add([1.0, side, offset]);
            targets.Add(center);
            sampleWeights.Add(weights[index]);
        }
        if (rows.Count < 3)
            return template;

        double centerX, pitch, gapRatio;
        if (!(leftSeen && rightSeen))
        {
            // Only one team is readable, so the middle gap can't be identified: hold it and fit the rest.
            var halfGap = template.Pitch * template.GapRatio;
            var design = rows.Select(row => new[] { row[0], row[2] }).ToList();
            var target = targets.Select((t, i) => t - rows[i][1] * halfGap).ToList();
            var solution = WeightedLeastSquares(design, target, sampleWeights);
            if (solution is null)
                return template;
            (centerX, pitch) = (solution[0], solution[1]);
            gapRatio = pitch <= 4 ? template.GapRatio : halfGap / pitch;
        }
        else
        {
            var solution = WeightedLeastSquares(rows, targets, sampleWeights);
            if (solution is null)
                return template;
            centerX = solution[0];
            var halfGap = solution[1];
            pitch = solution[2];
            gapRatio = pitch > 4 ? halfGap / pitch : template.GapRatio;
        }

        if (!double.IsFinite(centerX) || !double.IsFinite(pitch) || !double.IsFinite(gapRatio) || pitch <= 4)
            return template;
        // A refit that wild means the inputs were noise.
        if (pitch / template.Pitch is < 0.8 or > 1.25)
            return template;
        return template with { CenterX = centerX, Pitch = pitch, GapRatio = Math.Clamp(gapRatio, MinGapRatio, MaxGapRatio) };
    }

    /// <summary>Weighted least squares through the normal equations; null when the columns don't pin a solution.</summary>
    private static double[]? WeightedLeastSquares(List<double[]> design, List<double> targets, List<double> weights)
    {
        var n = design[0].Length;
        var normal = new double[n, n + 1];
        for (var row = 0; row < design.Count; row++)
        {
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                    normal[i, j] += weights[row] * design[row][i] * design[row][j];
                normal[i, n] += weights[row] * design[row][i] * targets[row];
            }
        }
        // Gaussian elimination with partial pivoting.
        for (var column = 0; column < n; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < n; row++)
            {
                if (Math.Abs(normal[row, column]) > Math.Abs(normal[pivot, column]))
                    pivot = row;
            }
            if (Math.Abs(normal[pivot, column]) < 1e-12)
                return null;
            for (var j = 0; j <= n; j++)
                (normal[column, j], normal[pivot, j]) = (normal[pivot, j], normal[column, j]);
            for (var row = 0; row < n; row++)
            {
                if (row == column)
                    continue;
                var factor = normal[row, column] / normal[column, column];
                for (var j = column; j <= n; j++)
                    normal[row, j] -= factor * normal[column, j];
            }
        }
        return Enumerable.Range(0, n).Select(i => normal[i, n] / normal[i, i]).ToArray();
    }

    private static double? WeightedMedian(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        var pairs = values.Zip(weights).Where(pair => pair.Second > 0.0)
            .OrderBy(pair => pair.First).ThenBy(pair => pair.Second).ToList();
        if (pairs.Count == 0)
            return null;
        var total = pairs.Sum(pair => pair.Second);
        var running = 0.0;
        foreach (var (value, weight) in pairs)
        {
            running += weight;
            if (running >= total / 2.0)
                return value;
        }
        return pairs[^1].First;
    }

    /// <summary>
    /// Refit how big the art is and where its top sits, off the boxes the slots chose. The search
    /// can't pin these alone (a too-small width ratio and a big trial scale score the same). A
    /// median, not a mean: your own slot is drawn bigger than the rest.
    /// </summary>
    public static Geometry FitShape(IReadOnlyList<Box> boxes, IReadOnlyList<double> weights, Geometry template)
    {
        var width = WeightedMedian(boxes.Select(b => b.W).ToList(), weights);
        var top = WeightedMedian(boxes.Select(b => b.Y).ToList(), weights);
        if (width is null || top is null || template.Pitch <= 4)
            return template;
        var ratio = width.Value / template.Pitch;
        if (ratio is < 0.30 or > 1.10 || top < 0)
            return template;
        return template with { WidthRatio = ratio, Top = top.Value };
    }

    /// <summary>One candidate per pitch, all at the same nominal top; the refinement window reaches the rest.</summary>
    public static List<Geometry> Candidates(int imageWidth, int imageHeight, (double Min, double Max) pitchRange, double top,
        int pitchSteps = PitchSteps)
    {
        var pitches = NumpyMath.Geomspace(Math.Max(pitchRange.Min, 4.0), Math.Max(pitchRange.Max, pitchRange.Min + 1.0), pitchSteps);
        var center = imageWidth / 2.0;
        var result = new List<Geometry>();
        foreach (var pitch in pitches)
        {
            var geometry = new Geometry(center, pitch, top);
            if (top + geometry.ArtHeight <= imageHeight && geometry.Centers()[0] > 0)
                result.Add(geometry);
        }
        return result;
    }

    public sealed record ScreenResult(double Score, List<Box> Boxes, float[] Peaks);

    /// <summary>
    /// Score a candidate grid the way the pipeline would read it: every slot refined first. Judging a
    /// grid by its nominal boxes flatters wrong grids, since Valve frames each hero slightly
    /// differently and a correct grid's slots sit a few pixels off.
    /// </summary>
    public static ScreenResult Screen(RgbImage image, TemplateBank bank, Geometry geometry,
        double[]? scales = null, double spanX = ScreenSpanX, double spanY = ScreenSpanY,
        int samplesX = SlotSamplesX, int samplesY = SlotSamplesY)
    {
        var (boxes, rows) = RefineSlots(image, bank, geometry.Boxes(), scales ?? ScreenScales, spanX, spanY, samplesX, samplesY);
        var peaks = rows.Select(row => row.Max()).ToArray();
        return new ScreenResult(TrimmedScore(peaks), boxes, peaks);
    }

    /// <summary>Every slot of a grid refined, in parallel; each is independent, so the answer doesn't depend on the order.</summary>
    public static (List<Box> Boxes, float[][] Scores) RefineSlots(RgbImage image, TemplateBank bank, IReadOnlyList<Box> slots,
        double[] scales, double spanX, double spanY, int samplesX = SlotSamplesX, int samplesY = SlotSamplesY)
    {
        var boxes = new Box[slots.Count];
        var scores = new float[slots.Count][];
        Parallel.For(0, slots.Count, index =>
        {
            (boxes[index], scores[index]) = RefineSlot(image, bank, slots[index], scales, spanX, spanY, samplesX, samplesY);
        });
        return (boxes.ToList(), scores);
    }

    public sealed record ConvergeResult(double Score, Geometry Geometry, List<Box> Boxes, float[] Peaks);

    /// <summary>
    /// Screen a grid, refit it to where its slots landed, screen again. A pitch a few per cent out
    /// scores badly as drawn but converges straight onto the truth once refit.
    /// </summary>
    public static ConvergeResult Converge(RgbImage image, TemplateBank bank, Geometry geometry, int rounds = ConvergeRounds)
    {
        var current = Screen(image, bank, geometry);
        for (var round = 0; round < rounds; round++)
        {
            var weights = current.Peaks.Select(p => Math.Max(0.0, (double)p - RefitMinScore)).ToList();
            var trial = FitGeometry(current.Boxes.Select(b => (double?)b.CenterX).ToList(), weights, geometry);
            trial = FitShape(current.Boxes, weights, trial);
            var trialResult = Screen(image, bank, trial);
            if (trialResult.Score <= current.Score)
                break;
            (geometry, current) = (trial, trialResult);
        }
        return new ConvergeResult(current.Score, geometry, current.Boxes, current.Peaks);
    }

    /// <summary>
    /// Locate the strip: a coarse pass ranks every candidate pitch, then the leading few converge and
    /// are judged on what they become rather than where they started (judging them as drawn once
    /// picked a pitch of 101 against a true 117, shifting the whole roster by a slot).
    /// <paramref name="referenceHeight"/> is the screen's height, which the HUD scales against; the
    /// image is usually just a band off the top of it.
    /// </summary>
    public static (Geometry Geometry, double Score)? Search(RgbImage image, TemplateBank bank,
        (double Min, double Max)? pitchRange = null, (double Min, double Max)? topRange = null,
        int? referenceHeight = null, IProgress<double>? progress = null)
    {
        if (bank.IsEmpty)
            return null;
        var height = referenceHeight ?? image.Height;
        var pitches = pitchRange ?? (PitchRatioMin * height, PitchRatioMax * height);
        var tops = topRange ?? (0.0, Math.Min(TopRatioMax * height, image.Height * 0.5));
        var nominalTop = 0.5 * (tops.Min + tops.Max);

        var candidates = Candidates(image.Width, image.Height, pitches, nominalTop);
        if (candidates.Count == 0)
            return null;

        var ranked = new List<(double Score, int Index, Geometry Geometry)>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var rank = Screen(image, bank, candidates[index], RankScales, RankSpanX, RankSpanY, RankSamplesX, RankSamplesY);
            ranked.Add((rank.Score, index, candidates[index]));
            progress?.Report(0.7 * (index + 1) / candidates.Count);
        }
        // A stable sort: equal scores keep the smaller pitch first.
        ranked = ranked.OrderByDescending(entry => entry.Score).ThenBy(entry => entry.Index).ToList();

        (double Score, Geometry Geometry)? best = null;
        var finalists = ranked.Take(Finalists).ToList();
        for (var index = 0; index < finalists.Count; index++)
        {
            var converged = Converge(image, bank, finalists[index].Geometry);
            if (best is null || converged.Score > best.Value.Score)
                best = (converged.Score, converged.Geometry);
            progress?.Report(0.7 + 0.3 * (index + 1) / finalists.Count);
        }
        progress?.Report(1.0);
        return (best!.Value.Geometry, best.Value.Score);
    }

    /// <summary>
    /// Score every hero against one slot, each at their own best-fitting box within a window. Per
    /// hero rather than per slot: taking the best box first and ranking heroes in it lets one hero's
    /// spurious peak choose the box every other hero is then judged in. Returns the winner's box and
    /// the per-hero scores.
    /// </summary>
    public static (Box Box, float[] Scores) RefineSlot(RgbImage image, TemplateBank bank, Box box,
        double[]? scales = null, double spanX = SlotSpanX, double spanY = SlotSpanY,
        int samplesX = SlotSamplesX, int samplesY = SlotSamplesY)
    {
        scales ??= SlotScales;
        var reachX = Math.Max(2, (int)Math.Round(box.W * spanX));
        var reachY = Math.Max(2, (int)Math.Round(box.H * spanY));
        var offsetsX = Offsets(reachX, samplesX);
        var offsetsY = Offsets(reachY, samplesY);

        var best = new float[bank.Heroes.Count];
        Array.Fill(best, -1.0f);
        var bestBoxes = new Box?[bank.Heroes.Count];
        foreach (var scale in scales)
        {
            var width = box.W * scale;
            var height = width * ArtAspect;
            foreach (var dx in offsetsX)
            {
                foreach (var dy in offsetsY)
                {
                    var trial = new Box(box.CenterX - width / 2.0 + dx, box.Y + dy, width, height);
                    if (Crop(image, trial) is not { } patch || ImageOps.Descriptor(patch) is not { } vector)
                        continue;
                    var scores = bank.Scores(vector);
                    for (var hero = 0; hero < scores.Length; hero++)
                    {
                        if (scores[hero] > best[hero])
                        {
                            best[hero] = scores[hero];
                            bestBoxes[hero] = trial;
                        }
                    }
                }
            }
        }

        if (best.Length == 0)
            return (box, best);
        var winner = Array.IndexOf(best, best.Max());
        var chosen = best[winner] > -1.0f ? bestBoxes[winner] : box;
        return (chosen ?? box, best);
    }

    /// <summary>np.unique(np.linspace(-reach, reach, samples).round().astype(int)).</summary>
    private static int[] Offsets(int reach, int samples) =>
        NumpyMath.Linspace(-reach, reach, Math.Max(1, samples))
            .Select(value => (int)Math.Round(value, MidpointRounding.ToEven))
            .Distinct()
            .Order()
            .ToArray();
}
