using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>
/// Measuring where each reference image's art sits in a slot, off labelled captures: each true
/// hero's images are searched for widely in their slot, then the images' frames and each capture's
/// grid are refined in turn, since a capture's saved grid was itself fitted to how that roster's
/// art happened to sit.
/// </summary>
public static class VisionCalibration
{
    /// <summary>Where art sits in a slot, in pitches: its centre from the slot's, its top from the strip's, and its width.</summary>
    public readonly record struct Offset(double Dx, double Dy, double Scale);

    /// <summary>A true hero's image found in its slot: the box it scored best in, and how well.</summary>
    public sealed record Sample(CorpusCapture Capture, int Slot, int Row, Box Box, double Score);

    /// <summary>A measured image must score this well at its best: anything less is a poor match whose best box means little.</summary>
    public const double MinScore = 0.4;

    private static readonly SlotState[] _normalArt = [SlotState.Visible, SlotState.Faded];

    public static List<Sample> Measure(IEnumerable<CorpusCapture> captures, TemplateBank bank)
    {
        var samples = new List<Sample>();
        foreach (var capture in captures)
        {
            if (capture.Labels.Grid is not { } grid)
                continue;
            var boxes = grid.Boxes();
            var jobs = capture.Labels.Heroes.Where(pair => _normalArt.Contains(capture.Labels.StateOf(pair.Key))).ToList();
            var found = new Sample[jobs.Count][];
            Parallel.For(0, jobs.Count, i =>
            {
                var (slot, hero) = jobs[i];
                var rows = Enumerable.Range(0, bank.Vectors.Count)
                    .Where(row => bank.Heroes[bank.RowsHero[row]] == hero && bank.Sources[row].State == PortraitState.Normal)
                    .ToList();
                found[i] = Find(capture.Image, bank, rows, boxes[slot], grid.Pitch)
                    .Where(result => result.Score >= MinScore)
                    .Select(result => new Sample(capture, slot, result.Row, result.Box, result.Score))
                    .ToArray();
            });
            samples.AddRange(found.SelectMany(list => list));
        }
        return samples;
    }

    private static IEnumerable<(int Row, Box Box, double Score)> Find(RgbImage image, TemplateBank bank, List<int> rows, Box around, double pitch)
    {
        // Coarse: every scale and a grid of positions a good way around the slot, each crop scored against every image.
        var best = rows.ToDictionary(row => row, _ => (Box: around, Score: -1.0));
        for (var scale = 0.70; scale <= 1.40; scale += 0.05)
        {
            var width = around.W * scale;
            for (var dx = -0.35; dx <= 0.35 + 1e-9; dx += 0.05)
            {
                for (var dy = -0.40; dy <= 0.40 + 1e-9; dy += 0.05)
                {
                    var box = new Box(around.CenterX + dx * pitch - width / 2, around.Y + dy * pitch, width, width * Layout.ArtAspect);
                    if (Layout.Crop(image, box) is not { } patch || ImageOps.Descriptor(patch) is not { } vector)
                        continue;
                    foreach (var row in rows)
                    {
                        var score = bank.Score(vector, row);
                        if (score > best[row].Score)
                            best[row] = (box, score);
                    }
                }
            }
        }
        return rows.Select(row => Climb(image, bank, row, best[row], pitch));
    }

    private static (int Row, Box Box, double Score) Climb(RgbImage image, TemplateBank bank, int row, (Box Box, double Score) best, double pitch)
    {
        double Score(Box box) => Layout.Crop(image, box) is { } patch && ImageOps.Descriptor(patch) is { } vector ? bank.Score(vector, row) : -1;

        // Fine: climb in shrinking steps of position and size.
        foreach (var step in new[] { 0.02, 0.01, 0.005 })
        {
            for (var round = 0; round < 20; round++)
            {
                var improved = false;
                foreach (var (dx, dy, dw) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    var width = best.Box.W + dw * step * pitch;
                    var box = new Box(best.Box.CenterX + dx * step * pitch - width / 2, best.Box.Y + dy * step * pitch, width, width * Layout.ArtAspect);
                    var score = Score(box);
                    if (score > best.Score)
                    {
                        best = (box, score);
                        improved = true;
                    }
                }
                if (!improved)
                    break;
            }
        }
        return (row, best.Box, best.Score);
    }

    /// <summary>
    /// Frames for every image measured, from <paramref name="samples"/> (leave a match's out to test on
    /// it honestly). An image's frame is the median of where it sat; each capture's grid is refitted to
    /// the frames, and the two alternate. The frames are then shifted so the typical one sits at the
    /// slot's centre and the strip's top, and the typical width becomes the grid's.
    /// </summary>
    public static (double WidthRatio, Dictionary<int, (Offset Frame, int Samples)> Frames) Fit(IReadOnlyList<Sample> samples)
    {
        var grids = samples.Select(sample => sample.Capture).Distinct().ToDictionary(capture => capture, capture => capture.Labels.Grid!);
        var frames = new Dictionary<int, (Offset Frame, int Samples)>();
        for (var round = 0; round < 5; round++)
        {
            frames = samples.GroupBy(sample => sample.Row).ToDictionary(group => group.Key, group =>
            {
                var offsets = group.Select(sample => OffsetOf(sample, grids[sample.Capture])).ToList();
                return (new Offset(Median(offsets.Select(o => o.Dx)), Median(offsets.Select(o => o.Dy)), Median(offsets.Select(o => o.Scale))),
                    offsets.Count);
            });

            // The typical frame defines the slot: its centre, the strip's top, and the art's width.
            var (centre, top) = (Median(frames.Values.Select(f => f.Frame.Dx)), Median(frames.Values.Select(f => f.Frame.Dy)));
            frames = frames.ToDictionary(pair => pair.Key,
                pair => (pair.Value.Frame with { Dx = pair.Value.Frame.Dx - centre, Dy = pair.Value.Frame.Dy - top }, pair.Value.Samples));

            foreach (var capture in grids.Keys.ToList())
            {
                var grid = grids[capture];
                var mine = samples.Where(sample => sample.Capture == capture && frames.ContainsKey(sample.Row))
                    .GroupBy(sample => sample.Slot)
                    .Select(group => group.MaxBy(sample => sample.Score)!)
                    .ToList();
                if (mine.Count < 3)
                    continue;
                var centers = new double?[Layout.SlotCount];
                var weights = new double[Layout.SlotCount];
                foreach (var sample in mine)
                {
                    centers[sample.Slot] = sample.Box.CenterX - frames[sample.Row].Frame.Dx * grid.Pitch;
                    weights[sample.Slot] = sample.Score;
                }
                var fitted = Layout.FitGeometry(centers, weights, grid);
                var canonicalTop = Median(mine.Select(sample => sample.Box.Y - frames[sample.Row].Frame.Dy * fitted.Pitch));
                grids[capture] = fitted with { Top = canonicalTop };
            }
        }
        var width = Median(frames.Values.Select(f => f.Frame.Scale));
        return (width, frames);
    }

    private static Offset OffsetOf(Sample sample, Geometry grid)
    {
        var pitch = grid.Pitch;
        return new Offset((sample.Box.CenterX - grid.Centers()[sample.Slot]) / pitch, (sample.Box.Y - grid.Top) / pitch, sample.Box.W / pitch);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
