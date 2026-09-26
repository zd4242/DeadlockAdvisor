using System.Globalization;
using System.Text.RegularExpressions;

namespace DeadlockAdvisor.Vision;

/// <summary>
/// Reads the net worth pills under the portraits and the team totals beside the clock. A pure
/// function of the image and the grid <see cref="Detector"/> found, like detection itself.
/// <para>
/// Every length is in pitches, so it holds at any resolution or HUD scale. The pills' text sits on
/// one row across all twelve slots: its height is found once from all of them together, then each
/// pill is bounded by its own edges. Amber and teal pills have dark text and sapphire ones light, so
/// the text is whichever side of an Otsu split is in the minority. Digits are the bold full-height
/// glyphs; the faint "k" after them is left out by ink strength, and a dot by its size and height.
/// </para>
/// </summary>
public static partial class NetWorthReader
{
    public const int FrameWidth = 10;
    public const int FrameHeight = 14;

    /// <summary>A pill's digit has to match a reference at least this well, or the pill is unread.</summary>
    public const double MinDigitScore = 0.85;

    /// <summary>...and beat every other digit by this much: closer than that is a guess (a soft 5 against a 6).</summary>
    public const double MinDigitMargin = 0.03;

    /// <summary>Below this a mark beside a total is taken for part of the crest rather than a digit.</summary>
    public const double MinTotalDigitScore = 0.8;

    // Where things are, in pitches (measured on the 2560x1440 fixtures, held on the rescaled ones).
    private const double _textLine = 1.215;         // the pills' text, from the top of the screen
    private const double _textLineSearch = 0.2;
    private const double _pillHalfWidth = 0.165;
    private const double _pillEdgeSearch = 0.3;
    private const double _pillMinWidth = 0.28;
    private const double _pillMaxWidth = 0.44;
    private const double _pillInkHalfHeight = 0.07;
    private const double _totalsAboveLine = 0.525;
    private const double _totalsHalfHeight = 0.15;
    private const double _totalsInner = 0.05;
    private const double _totalsOuter = 1.25;

    // Ink is 0 (background) to 1 (text), scaled per patch.
    private const float _strongInk = 0.85f;
    private const float _inkRow = 0.5f;
    private const float _bridgeInk = 0.6f;

    private const int _supersample = 3;

    public static NetWorthReading Read(RgbImage band, Geometry geometry, NetWorthGlyphs glyphs)
    {
        if (TextLine(band, geometry) is not { } line)
            return NetWorthReading.Empty;
        int?[] totals = [ReadTotal(band, geometry, line, 0, glyphs), ReadTotal(band, geometry, line, 1, glyphs)];
        var pills = geometry.Centers()
            .Select((center, slot) => ReadPill(band, center, line, geometry.Pitch, glyphs) is { } printed
                ? PillSouls(printed, totals[slot / Layout.PerTeam])
                : null)
            .ToList();
        return new NetWorthReading(pills, totals);
    }

    /// <summary>
    /// Every pill's and total's digits as raw frames, left to right: the twelve pills, then the left
    /// and right totals. What reference glyphs are cut from.
    /// </summary>
    internal static List<List<float[]>> DigitFrames(RgbImage band, Geometry geometry)
    {
        var result = new List<List<float[]>>();
        var line = TextLine(band, geometry);
        foreach (var center in geometry.Centers())
            result.Add(line is { } y && PillText(band, center, y, geometry.Pitch) is { } text ? text.DigitFrames() : []);
        for (var side = 0; side < 2; side++)
            result.Add(line is { } y && TotalText(band, geometry, y, side) is { } text ? text.DigitFrames() : []);
        return result;
    }

    // -- pills ------------------------------------------------------------------------

