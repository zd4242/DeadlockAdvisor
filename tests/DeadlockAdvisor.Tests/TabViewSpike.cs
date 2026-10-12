using System.Globalization;
using System.Numerics;
using System.Text;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Tests.Ui;
using DeadlockAdvisor.Vision;
using SkiaSharp;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// WP24's measurements on captures of the Tab scoreboard. Not a test: it does nothing unless
/// DEADLOCK_TAB_SPIKE points at a folder of full-frame captures and DEADLOCK_ASSETS at an assets folder
/// holding the item icons (<c>items/</c>). It writes report.md and contact sheets under mockups/tab_spike
/// (git-ignored). Nothing here ships.
/// </summary>
public class TabViewSpike
{
    public static string OutDir => Path.Combine(UiHarness.RepoRoot(), "mockups", "tab_spike");

    // Where the item cells sit, in pitches: the columns from a slot's centre, the rows from the top of
    // the portraits (not the top of the screen: the spectator strip moves everything down together).
    private const double _columnLeft = -0.185;
    private const double _columnRight = 0.213;
    private const double _firstRow = 3.82;
    private const double _rowStep = 0.396;
    private const double _cellHalf = 0.155;
    private const int _rows = 6;
    private const int _cellsPerHero = 2 * _rows;

    // The three bars under a portrait, left to right: weapon, spirit, vitality.
    private static readonly double[] _barOffsets = [-0.125, 0.0, 0.135];
    private const double _trackTop = 2.17;
    private const double _trackBottom = 2.44;

    private const int _frame = 32;
    private const int _crop = 40;
    private const double _confidentScore = 0.70;
    private const double _confidentMargin = 0.08;
    private const double _cachedGridFit = 0.7;
    
    /// <summary>The bars of a hero with more unread cells than this are left out of the comparison with their souls (DEADLOCK_TAB_SPIKE_UNKNOWN).</summary>
    private static readonly int _maxUnknownForBars = int.TryParse(Environment.GetEnvironmentVariable("DEADLOCK_TAB_SPIKE_UNKNOWN"), out var allowed) ? allowed : 2;

    /// <summary>Items each hero carries, counted by eye on captures, hero by hero in slot order (the first six are the left team).</summary>
    private static readonly Dictionary<string, int[]> _handCounts = new()
    {
        ["test1.png"] = [11, 9, 10, 9, 9, 10, 10, 10, 10, 10, 10, 9],
        ["test5.png"] = [12, 12, 11, 11, 12, 11, 12, 12, 12, 12, 12, 12],
        ["test9.png"] = [9, 9, 9, 8, 9, 9, 9, 11, 11, 10, 10, 11],
    };

    /// <summary>DEADLOCK_TAB_SPIKE=&lt;folder of captures&gt;, DEADLOCK_ASSETS=&lt;assets folder&gt;: the whole spike.</summary>
    [Fact]
    public void MeasureTheTabView()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_TAB_SPIKE") is not { Length: > 0 } folder || UiHarness.ArtFolder is not { } assets)
            return;
        Directory.CreateDirectory(OutDir);
        var store = Golden.LoadStore();
        var icons = LoadIcons(Path.Combine(assets, "items"), store.Items.Values.ToList());
        var captures = Directory.GetFiles(folder, "*.png").Order(StringComparer.Ordinal)
            .Select(path => (Name: Path.GetFileName(path), Frame: ImageFile.Load(path))).ToList();

