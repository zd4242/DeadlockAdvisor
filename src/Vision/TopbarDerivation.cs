using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeadlockAdvisor.Vision;

/// <summary>
/// Top-bar portraits cut from the API's hero cards. The API's own top-bar art is stale for some
/// heroes and doesn't exist at all for the critical and on-fire portraits, but the cards are current
/// and come in all three states, so the portraits are cut from them where the top bar crops them.
/// <para>
/// Where the top bar crops a card is found by fitting the API's top-bar art inside the hero's normal
/// card. A hero whose top-bar art no longer fits their card (it's the stale art) takes the median
/// crop of the heroes that fit, which vary by only a few per cent. What's derived from what is kept
/// in topbar/_derived.json, so a hero is only cut again when one of their images changes.
/// </para>
/// </summary>
public static class TopbarDerivation
{
    /// <summary>Bumped whenever a change here makes portraits cut before it wrong.</summary>
    public const int Version = 1;

    public const string StateFile = "_derived.json";

    /// <summary>A fit this close is the art the top bar was cut from.</summary>
    public const double AcceptScore = 0.90;

    /// <summary>A fit counts only if it covers this much of the top-bar art: a small patch can match anywhere.</summary>
    public const double MinOverlap = 0.60;

    /// <summary>What the see-through parts of a card are laid over. The game shows a team colour there; grey is between them.</summary>
    public static readonly (byte R, byte G, byte B) Background = (110, 110, 110);

    private const int ArtWidth = 120;
    private const int ArtHeight = 200;

    private static readonly string[] _imageSuffixes = [".png", ".webp", ".jpg", ".jpeg", ".bmp"];

    /// <summary>Where a hero's card for one portrait state is kept, under the top-bar folder but out of the template bank's sight.</summary>
    public static string CardFolder(string topbarDir, PortraitState state) =>
        Path.Combine(topbarDir, "_cards", state.ToString().ToLowerInvariant());

    /// <summary>The file a portrait cut from the card for <paramref name="state"/> is saved as, in the hero's folder.</summary>
    public static string OutputName(PortraitState state) => state switch
    {
        PortraitState.Normal => "card_normal.png",
        PortraitState.Critical => "state_critical.png",
        _ => "state_gloat.png",
    };

    /// <summary>
    /// A crop of a card: its left, top and width as fractions of the card's width, height and
    /// width. Its height follows from the top-bar art's shape, so it fits cards of any size.
    /// </summary>
    public readonly record struct Frame(double X, double Y, double W)
    {
        public double HeightIn(int cardWidth, int cardHeight) => W * cardWidth * Layout.ArtAspect / cardHeight;
    }

    /// <param name="Score">How well the art matches the card there: a correlation, 1 being identical.</param>
    /// <param name="Overlap">How much of the art's opaque area landed on the card's.</param>
    public sealed record Registration(Frame Frame, double Score, double Overlap)
    {
        public bool IsAccepted => Score >= AcceptScore && Overlap >= MinOverlap;
    }

    /// <param name="Derived">Heroes whose portraits were cut this time.</param>
    /// <param name="Fallbacks">Heroes whose top-bar art doesn't fit their card, cut at the median crop instead.</param>
    /// <param name="Failed">"haze: ...": heroes whose images couldn't be read.</param>
    public sealed record Outcome(IReadOnlyList<string> Derived, IReadOnlyList<string> Fallbacks, IReadOnlyList<string> Failed);