    /// <summary>A pill's value as printed ("600", "1.2", "16"), or null where it couldn't be read.</summary>
    private static string? ReadPill(RgbImage band, double center, double line, double pitch, NetWorthGlyphs glyphs)
    {
        if (PillText(band, center, line, pitch) is not { } text)
            return null;
        var characters = new List<char>();
        foreach (var token in text.Tokens)
        {
            if (token.IsDot)
            {
                characters.Add('.');
                continue;
            }
            var (digit, score, margin) = glyphs.Classify(text.Frame(token));
            if (score < MinDigitScore || margin < MinDigitMargin)
                return null;
            characters.Add(digit);
        }
        return new string(characters.ToArray());
    }

    /// <summary>
    /// The row every pill's text sits on: the middle of the digit-sized marks found around each slot,
    /// medianed over all twelve so a portrait or two poking into the window doesn't move it.
    /// </summary>
    private static double? TextLine(RgbImage band, Geometry geometry)
    {
        var pitch = geometry.Pitch;
        var middles = new List<double>();
        var top = Math.Max(0, (int)Math.Round((_textLine - _textLineSearch) * pitch));
        var bottom = Math.Min(band.Height, (int)Math.Round((_textLine + _textLineSearch) * pitch));
        foreach (var center in geometry.Centers())
        {
            var left = Math.Max(0, (int)Math.Round(center - _pillHalfWidth * pitch));
            var right = Math.Min(band.Width, (int)Math.Round(center + _pillHalfWidth * pitch));
            if (right - left < 4 || bottom - top < 4)
                continue;
            var luminance = Luminance(band, left, top, right, bottom);
            var threshold = Otsu(luminance.Values);
            foreach (var dark in new[] { true, false })
            {
                var mask = luminance.Values.Select(value => value <= threshold == dark).ToArray();
                foreach (var (x0, y0, x1, y1) in Components(mask, luminance.Width, luminance.Height))
                {
                    var height = y1 - y0;
                    if (height >= 0.07 * pitch && height <= 0.15 * pitch && x1 - x0 <= 0.12 * pitch && y0 > 0 && y1 < luminance.Height)
                        middles.Add(top + (y0 + y1) / 2.0);
                }
            }
        }
        return middles.Count == 0 ? null : Median(middles);
    }

    private static Text? PillText(RgbImage band, double center, double line, double pitch)
    {
        if (PillEdges(band, center, line, pitch) is not var (left, right))
            return null;
        var top = (int)Math.Round(line - _pillInkHalfHeight * pitch);
        var bottom = (int)Math.Round(line + _pillInkHalfHeight * pitch);
        if (top < 0 || bottom > band.Height || right - 1 - (left + 2) < 4)
            return null;
        return Ink(Luminance(band, left + 2, top, right - 1, bottom)) is { } ink ? PillTokens(ink) : null;
    }

    /// <summary>
    /// The pill's left and right edges: the strongest pair of colour steps around the slot centre, a
    /// pill's width apart, on the rows just above and below the text (plain pill there, so the only
    /// steps are its edges and whatever lies outside it).
    /// </summary>
    private static (int Left, int Right)? PillEdges(RgbImage band, double center, double line, double pitch)
    {
        var rows = new List<int>();
        for (var y = (int)Math.Round(line - 0.14 * pitch); y < (int)Math.Round(line - 0.075 * pitch); y++)
            rows.Add(y);
        for (var y = (int)Math.Round(line + 0.075 * pitch); y < (int)Math.Round(line + 0.14 * pitch); y++)
            rows.Add(y);
        rows.RemoveAll(y => y < 0 || y >= band.Height);
        var x0 = Math.Max(0, (int)Math.Round(center - _pillEdgeSearch * pitch));
        var x1 = Math.Min(band.Width - 1, (int)Math.Round(center + _pillEdgeSearch * pitch));
        if (rows.Count == 0 || x1 - x0 < 4)
            return null;

        var steps = new double[x1 - x0];
        foreach (var y in rows)
        {
            for (var i = 0; i < steps.Length; i++)
            {
                var (r0, g0, b0) = band[x0 + i, y];
                var (r1, g1, b1) = band[x0 + i + 1, y];
                steps[i] += Math.Abs(r1 - r0) + Math.Abs(g1 - g0) + Math.Abs(b1 - b0);
            }
        }

        (double Strength, int Left, int Right)? best = null;
        for (var left = 0; left < steps.Length; left++)
        {
            if (x0 + left >= center)
                break;
            for (var right = left + 1; right < steps.Length; right++)
            {
                var width = right - left;
                if (width < _pillMinWidth * pitch || x0 + right <= center)
                    continue;
                if (width > _pillMaxWidth * pitch)
                    break;
                var strength = steps[left] + steps[right];
                if (best is null || strength > best.Value.Strength)
                    best = (strength, left, right);
            }
        }
        return best is { } found ? (x0 + found.Left + 1, x0 + found.Right + 1) : null;
    }