        var report = new StringBuilder("# Tab view spike\n\n");
        var heights = new[] { captures[0].Frame.Height, 1080, 900 };
        var nativePills = new Dictionary<string, IReadOnlyList<int?>>();
        foreach (var height in heights)
            report.Append(Measure(captures, height, icons, nativePills));
        File.WriteAllText(Path.Combine(OutDir, "report.md"), report.ToString());
    }

    private sealed record Icon(Item Item, RgbImage Art);

    private static List<Icon> LoadIcons(string folder, List<Item> items)
    {
        var icons = new List<Icon>();
        foreach (var item in items)
        {
            var path = Path.Combine(folder, item.ItemId + ".png");
            if (!File.Exists(path))
                continue;
            var (image, alpha) = ImageFile.LoadWithAlpha(path);
            icons.Add(new Icon(item, ImageOps.Composite(image, alpha, (40, 40, 40))));
        }
        return icons;
    }

    private sealed record Cell(float[] Scores, double Hue, double Saturation, RgbImage Crop);

    private sealed record Hero(string Capture, int Slot, Detection Detection, Cell[] Cells, double OffsetX, double OffsetY);

    private sealed record Read(Item? Item, double Score, double Margin, string? PipCategory, bool Confident, RgbImage Crop, RgbImage? Art);

    private static string Measure(List<(string Name, RgbImage Frame)> captures, int height, List<Icon> icons, Dictionary<string, IReadOnlyList<int?>> nativePills)
    {
        var report = new StringBuilder();
        var scale = (double)height / captures[0].Frame.Height;
        report.AppendLine(CultureInfo.InvariantCulture, $"## {height}p (captures shrunk by {scale:0.000})\n");

        // Find the grid the way the app does: one cached grid, searched afresh when it fits under 0.7.
        var detections = new List<(string Name, RgbImage Frame, RgbImage Band, Detection Detection)>();
        Geometry? cached = null;
        foreach (var (name, original) in captures)
        {
            var frame = scale == 1.0 ? original : original.Resize((int)Math.Round(original.Width * scale), height, ResampleFilter.Bilinear);
            var band = frame.Crop(0, 0, frame.Width, (int)(frame.Height * ScreenCaptureService.BandFraction));
            var detection = Detector.Detect(band, Bank, cached, screenHeight: height);
            if (detection is null || detection.Fit < _cachedGridFit)
            {
                var searched = Detector.Detect(band, Bank, screenHeight: height);
                if (searched is not null && (detection is null || searched.Fit > detection.Fit))
                    detection = searched;
            }
            if (detection is null)
            {
                report.AppendLine($"- {name}: nothing found");
                continue;
            }
            if (detection.Fit >= _cachedGridFit)
                cached = detection.Geometry;
            detections.Add((name, frame, band, detection));
            var worth = NetWorthReader.Read(band, detection.Geometry, NetWorthGlyphs.Bundled);
            nativePills.TryAdd(name, worth.Pills);
            var wrong = worth.Pills.Zip(nativePills[name]).Count(pair => pair.First is not null && pair.Second is not null && pair.First != pair.Second);
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- {name}: fit {detection.Fit:0.00}, {detection.ConfidentCount}/12 portraits confident, pitch {detection.Geometry.Pitch:0.0}, top {detection.Geometry.Top:0.0}, {worth.Pills.Count(p => p is not null)}/12 net worth pills read, {wrong} differing from the full-size read, team totals {string.Join("/", worth.Totals.Select(total => total is { } souls ? $"{souls / 1000}k" : "-"))}"));
        }

        var pitch = detections.Average(d => d.Detection.Geometry.Pitch);
        var frames = Frames(icons, pitch);
        var heroes = new List<Hero>();
        foreach (var (name, frame, _, detection) in detections)
        {
            var slots = new Hero?[Layout.SlotCount];
            Parallel.For(0, Layout.SlotCount, slot => slots[slot] = ReadHero(name, frame, detection, slot, frames));
            heroes.AddRange(slots!);
        }

        report.Append(CellOffsets(heroes));
        var reads = Identify(heroes, icons);
        report.Append(Occupancy(heroes, reads));
        report.Append(Identity(heroes, reads, height, icons));
        report.Append(Bars(detections, heroes, reads, height));
        return report.ToString();
    }

    // -- sampling and matching ------------------------------------------------------------

    /// <summary>A square of the image, <paramref name="side"/> pixels wide around (cx, cy), as size×size RGB, averaged as it shrinks.</summary>
    private static RgbImage Sample(RgbImage image, double cx, double cy, double side, int size)
    {
        var output = new RgbImage(size, size);
        var step = side / size;
        var taps = Math.Max(1, (int)Math.Ceiling(step));
        for (var oy = 0; oy < size; oy++)
        {
            for (var ox = 0; ox < size; ox++)
            {
                double r = 0, g = 0, b = 0;
                for (var ty = 0; ty < taps; ty++)
                {
                    for (var tx = 0; tx < taps; tx++)
                    {
                        var (sr, sg, sb) = Bilinear(image, cx - side / 2 + (ox + (tx + 0.5) / taps) * step, cy - side / 2 + (oy + (ty + 0.5) / taps) * step);
                        (r, g, b) = (r + sr, g + sg, b + sb);
                    }
                }
                var at = (oy * size + ox) * 3;
                var n = taps * taps;
                (output.Pixels[at], output.Pixels[at + 1], output.Pixels[at + 2]) = ((byte)Math.Round(r / n), (byte)Math.Round(g / n), (byte)Math.Round(b / n));
            }
        }
        return output;
    }

    private static (double R, double G, double B) Bilinear(RgbImage image, double x, double y)
    {
        var (u, v) = (x - 0.5, y - 0.5);
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        double fx = u - x0, fy = v - y0;
        double r = 0, g = 0, b = 0;
        for (var dy = 0; dy < 2; dy++)
        {
            for (var dx = 0; dx < 2; dx++)
            {
                var weight = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                var (pr, pg, pb) = image[Math.Clamp(x0 + dx, 0, image.Width - 1), Math.Clamp(y0 + dy, 0, image.Height - 1)];
                (r, g, b) = (r + weight * pr, g + weight * pg, b + weight * pb);
            }
        }
        return (r, g, b);
    }

    // The icon without its tier corner (the game draws that over it) and without the strip "ACTIVE" is
    // printed on, which only some items carry.
    private static readonly bool[] _keep = BuildMask();

    private static bool[] BuildMask()
    {
        var keep = new bool[_frame * _frame];
        for (var y = 0; y < _frame; y++)
        {
            for (var x = 0; x < _frame; x++)
            {
                double xf = (x + 0.5) / _frame, yf = (y + 0.5) / _frame;
                keep[y * _frame + x] = xf + (1 - yf) < 1.55 && !(yf > 0.80 && xf > 0.1 && xf < 0.9) && xf is > 0.06 and < 0.94 && yf is > 0.06 and < 0.94;
            }
        }
        return keep;
    }

    /// <summary>Each channel's mean taken off over the kept pixels, then scaled to unit length together: a dot product of two is a correlation.</summary>
    private static float[] Normalise(RgbImage frame)
    {
        var kept = _keep.Count(k => k);
        var vector = new float[kept * 3];
        var at = 0;
        for (var i = 0; i < _keep.Length; i++)
        {
            if (!_keep[i])
                continue;
            for (var c = 0; c < 3; c++)
                vector[c * kept + at] = frame.Pixels[i * 3 + c];
            at++;
        }
        double squares = 0;
        for (var c = 0; c < 3; c++)
        {
            var mean = vector.AsSpan(c * kept, kept).ToArray().Average();
            for (var i = 0; i < kept; i++)
            {
                vector[c * kept + i] -= (float)mean;
                squares += vector[c * kept + i] * (double)vector[c * kept + i];
            }
        }
        var scale = (float)(1.0 / (Math.Sqrt(squares) + 1e-6));
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= scale;
        return vector;
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            sum += new Vector<float>(a, i) * new Vector<float>(b, i);
        var total = Vector.Sum(sum);
        for (; i < a.Length; i++)
            total += a[i] * b[i];
        return total;
    }

    /// <summary>Every icon as the game would show it at this screen size: shrunk to a cell's own pixels, then framed.</summary>
    private static float[][] Frames(List<Icon> icons, double pitch)
    {
        var native = Math.Max(8, (int)Math.Round(2 * _cellHalf * pitch));
        return icons.Select(icon =>
        {
            var small = ImageOps.ResizeRgb(icon.Art, native, native);
            return Normalise(Sample(small, native / 2.0, native / 2.0, native, _frame));
        }).ToArray();
    }

    // -- one hero's cells -------------------------------------------------------------------

    private static (double X, double Y) CellCentre(Geometry grid, int slot, int row, int column) =>
        (grid.Centers()[slot] + (column == 0 ? _columnLeft : _columnRight) * grid.Pitch, grid.Top + (_firstRow + _rowStep * row) * grid.Pitch);

    private static float[] Scores(RgbImage frame, Geometry grid, int slot, int row, int column, double dx, double dy, float[][] frames)
    {
        var (x, y) = CellCentre(grid, slot, row, column);
        var vector = Normalise(Sample(frame, x + dx, y + dy, 2 * _cellHalf * grid.Pitch, _frame));
        return frames.Select(f => Dot(vector, f)).ToArray();
    }

    private static Hero ReadHero(string capture, RgbImage frame, Detection detection, int slot, float[][] frames)
    {
        var grid = detection.Geometry;
        var pitch = grid.Pitch;

        // The grid is found from the portraits and is a few pixels off down here: slide the four top rows
        // about until they match best, and read from where they fit.
        (double Value, double X, double Y) best = (double.MinValue, 0, 0);
        foreach (var dx in Enumerable.Range(0, 9).Select(i => (-0.05 + 0.0125 * i) * pitch))
        {
            foreach (var dy in Enumerable.Range(0, 9).Select(i => (-0.06 + 0.015 * i) * pitch))
            {
                var peaks = new List<float>();
                for (var row = 0; row < 4; row++)
                {
                    for (var column = 0; column < 2; column++)
                        peaks.Add(Scores(frame, grid, slot, row, column, dx, dy, frames).Max());
                }
                var value = peaks.Order().TakeLast(5).Average();
                if (value > best.Value)
                    best = (value, dx, dy);
            }
        }

        var cells = new Cell[_cellsPerHero];
        for (var k = 0; k < cells.Length; k++)
        {
            var (row, column) = (k / 2, k % 2);
            float[]? top = null;
            foreach (var jx in new[] { -0.008, 0, 0.008 })
            {
                foreach (var jy in new[] { -0.008, 0, 0.008 })
                {
                    var scores = Scores(frame, grid, slot, row, column, best.X + jx * pitch, best.Y + jy * pitch, frames);
                    top = top is null ? scores : top.Zip(scores, Math.Max).ToArray();
                }
            }
            var (x, y) = CellCentre(grid, slot, row, column);
            var crop = Sample(frame, x + best.X, y + best.Y, 2 * _cellHalf * pitch, _crop);
            var (hue, saturation) = PipColour(crop);
            cells[k] = new Cell(top!, hue, saturation, crop);
        }
        return new Hero(capture, slot, detection, cells, best.X / pitch, best.Y / pitch);
    }

    /// <summary>Hue and saturation of the tier corner: the most saturated half of its pixels, medianed.</summary>
    private static (double Hue, double Saturation) PipColour(RgbImage crop)
    {
        var pixels = new List<(double Hue, double Saturation, double Value)>();
        for (var y = 0; y < _crop; y++)
        {
            for (var x = 0; x < _crop; x++)
            {
                if ((x + 0.5) / _crop + (1 - (y + 0.5) / _crop) < 1.62)
                    continue;
                var (r, g, b) = crop[x, y];
                pixels.Add(ImageOps.Hsv([r, g, b]));
            }
        }
        var strongest = pixels.OrderByDescending(p => p.Saturation * p.Value).Take(Math.Max(4, pixels.Count / 2)).ToList();
        return (Median(strongest.Select(p => p.Hue)), Median(strongest.Select(p => p.Saturation)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }

    /// <summary>The shop category the corner's colour says: orange weapon, green vitality, purple spirit.</summary>
    private static string? PipCategory(double hue, double saturation)
    {
        if (saturation < 0.13)
            return null;
        if (hue is >= 16 and < 50 && saturation >= 0.3)
            return "weapon";
        if (hue is >= 50 and <= 100 && saturation >= 0.4)
            return "vitality";
        return hue is >= 230 and <= 340 ? "spirit" : null;
    }

    // -- reading the cells -------------------------------------------------------------------

    /// <summary>The split of an array of evidence (positive where something is) that agrees best: that many cells from the start are occupied.</summary>
    private static int BestPrefix(IReadOnlyList<double> evidence)
    {
        var (bestCount, bestValue) = (0, double.MinValue);
        for (var n = 0; n <= evidence.Count; n++)
        {
            var value = evidence.Take(n).Sum() - evidence.Skip(n).Sum();
            if (value > bestValue)
                (bestCount, bestValue) = (n, value);
        }
        return bestCount;
    }

    /// <summary>The score that best splits a capture's cells into those with an icon and those without (Otsu's split, over 0.3 to 0.8).</summary>
    private static double SplitScore(IReadOnlyList<double> scores)
    {
        var (bestSpread, bestThreshold) = (-1.0, 0.5);
        for (var step = 0; step <= 100; step++)
        {
            var threshold = 0.3 + 0.005 * step;
            var low = scores.Where(s => s <= threshold).ToList();
            var high = scores.Where(s => s > threshold).ToList();
            if (low.Count < 5 || high.Count < 5)
                continue;
            var spread = low.Count * (double)high.Count * Math.Pow(low.Average() - high.Average(), 2);
            if (spread > bestSpread)
                (bestSpread, bestThreshold) = (spread, threshold);
        }
        return bestThreshold;
    }

    /// <summary>Per hero, the icons it carries: how many cells are filled, and which item is in each.</summary>
    private static Dictionary<(string Capture, int Slot), Read[]> Identify(List<Hero> heroes, List<Icon> icons)
    {
        var reads = new Dictionary<(string, int), Read[]>();
        foreach (var capture in heroes.GroupBy(h => h.Capture))
        {
            var threshold = SplitScore(capture.SelectMany(h => h.Cells.Select(c => (double)c.Scores.Max())).ToList());
            foreach (var hero in capture)
            {
                var evidence = hero.Cells.Select(c => c.Scores.Max() - threshold + 0.2 * (PipCategory(c.Hue, c.Saturation) is null ? -1 : 1)).ToList();
                var count = BestPrefix(evidence);
                var cells = hero.Cells.Take(count).ToList();
                var candidates = cells.Select(c =>
                {
                    var pip = PipCategory(c.Hue, c.Saturation);
                    return c.Scores.Select((score, i) => pip is not null && icons[i].Item.Category != pip ? -1f : score).ToArray();
                }).ToList();

                // An item can be owned once: the cell that matches an icon best keeps it.
                var assigned = new int[count];
                var used = new HashSet<int>();
                var open = Enumerable.Range(0, count).ToHashSet();
                while (open.Count > 0)
                {
                    var (cell, icon, score) = open.SelectMany(k => Enumerable.Range(0, icons.Count).Where(i => !used.Contains(i)).Select(i => (k, i, candidates[k][i])))
                        .MaxBy(t => t.Item3);
                    assigned[cell] = icon;
                    used.Add(icon);
                    open.Remove(cell);
                }

                reads[(hero.Capture, hero.Slot)] = cells.Select((c, k) =>
                {
                    var score = candidates[k][assigned[k]];
                    var margin = score - candidates[k].Where((_, i) => i != assigned[k]).Max();
                    return new Read(icons[assigned[k]].Item, score, margin, PipCategory(c.Hue, c.Saturation), score >= _confidentScore && margin >= _confidentMargin, c.Crop, icons[assigned[k]].Art);
                }).ToArray();
            }
        }
        return reads;
    }

    // -- the report ---------------------------------------------------------------------------

    private static string CellOffsets(List<Hero> heroes)
    {
        var report = new StringBuilder("\n### Item cells against the premise numbers\n\nHow far each hero's cells had to slide from the premise positions to fit best, in pitches (dx, dy), median per capture and over all:\n\n");
        foreach (var capture in heroes.GroupBy(h => h.Capture))
            report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {capture.Key}: dx {Median(capture.Select(h => h.OffsetX)):0.000}, dy {Median(capture.Select(h => h.OffsetY)):0.000}"));
        var dx = heroes.Select(h => h.OffsetX).Order().ToList();
        var dy = heroes.Select(h => h.OffsetY).Order().ToList();
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- all: dx median {Median(dx):0.000} (5th {dx[dx.Count * 5 / 100]:0.000}, 95th {dx[dx.Count * 95 / 100]:0.000}); dy median {Median(dy):0.000} (5th {dy[dy.Count * 5 / 100]:0.000}, 95th {dy[dy.Count * 95 / 100]:0.000})"));
        return report.ToString();
    }

    private static string Occupancy(List<Hero> heroes, Dictionary<(string Capture, int Slot), Read[]> reads)
    {
        var report = new StringBuilder("\n### Cells filled per hero\n\n");
        int exact = 0, near = 0, total = 0, truly = 0, sure = 0, unsure = 0, missed = 0, extra = 0;
        foreach (var (capture, counts) in _handCounts)
        {
            if (!heroes.Any(h => h.Capture == capture))
                continue;
            for (var slot = 0; slot < Layout.SlotCount; slot++)
            {
                var read = reads[(capture, slot)];
                var difference = Math.Abs(read.Length - counts[slot]);
                total++;
                exact += difference == 0 ? 1 : 0;
                near += difference <= 1 ? 1 : 0;
                truly += counts[slot];
                sure += read.Take(counts[slot]).Count(r => r.Confident);
                unsure += read.Take(counts[slot]).Count(r => !r.Confident);
                missed += Math.Max(0, counts[slot] - read.Length);
                extra += Math.Max(0, read.Length - counts[slot]);
            }
        }
        report.AppendLine($"Against the counts made by eye on {_handCounts.Count} captures: {exact} of {total} heroes exactly right, {near} within one.");
        report.AppendLine($"Of the {truly} cells that really hold an item there: {sure} read confidently, {unsure} read as an unknown item, {missed} read as empty; {extra} empty cells were read as filled.");
        return report.ToString();
    }

    private static string Identity(List<Hero> heroes, Dictionary<(string Capture, int Slot), Read[]> reads, int height, List<Icon> icons)
    {
        var report = new StringBuilder("\n### Which item, and its category\n\n");
        var all = reads.Values.SelectMany(r => r).ToList();
        var confident = all.Where(r => r.Confident).ToList();
        var classed = all.Count(r => r.PipCategory is not null);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"{all.Count} filled cells: {confident.Count} confident ({100.0 * confident.Count / all.Count:0}%), {all.Count - confident.Count} unknown. The corner colour was classed on {classed}.");

        // The corner colour against icons matched without being told the category: a strong match of any item.
        var strong = heroes.SelectMany(h => h.Cells.Take(reads[(h.Capture, h.Slot)].Length)).Select(c => (Cell: c, Order: c.Scores.Select((s, i) => (s, i)).OrderByDescending(p => p.s).Take(2).ToArray()))
            .Where(c => c.Order[0].s >= 0.8f && c.Order[0].s - c.Order[1].s >= 0.08f).ToList();
        var agree = strong.Count(c => PipCategory(c.Cell.Hue, c.Cell.Saturation) == icons[c.Order[0].i].Item.Category);
        report.AppendLine(CultureInfo.InvariantCulture, $"Matched to any item, with no help from the corner, strongly (0.80, lead 0.08): {strong.Count} cells; the corner colour names the matched item's category on {agree}.");

        // Sheets for checking by eye: the cell, the icon it was matched to, and how sure.
        var random = new Random(24);
        var sample = confident.OrderBy(_ => random.Next()).ToList();
        SaveSheet(sample.Take(60).ToList(), Path.Combine(OutDir, $"sheet_{height}_confident.png"));
        SaveSheet(sample.Skip(60).Take(60).ToList(), Path.Combine(OutDir, $"sheet_{height}_confident_2.png"));
        SaveSheet(all.Where(r => !r.Confident).OrderBy(_ => random.Next()).Take(60).ToList(), Path.Combine(OutDir, $"sheet_{height}_unknown.png"));

        report.AppendLine("\nShare of filled cells read confidently, by the score and lead required:\n");
        foreach (var score in new[] { 0.55, 0.60, 0.65, 0.70 })
        {
            var shares = new[] { 0.05, 0.08, 0.10, 0.15 }
                .Select(lead => FormattableString.Invariant($"lead {lead:0.00} {100.0 * all.Count(r => r.Score >= score && r.Margin >= lead) / all.Count:0}%"));
            report.AppendLine(FormattableString.Invariant($"- score {score:0.00}: ") + string.Join(", ", shares));
        }
        var band = all.Where(r => !r.Confident && r.Score >= 0.55 && r.Margin >= 0.10).OrderBy(_ => random.Next()).ToList();
        SaveSheet(band.Take(60).ToList(), Path.Combine(OutDir, $"sheet_{height}_band.png"));
        report.AppendLine($"\n{band.Count} cells sit between 0.55 and 0.70 with a lead of 0.10 or more (sheet_{height}_band.png).");
        return report.ToString();
    }

    private static string Bars(List<(string Name, RgbImage Frame, RgbImage Band, Detection Detection)> detections, List<Hero> heroes,
        Dictionary<(string Capture, int Slot), Read[]> reads, int height)
    {
        var points = new List<(string Category, double Fill, int Souls, string Where)>();
        foreach (var (name, frame, _, detection) in detections)
        {
            for (var slot = 0; slot < Layout.SlotCount; slot++)
            {
                var read = reads[(name, slot)];
                if (read.Count(r => !r.Confident) > _maxUnknownForBars)
                    continue;
                for (var bar = 0; bar < 3; bar++)
                {
                    var category = bar == 0 ? "weapon" : bar == 1 ? "spirit" : "vitality";
                    if (BarFill(frame, detection.Geometry, slot, bar) is { } fill)
                        points.Add((category, fill, read.Where(r => r.Item!.Category == category).Sum(r => r.Item!.Cost), $"{name}:{slot}"));
                }
            }
        }
        var report = new StringBuilder("\n### The three bars against the souls in each category\n\n");
        report.AppendLine($"Bars of heroes with at most {_maxUnknownForBars} unread cells: {points.Count}.");
        File.WriteAllLines(Path.Combine(OutDir, $"bars_{height}.csv"), points.Select(p => FormattableString.Invariant($"{p.Category},{p.Souls},{p.Fill:0.000},{p.Where}")));
        foreach (var group in points.GroupBy(p => p.Category))
        {
            var xs = group.Select(p => (double)p.Souls).ToList();
            var ys = group.Select(p => p.Fill).ToList();
            report.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key}: r = {Correlation(xs, ys):0.00} over {xs.Count} bars");
            foreach (var band in group.GroupBy(p => Math.Min(p.Souls / 2000, 8)).OrderBy(b => b.Key))
                report.AppendLine(CultureInfo.InvariantCulture, $"    souls {band.Key * 2000}-{band.Key * 2000 + 1999}: mean fill {band.Average(p => p.Fill):0.00} (n={band.Count()})");
        }
        return report.ToString();
    }

    /// <summary>How full a bar is, 0 to 1: its lit run from the bottom of the track, as a share of the track.</summary>
    private static double? BarFill(RgbImage frame, Geometry grid, int slot, int bar)
    {
        var x = grid.Centers()[slot] + _barOffsets[bar] * grid.Pitch;
        int x0 = (int)Math.Round(x - 0.02 * grid.Pitch), x1 = (int)Math.Round(x + 0.02 * grid.Pitch);
        int y0 = (int)Math.Round(grid.Top + 2.10 * grid.Pitch), y1 = (int)Math.Round(grid.Top + 2.56 * grid.Pitch);
        if (x0 < 0 || x1 >= frame.Width || y1 >= frame.Height)
            return null;
        int? first = null;
        for (var y = y0; y < y1; y++)
        {
            var lit = 0;
            for (var px = x0; px <= x1; px++)
            {
                var (r, g, b) = frame[px, y];
                // The lit part is pale against a dark track; its hue is washed out too much by compression to go by.
                var on = 0.299 * r + 0.587 * g + 0.114 * b >= 140;
                lit += on ? 1 : 0;
            }
            if (lit * 2 > x1 - x0 + 1)
            {
                first = y;
                break;
            }
        }
        if (first is not { } top)
            return 0.0;
        var track = (_trackBottom - _trackTop) * grid.Pitch;
        return Math.Clamp((grid.Top + _trackBottom * grid.Pitch - top) / track, 0, 1);
    }

    private static double Correlation(List<double> xs, List<double> ys)
    {
        double mx = xs.Average(), my = ys.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            sxy += (xs[i] - mx) * (ys[i] - my);
            sxx += (xs[i] - mx) * (xs[i] - mx);
            syy += (ys[i] - my) * (ys[i] - my);
        }
        return sxx == 0 || syy == 0 ? double.NaN : sxy / Math.Sqrt(sxx * syy);
    }

    private static void SaveSheet(List<Read> reads, string path)
    {
        const int tile = 80, caption = 30, perRow = 6;
        var rows = (reads.Count + perRow - 1) / perRow;
        using var surface = SKSurface.Create(new SKImageInfo(perRow * (2 * tile + 4), Math.Max(1, rows) * (tile + caption)));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(24, 24, 28));
        using var font = new SKFont(SKTypeface.Default, 11);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var i = 0; i < reads.Count; i++)
        {
            var (x, y) = (i % perRow * (2 * tile + 4), i / perRow * (tile + caption));
            Draw(canvas, reads[i].Crop, x, y, tile);
            if (reads[i].Art is { } art)
                Draw(canvas, art, x + tile, y, tile);
            canvas.DrawText(FormattableString.Invariant($"{reads[i].Item?.ItemId}"), x + 2, y + tile + 12, font, text);
            canvas.DrawText(FormattableString.Invariant($"{reads[i].Score:0.00} +{reads[i].Margin:0.00} {reads[i].PipCategory}"), x + 2, y + tile + 25, font, text);
        }
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void Draw(SKCanvas canvas, RgbImage image, int x, int y, int size)
    {
        var scaled = image.Resize(size, size, ResampleFilter.Bilinear);
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            pixels[i * 4] = scaled.Pixels[i * 3];
            pixels[i * 4 + 1] = scaled.Pixels[i * 3 + 1];
            pixels[i * 4 + 2] = scaled.Pixels[i * 3 + 2];
            pixels[i * 4 + 3] = 255;
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        canvas.DrawBitmap(bitmap, x, y);
    }
}