    /// <summary>
    /// Cut every hero's portraits whose images changed since last time (all of them when
    /// <paramref name="force"/>), and remove any whose card has gone.
    /// </summary>
    public static Outcome Run(string topbarDir, IEnumerable<string> heroes, bool force = false)
    {
        var previous = LoadState(topbarDir);
        var fresh = force || previous?.Version != Version || previous.Background != Background;
        var records = new SortedDictionary<string, HeroRecord>(StringComparer.Ordinal);
        var failed = new List<string>();
        var toCut = new List<string>();

        foreach (var hero in heroes.Order(StringComparer.Ordinal))
        {
            var inputs = InputsOf(topbarDir, hero);
            if (inputs.Normal is null)
            {
                RemoveOutputs(topbarDir, hero);
                continue;
            }
            var hashes = inputs.Hashes();
            if (!fresh && previous!.Heroes.TryGetValue(hero, out var known) && known.Inputs.SequenceEqual(hashes))
            {
                records[hero] = known;
                continue;
            }
            try
            {
                var registration = inputs.Vertical is { } vertical ? Register(LoadArt(vertical), LoadArt(inputs.Normal)) : null;
                records[hero] = new HeroRecord(registration, hashes, registration?.Frame, "");
                toCut.Add(hero);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failed.Add($"{hero}: {ex.Message}");
            }
        }

        // The median of the crops that fit stands in for the ones that don't; if it moved, they move with it.
        var median = MedianFrame(records.Values.Where(record => record.Registration?.IsAccepted == true).Select(record => record.Registration!.Frame));
        var fallbacks = new List<string>();
        foreach (var (hero, record) in records.ToList())
        {
            if (record.Registration?.IsAccepted == true)
            {
                records[hero] = record with { Frame = record.Registration.Frame, Source = "registered" };
                continue;
            }
            fallbacks.Add(hero);
            if (median is null)
            {
                records.Remove(hero);
                continue;
            }
            if (record.Frame != median.Value && !toCut.Contains(hero))
                toCut.Add(hero);
            records[hero] = record with { Frame = median, Source = "median" };
        }

        var derived = new List<string>();
        foreach (var hero in toCut.Order(StringComparer.Ordinal))
        {
            if (!records.TryGetValue(hero, out var record) || record.Frame is not { } frame)
                continue;
            try
            {
                WriteOutputs(topbarDir, hero, frame);
                derived.Add(hero);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failed.Add($"{hero}: {ex.Message}");
                records.Remove(hero);
            }
        }
        SaveState(topbarDir, records);
        return new Outcome(derived, fallbacks, failed);
    }

    /// <summary>
    /// Where <paramref name="vertical"/> sits in <paramref name="card"/>: a coarse pass over every
    /// crop, then the best few refined, compared on brightness wherever both images are opaque.
    /// Null when nothing overlaps enough to judge.
    /// </summary>
    public static Registration? Register((RgbImage Image, byte[] Alpha) vertical, (RgbImage Image, byte[] Alpha) card)
    {
        var plane = Plane.Of(card.Image, card.Alpha).Blur(2);
        var coarse = Cells.Of(vertical.Image, vertical.Alpha, 12, 20);
        var fine = Cells.Of(vertical.Image, vertical.Alpha, 30, 50);
        if (coarse.OpaqueCount == 0 || fine.OpaqueCount == 0)
            return null;

        var candidates = new List<Registration>();
        for (var w = 0.35; w <= 1.0 + 1e-9; w += 0.03)
        {
            var h = new Frame(0, 0, w).HeightIn(plane.Width, plane.Height);
            for (var x = 0.0; x <= 1.0 - w + 1e-9; x += 0.02)
            {
                for (var y = 0.0; y <= 1.0 - h + 1e-9; y += 0.02)
                {
                    if (Compare(coarse, plane, new Frame(x, y, w)) is { } found && found.Overlap >= MinOverlap)
                        candidates.Add(found);
                }
            }
        }

        Registration? best = null;
        foreach (var start in candidates.OrderByDescending(candidate => candidate.Score).Take(6))
        {
            var refined = Refine(fine, plane, start.Frame);
            if (refined is not null && (best is null || refined.Score > best.Score))
                best = refined;
        }
        return best;
    }