    /// <summary>
    /// Glyphs are runs of columns with bold ink (the "k" never gets there). Runs a narrow, still-inked
    /// gap apart are one digit whose thin stroke dipped; a short mark low down is the decimal point.
    /// </summary>
    private static Text PillTokens(Patch ink)
    {
        var (runs, columnPeak) = StrongRuns(ink);
        if (runs.Count == 0)
            return new Text(ink, [], 0, 0);
        var extents = runs.Select(run => InkRows(ink, Math.Max(0, run.Start - 1), Math.Min(ink.Width, run.End + 1))).ToList();
        var top = extents.Min(extent => extent.Top);
        var bottom = extents.Max(extent => extent.Bottom);
        var height = bottom - top;
        bool DotLike(int i) => extents[i].Top >= top + 0.55 * height && runs[i].End - runs[i].Start <= 0.5 * height;

        var merged = new List<(int Start, int End, int Top, int Bottom, bool IsDot)>();
        for (var i = 0; i < runs.Count; i++)
        {
            var dot = DotLike(i);
            if (!dot && merged.Count > 0 && !merged[^1].IsDot)
            {
                var previous = merged[^1];
                var gap = runs[i].Start - previous.End;
                if (gap <= 2 && Enumerable.Range(previous.End, gap).All(x => columnPeak[x] >= _bridgeInk))
                {
                    merged[^1] = (previous.Start, runs[i].End, Math.Min(previous.Top, extents[i].Top), Math.Max(previous.Bottom, extents[i].Bottom), false);
                    continue;
                }
            }
            merged.Add((runs[i].Start, runs[i].End, extents[i].Top, extents[i].Bottom, dot));
        }

        var tokens = new List<Token>();
        foreach (var (start, end, glyphTop, glyphBottom, dot) in merged)
        {
            var x0 = Math.Max(0, start - 1);
            var x1 = Math.Min(ink.Width, end + 1);
            if (dot)
                tokens.Add(new Token(x0, x1, true));
            else if (glyphBottom - glyphTop >= 0.6 * height)
                tokens.AddRange(Split(ink, x0, x1, height));
        }
        return new Text(ink, tokens, top, bottom);
    }

    // -- totals ------------------------------------------------------------------------

    private static int? ReadTotal(RgbImage band, Geometry geometry, double line, int side, NetWorthGlyphs glyphs)
    {
        if (TotalText(band, geometry, line, side) is not { } text)
            return null;
        var digits = text.Tokens
            .Select(token => glyphs.Classify(text.Frame(token)))
            .Where(match => match.Score >= MinTotalDigitScore)
            .Select(match => match.Digit)
            .ToArray();
        return TotalSouls(new string(digits));
    }