    /// <summary>Hill-climb a crop's position and width in shrinking steps.</summary>
    private static Registration? Refine(Cells cells, Plane plane, Frame start)
    {
        var best = Compare(cells, plane, start);
        if (best is null)
            return null;
        foreach (var step in new[] { 0.01, 0.005, 0.0025, 0.00125 })
        {
            for (var round = 0; round < 20; round++)
            {
                var improved = false;
                foreach (var (dx, dy, dw) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    var frame = best.Frame with { X = best.Frame.X + dx * step, Y = best.Frame.Y + dy * step, W = best.Frame.W + dw * step };
                    if (Compare(cells, plane, frame) is { } trial && trial.Overlap >= MinOverlap && trial.Score > best.Score)
                    {
                        best = trial;
                        improved = true;
                    }
                }
                if (!improved)
                    break;
            }
        }
        return best;
    }

    /// <summary>The art's opaque cells against the card's opaque pixels at the same place, as a correlation.</summary>
    private static Registration? Compare(Cells cells, Plane plane, Frame frame)
    {
        if (frame.W <= 0)
            return null;
        var width = frame.W * plane.Width;
        var height = width * Layout.ArtAspect;
        var left = frame.X * plane.Width;
        var top = frame.Y * plane.Height;
        double n = 0, sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        for (var row = 0; row < cells.Rows; row++)
        {
            var y = top + (row + 0.5) / cells.Rows * height;
            if (y < 0 || y > plane.Height - 1)
                continue;
            for (var column = 0; column < cells.Columns; column++)
            {
                var index = row * cells.Columns + column;
                if (!cells.Opaque[index])
                    continue;
                var x = left + (column + 0.5) / cells.Columns * width;
                if (x < 0 || x > plane.Width - 1)
                    continue;
                var (luma, opacity) = plane.Sample(x, y);
                if (opacity < 0.98f)
                    continue;
                double a = cells.Luma[index], b = luma;
                n++;
                sumA += a;
                sumB += b;
                sumAA += a * a;
                sumBB += b * b;
                sumAB += a * b;
            }
        }
        var overlap = n / cells.OpaqueCount;
        if (n < 8)
            return new Registration(frame, -1, overlap);
        var covariance = sumAB - sumA * sumB / n;
        var varianceA = sumAA - sumA * sumA / n;
        var varianceB = sumBB - sumB * sumB / n;
        var score = varianceA <= 1e-9 || varianceB <= 1e-9 ? -1 : covariance / Math.Sqrt(varianceA * varianceB);
        return new Registration(frame, score, overlap);
    }

    /// <summary>A card cut at a frame, over <see cref="Background"/>, at the top-bar art's size.</summary>
    public static RgbImage Cut((RgbImage Image, byte[] Alpha) card, Frame frame)
    {
        var laid = ImageOps.Composite(card.Image, card.Alpha, Background);
        var width = frame.W * laid.Width;
        var height = width * Layout.ArtAspect;
        var left = Math.Clamp((int)Math.Round(frame.X * laid.Width), 0, laid.Width - 1);
        var top = Math.Clamp((int)Math.Round(frame.Y * laid.Height), 0, laid.Height - 1);
        var right = Math.Clamp((int)Math.Round(frame.X * laid.Width + width), left + 1, laid.Width);
        var bottom = Math.Clamp((int)Math.Round(frame.Y * laid.Height + height), top + 1, laid.Height);
        return ImageOps.ResizeRgb(laid.Crop(left, top, right, bottom), ArtWidth, ArtHeight);
    }