    /// <summary>
    /// A team's total, from a window beside the clock wide enough for any layout: the digits are the
    /// marks of a digit's height, which leaves out the taller crest beside them.
    /// </summary>
    private static Text? TotalText(RgbImage band, Geometry geometry, double line, int side)
    {
        var pitch = geometry.Pitch;
        var middle = line - _totalsAboveLine * pitch;
        var top = Math.Max(0, (int)Math.Round(middle - _totalsHalfHeight * pitch));
        var bottom = Math.Min(band.Height, (int)Math.Round(middle + _totalsHalfHeight * pitch));
        var (inner, outer) = (_totalsInner * pitch, _totalsOuter * pitch);
        var left = Math.Max(0, (int)Math.Round(side == 0 ? geometry.CenterX - outer : geometry.CenterX + inner));
        var right = Math.Min(band.Width, (int)Math.Round(side == 0 ? geometry.CenterX - inner : geometry.CenterX + outer));
        if (right - left < 4 || bottom - top < 4 || Ink(Luminance(band, left, top, right, bottom)) is not { } ink)
            return null;

        var (runs, _) = StrongRuns(ink);
        var marks = runs
            .Select(run => (run.Start, run.End, Rows: InkRows(ink, Math.Max(0, run.Start - 1), Math.Min(ink.Width, run.End + 1))))
            .Where(mark => mark.Rows.Bottom - mark.Rows.Top >= 0.12 * pitch && mark.Rows.Bottom - mark.Rows.Top <= 0.2 * pitch)
            .ToList();
        if (marks.Count == 0)
            return null;
        var textTop = (int)Median(marks.Select(mark => (double)mark.Rows.Top).ToList());
        var textBottom = (int)Median(marks.Select(mark => (double)mark.Rows.Bottom).ToList());
        var height = textBottom - textTop;

        var tokens = new List<Token>();
        foreach (var (start, end, rows) in marks)
        {
            if (Math.Abs(rows.Top - textTop) > 0.25 * height || Math.Abs(rows.Bottom - textBottom) > 0.25 * height)
                continue;
            tokens.AddRange(Split(ink, Math.Max(0, start - 1), Math.Min(ink.Width, end + 1), height));
        }
        return new Text(ink, tokens, textTop, textBottom);
    }

    // -- shared pieces ---------------------------------------------------------------

    /// <summary>
    /// A pill in souls, from how the game prints it: plain souls under a thousand ("600"), thousands
    /// to one decimal below ten ("1.2"), whole thousands from there ("16"). The "k" that marks
    /// thousands is too faint to read, so three bare digits are plain souls unless the team's total
    /// says it's past 100k.
    /// </summary>
    internal static int? PillSouls(string printed, int? teamTotal)
    {
        if (!NumberPattern().IsMatch(printed))
            return null;
        var value = double.Parse(printed, CultureInfo.InvariantCulture);
        var thousands = printed.Contains('.') || printed.Length == 2 || (printed.Length >= 3 && teamTotal >= 100_000);
        return (int)Math.Round(thousands ? value * 1000 : value);
    }

    /// <summary>A team total in souls: always whole thousands ("3", "112").</summary>
    internal static int? TotalSouls(string printed) =>
        printed.Length is > 0 and <= 4 && printed.All(char.IsAsciiDigit) ? int.Parse(printed, CultureInfo.InvariantCulture) * 1000 : null;

    [GeneratedRegex(@"^\d+(\.\d)?$")]
    private static partial Regex NumberPattern();

    /// <summary>One glyph, or two or more digits touching, cut at the faintest columns.</summary>
    private static IEnumerable<Token> Split(Patch ink, int x0, int x1, int height)
    {
        var width = x1 - x0;
        var pieces = width > 0.95 * height ? Math.Max(1, (int)Math.Round(width / (0.62 * height))) : 1;
        if (pieces == 1)
        {
            yield return new Token(x0, x1, false);
            yield break;
        }
        var columns = Enumerable.Range(x0, width).Select(x => Enumerable.Range(0, ink.Height).Sum(y => ink[x, y])).ToArray();
        var cuts = new List<int> { x0 };
        for (var piece = 1; piece < pieces; piece++)
        {
            var nominal = width * piece / pieces;
            var low = Math.Max(1, nominal - (int)(0.15 * height));
            var high = Math.Min(width - 1, nominal + (int)(0.15 * height));
            var cut = low;
            for (var i = low; i <= high; i++)
            {
                if (columns[i] < columns[cut])
                    cut = i;
            }
            cuts.Add(x0 + cut);
        }
        cuts.Add(x1);
        for (var i = 0; i < pieces; i++)
            yield return new Token(cuts[i], cuts[i + 1], false);
    }

    private static (List<(int Start, int End)> Runs, float[] ColumnPeak) StrongRuns(Patch ink)
    {
        var peak = new float[ink.Width];
        for (var x = 0; x < ink.Width; x++)
        {
            for (var y = 0; y < ink.Height; y++)
                peak[x] = Math.Max(peak[x], ink[x, y]);
        }
        var runs = new List<(int, int)>();
        for (var x = 0; x < ink.Width;)
        {
            if (peak[x] < _strongInk)
            {
                x++;
                continue;
            }
            var start = x;
            while (x < ink.Width && peak[x] >= _strongInk)
                x++;
            runs.Add((start, x));
        }
        return (runs, peak);
    }

    /// <summary>The rows a stretch of columns has ink on.</summary>
    private static (int Top, int Bottom) InkRows(Patch ink, int x0, int x1)
    {
        int? first = null;
        var last = 0;
        for (var y = 0; y < ink.Height; y++)
        {
            var inked = false;
            for (var x = x0; x < x1 && !inked; x++)
                inked = ink[x, y] >= _inkRow;
            if (!inked)
                continue;
            first ??= y;
            last = y;
        }
        return first is { } top ? (top, last + 1) : (0, 0);
    }

    /// <summary>
    /// Text as ink: 0 on the background, 1 on the text. The text is the minority side of an Otsu split,
    /// whichever way round its colours are; null for a patch with no contrast to speak of.
    /// </summary>
    private static Patch? Ink(Patch luminance)
    {
        var threshold = Otsu(luminance.Values);
        var darkShare = luminance.Values.Count(value => value <= threshold) / (double)luminance.Values.Length;
        var textDark = darkShare < 0.5;
        var low = Percentile(luminance.Values, 3);
        var high = Percentile(luminance.Values, 97);
        if (high - low < 10)
            return null;
        var values = luminance.Values
            .Select(value => (float)Math.Clamp(textDark ? (high - value) / (high - low) : (value - low) / (high - low), 0, 1))
            .ToArray();
        return new Patch(luminance.Width, luminance.Height, values);
    }