    private static Frame? MedianFrame(IEnumerable<Frame> frames)
    {
        var list = frames.ToList();
        if (list.Count == 0)
            return null;
        static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToList();
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }
        // Rounded as it's saved, so a median worked out afresh matches the one on disk.
        return new Frame(Math.Round(Median(list.Select(frame => frame.X)), 5), Math.Round(Median(list.Select(frame => frame.Y)), 5),
            Math.Round(Median(list.Select(frame => frame.W)), 5));
    }

    private static void WriteOutputs(string topbarDir, string hero, Frame frame)
    {
        var folder = Path.Combine(topbarDir, hero);
        foreach (var state in Enum.GetValues<PortraitState>())
        {
            var output = Path.Combine(folder, OutputName(state));
            if (FindImage(CardFolder(topbarDir, state), hero) is not { } card)
            {
                if (File.Exists(output))
                    File.Delete(output);
                continue;
            }
            Directory.CreateDirectory(folder);
            Png.Save(Cut(LoadArt(card), frame), output);
        }
        TemplateBank.InvalidatePythonCache(topbarDir);
    }

    private static void RemoveOutputs(string topbarDir, string hero)
    {
        foreach (var state in Enum.GetValues<PortraitState>())
        {
            var output = Path.Combine(topbarDir, hero, OutputName(state));
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static (RgbImage Image, byte[] Alpha) LoadArt(string path) => ImageFile.LoadWithAlpha(path);

    private static string? FindImage(string folder, string stem) =>
        _imageSuffixes.Select(suffix => Path.Combine(folder, stem + suffix)).FirstOrDefault(File.Exists);

    private sealed record Inputs(string? Vertical, string? Normal, string? Critical, string? Gloat)
    {
        public string?[] Hashes() => [Hash(Vertical), Hash(Normal), Hash(Critical), Hash(Gloat)];

        private static string? Hash(string? path) => path is null ? null : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static Inputs InputsOf(string topbarDir, string hero) => new(
        FindImage(topbarDir, hero),
        FindImage(CardFolder(topbarDir, PortraitState.Normal), hero),
        FindImage(CardFolder(topbarDir, PortraitState.Critical), hero),
        FindImage(CardFolder(topbarDir, PortraitState.Gloat), hero));

    // -- what was derived from what ------------------------------------------------------------

    /// <param name="Inputs">Hashes of the top-bar art and the three cards, null where there's none.</param>
    /// <param name="Source">"registered" or "median": where the frame came from.</param>
    private sealed record HeroRecord(Registration? Registration, string?[] Inputs, Frame? Frame, string Source);

    private sealed record State(int Version, (byte R, byte G, byte B) Background, Dictionary<string, HeroRecord> Heroes);

    private static State? LoadState(string topbarDir)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(topbarDir, StateFile))) is not JsonObject json)
                return null;
            var background = json["background"]!.AsArray().Select(value => (byte)(int)value!).ToArray();
            var heroes = new Dictionary<string, HeroRecord>(StringComparer.Ordinal);
            foreach (var (hero, node) in json["heroes"]!.AsObject())
            {
                var frame = FrameOf(node!["frame"]);
                var registration = node["registration"] is JsonObject found && FrameOf(found["frame"]) is { } fit
                    ? new Registration(fit, (double)found["score"]!, (double)found["overlap"]!)
                    : null;
                var inputs = node["inputs"]!.AsArray().Select(value => (string?)value).ToArray();
                heroes[hero] = new HeroRecord(registration, inputs, frame, (string?)node["source"] ?? "");
            }
            return new State((int)json["version"]!, (background[0], background[1], background[2]), heroes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                       or NullReferenceException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static void SaveState(string topbarDir, IReadOnlyDictionary<string, HeroRecord> heroes)
    {
        var json = new JsonObject
        {
            ["version"] = Version,
            ["background"] = new JsonArray(Background.R, Background.G, Background.B),
            ["heroes"] = new JsonObject(heroes.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)new JsonObject
            {
                ["frame"] = FrameJson(pair.Value.Frame),
                ["source"] = pair.Value.Source,
                ["registration"] = pair.Value.Registration is { } registration
                    ? new JsonObject
                    {
                        ["frame"] = FrameJson(registration.Frame),
                        ["score"] = Math.Round(registration.Score, 4),
                        ["overlap"] = Math.Round(registration.Overlap, 4),
                    }
                    : null,
                ["inputs"] = new JsonArray(pair.Value.Inputs.Select(hash => (JsonNode?)hash).ToArray()),
            }))),
        };
        Directory.CreateDirectory(topbarDir);
        File.WriteAllText(Path.Combine(topbarDir, StateFile), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static JsonArray? FrameJson(Frame? frame) =>
        frame is { } value ? new JsonArray(Math.Round(value.X, 5), Math.Round(value.Y, 5), Math.Round(value.W, 5)) : null;

    private static Frame? FrameOf(JsonNode? node) =>
        node is JsonArray values && values.Count == 3 ? new Frame((double)values[0]!, (double)values[1]!, (double)values[2]!) : null;

    // -- the pixels ----------------------------------------------------------------------------

    /// <summary>An image's brightness and opacity, 0-1, as planes to sample between pixels.</summary>
    private sealed class Plane(int width, int height, float[] luma, float[] opacity)
    {
        public int Width => width;
        public int Height => height;

        public static Plane Of(RgbImage image, byte[] alpha)
        {
            var count = image.Width * image.Height;
            var luma = new float[count];
            var opacity = new float[count];
            for (var i = 0; i < count; i++)
            {
                luma[i] = (0.299f * image.Pixels[i * 3] + 0.587f * image.Pixels[i * 3 + 1] + 0.114f * image.Pixels[i * 3 + 2]) / 255f;
                opacity[i] = alpha[i] / 255f;
            }
            return new Plane(image.Width, image.Height, luma, opacity);
        }

        /// <summary>A box blur, so sampling a big card at a small art's spacing averages rather than aliases.</summary>
        public Plane Blur(int radius) => new(width, height, BoxBlur(luma, radius), BoxBlur(opacity, radius));

        private float[] BoxBlur(float[] values, int radius)
        {
            var across = new float[values.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    float total = 0;
                    for (var dx = -radius; dx <= radius; dx++)
                        total += values[y * width + Math.Clamp(x + dx, 0, width - 1)];
                    across[y * width + x] = total / (2 * radius + 1);
                }
            }
            var result = new float[values.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    float total = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                        total += across[Math.Clamp(y + dy, 0, height - 1) * width + x];
                    result[y * width + x] = total / (2 * radius + 1);
                }
            }
            return result;
        }

        public (float Luma, float Opacity) Sample(double x, double y)
        {
            var x0 = (int)x;
            var y0 = (int)y;
            var x1 = Math.Min(x0 + 1, width - 1);
            var y1 = Math.Min(y0 + 1, height - 1);
            var fx = (float)(x - x0);
            var fy = (float)(y - y0);
            float Mix(float[] plane) =>
                (plane[y0 * width + x0] * (1 - fx) + plane[y0 * width + x1] * fx) * (1 - fy)
                + (plane[y1 * width + x0] * (1 - fx) + plane[y1 * width + x1] * fx) * fy;
            return (Mix(luma), Mix(opacity));
        }
    }

    /// <summary>Art averaged into a grid of cells, noting which cells are fully opaque.</summary>
    private sealed record Cells(int Columns, int Rows, float[] Luma, bool[] Opaque, int OpaqueCount)
    {
        public static Cells Of(RgbImage image, byte[] alpha, int columns, int rows)
        {
            var luma = new float[columns * rows];
            var opaque = new bool[columns * rows];
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    int x0 = column * image.Width / columns, x1 = (column + 1) * image.Width / columns;
                    int y0 = row * image.Height / rows, y1 = (row + 1) * image.Height / rows;
                    double total = 0;
                    var solid = true;
                    for (var y = y0; y < y1; y++)
                    {
                        for (var x = x0; x < x1; x++)
                        {
                            var i = y * image.Width + x;
                            total += 0.299 * image.Pixels[i * 3] + 0.587 * image.Pixels[i * 3 + 1] + 0.114 * image.Pixels[i * 3 + 2];
                            solid &= alpha[i] >= 250;
                        }
                    }
                    var count = Math.Max(1, (x1 - x0) * (y1 - y0));
                    luma[row * columns + column] = (float)(total / count / 255.0);
                    opaque[row * columns + column] = solid;
                }
            }
            return new Cells(columns, rows, luma, opaque, opaque.Count(value => value));
        }
    }
}