    private static Patch Luminance(RgbImage image, int left, int top, int right, int bottom)
    {
        var width = right - left;
        var height = bottom - top;
        var values = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = image[left + x, top + y];
                values[y * width + x] = 0.299f * r + 0.587f * g + 0.114f * b;
            }
        }
        return new Patch(width, height, values);
    }

    /// <summary>The threshold (on 0-255) that best splits the values in two; the low side is "≤ threshold".</summary>
    private static int Otsu(float[] values)
    {
        var counts = new double[256];
        var sums = new double[256];
        foreach (var value in values)
        {
            var bin = Math.Clamp((int)Math.Ceiling(value), 0, 255);
            counts[bin]++;
            sums[bin] += value;
        }
        double total = values.Length, totalSum = sums.Sum();
        double lowCount = 0, lowSum = 0, bestSpread = -1;
        var best = 0;
        for (var t = 0; t < 255; t++)
        {
            lowCount += counts[t];
            lowSum += sums[t];
            var highCount = total - lowCount;
            if (lowCount == 0 || highCount == 0)
                continue;
            var difference = lowSum / lowCount - (totalSum - lowSum) / highCount;
            var spread = lowCount * highCount * difference * difference;
            if (spread > bestSpread)
            {
                bestSpread = spread;
                best = t;
            }
        }
        return best;
    }

    /// <summary>numpy's default (linear) percentile.</summary>
    private static double Percentile(float[] values, double percent)
    {
        var sorted = values.Order().ToArray();
        var position = percent / 100.0 * (sorted.Length - 1);
        var below = (int)Math.Floor(position);
        var above = Math.Min(sorted.Length - 1, below + 1);
        return sorted[below] + (sorted[above] - sorted[below]) * (position - below);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var half = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[half] : (sorted[half - 1] + sorted[half]) / 2.0;
    }

    /// <summary>8-connected components of a mask, as bounding boxes (right and bottom exclusive).</summary>
    private static List<(int X0, int Y0, int X1, int Y1)> Components(bool[] mask, int width, int height)
    {
        var seen = new bool[mask.Length];
        var boxes = new List<(int, int, int, int)>();
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start])
                continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                var (x, y) = (at % width, at / width);
                (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x + 1), Math.Max(y1, y + 1));
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;
                        var next = ny * width + nx;
                        if (mask[next] && !seen[next])
                        {
                            seen[next] = true;
                            stack.Push(next);
                        }
                    }
                }
            }
            boxes.Add((x0, y0, x1, y1));
        }
        return boxes;
    }

    /// <summary>A grid of floats: luminance, or ink.</summary>
    private sealed record Patch(int Width, int Height, float[] Values)
    {
        public float this[int x, int y] => Values[y * Width + x];
    }

    private readonly record struct Token(int X0, int X1, bool IsDot);

    /// <summary>One line of text: its ink, its glyphs left to right, and the rows its digits span.</summary>
    private sealed record Text(Patch Ink, List<Token> Tokens, int Top, int Bottom)
    {
        public List<float[]> DigitFrames() => Tokens.Where(token => !token.IsDot).Select(Frame).ToList();

        /// <summary>
        /// A glyph framed for matching: the digit rows by a fixed width around its ink's centre of mass,
        /// supersampled and averaged down, so a frame doesn't depend on where the edges of a run fell.
        /// </summary>
        public float[] Frame(Token token)
        {
            var height = Bottom - Top;
            double weight = 0, moment = 0;
            for (var x = token.X0; x < token.X1; x++)
            {
                for (var y = Top; y < Bottom; y++)
                {
                    weight += Ink[x, y];
                    moment += x * (double)Ink[x, y];
                }
            }
            var center = weight > 0 ? moment / weight : (token.X0 + token.X1 - 1) / 2.0;
            var half = Math.Max(0.4 * height, (token.X1 - token.X0) / 2.0);

            var columns = FrameWidth * _supersample;
            var rows = FrameHeight * _supersample;
            var frame = new float[FrameWidth * FrameHeight];
            for (var row = 0; row < rows; row++)
            {
                var y = rows == 1 ? Top : Top + (Bottom - 1 - Top) * row / (double)(rows - 1);
                for (var column = 0; column < columns; column++)
                {
                    var x = center - half + 2 * half * column / (columns - 1);
                    var value = x >= token.X0 - 1 && x <= token.X1 ? Sample(x, y) : 0f;
                    frame[row / _supersample * FrameWidth + column / _supersample] += value / (_supersample * _supersample);
                }
            }
            return frame;
        }

        private float Sample(double x, double y)
        {
            x = Math.Clamp(x, 0, Ink.Width - 1.001);
            y = Math.Clamp(y, 0, Ink.Height - 1.001);
            var (xi, yi) = ((int)x, (int)y);
            var (fx, fy) = ((float)(x - xi), (float)(y - yi));
            return Ink[xi, yi] * (1 - fx) * (1 - fy) + Ink[xi + 1, yi] * fx * (1 - fy)
                   + Ink[xi, yi + 1] * (1 - fx) * fy + Ink[xi + 1, yi + 1] * fx * fy;
        }
    }
}
