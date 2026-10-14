namespace DeadlockAdvisor.Vision.Items;

/// <summary>
/// Where the tier tags are. Each filled cell carries a small solid tag in its top right corner in its shop category's colour
/// (orange weapon, green vitality, purple spirit), far more saturated and bright than the translucent panel, the team's
/// brown or blue, behind it. The tags give the cells' places, which are filled, and their category without looking at an icon.
/// </summary>
internal sealed class TagMap
{
    private readonly int _left;
    private readonly int _top;
    private readonly int _width;
    private readonly int _height;
    private readonly sbyte[] _category;
    private readonly int[] _integral;
    private readonly int[] _darkIntegral;

    /// <summary>A pixel this dark (no channel over this) is the numeral's stroke and counts neither for the tag nor against it.</summary>
    private const int _dark = 50;

    private const double _flashSaturation = 0.07;
    private const double _flashBrightness = 0.95;

    /// <summary>The image the map was made from.</summary>
    public RgbImage Source { get; }

    private TagMap(RgbImage source, int left, int top, int width, int height, sbyte[] category, bool[] dark)
    {
        Source = source;
        (_left, _top, _width, _height) = (left, top, width, height);
        _category = category;
        _integral = Integral(width, height, i => category[i] >= 0);
        _darkIntegral = Integral(width, height, i => dark[i]);
    }

    private static int[] Integral(int width, int height, Func<int, bool> counts)
    {
        var integral = new int[(width + 1) * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = 0;
            for (var x = 0; x < width; x++)
            {
                row += counts(y * width + x) ? 1 : 0;
                integral[(y + 1) * (width + 1) + x + 1] = integral[y * (width + 1) + x + 1] + row;
            }
        }
        return integral;
    }

    public static TagMap Build(RgbImage image, int left, int top, int right, int bottom)
    {
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(image.Width, right);
        bottom = Math.Min(image.Height, bottom);
        var (width, height) = (Math.Max(0, right - left), Math.Max(0, bottom - top));
        var category = new sbyte[width * height];
        var dark = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((top + y) * image.Width + left + x) * 3;
                category[y * width + x] = Classify(image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
                dark[y * width + x] = Math.Max(image.Pixels[at], Math.Max(image.Pixels[at + 1], image.Pixels[at + 2])) < _dark;
            }
        }
        return new TagMap(image, left, top, width, height, category, dark);
    }

    /// <summary>
    /// The category a pixel's colour is a tag of, or -1. A tag is a flat colour, the same on every board measured: weapon hue 31
    /// to 38 at saturation 0.85 and up, vitality hue 76 to 88 at 0.8 and up (and dark, 0.33 to 0.42 bright), spirit hue 262 on a
    /// blue panel and 303 on a brown one at 0.37 to 0.51. The panel behind is hue 20 to 30 at 0.42 and under 0.42 bright.
    /// </summary>
    private static sbyte Classify(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max < 0.25 * 255)
            return -1;
        double delta = max - min;
        var saturation = delta / max;
        if (delta == 0)
            return -1;
        var hue = max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0)
            hue += 360;
        var brightness = max / 255.0;
        // A freshly bought item's tag flashes: spirit's purple is blown out to a pale lilac (255, 231, 255), which only its hue is left of.
        if (saturation is >= _flashSaturation and < 0.30 && brightness >= _flashBrightness && hue is >= 280 and < 330)
            return (sbyte)ShopCategory.Spirit;
        if (saturation < 0.30)
            return -1;
        return hue switch
        {
            >= 22 and < 48 when saturation >= 0.65 && brightness >= 0.30 => (sbyte)ShopCategory.Weapon,
            >= 60 and < 110 when saturation >= 0.60 => (sbyte)ShopCategory.Vitality,
            >= 245 and < 320 when brightness >= 0.35 => (sbyte)ShopCategory.Spirit,
            _ => -1,
        };
    }

    /// <summary>
    /// The share of the square around (cx, cy), <paramref name="half"/> pixels either side, that is tag-coloured, not counting the
    /// very dark pixels of a numeral; 0 outside the map.
    /// </summary>
    public double Density(double cx, double cy, double half)
    {
        int x0 = (int)Math.Round(cx - half) - _left, x1 = (int)Math.Round(cx + half) - _left;
        int y0 = (int)Math.Round(cy - half) - _top, y1 = (int)Math.Round(cy + half) - _top;
        var area = Math.Max(1, (x1 - x0) * (y1 - y0));
        (x0, x1, y0, y1) = (Math.Clamp(x0, 0, _width), Math.Clamp(x1, 0, _width), Math.Clamp(y0, 0, _height), Math.Clamp(y1, 0, _height));
        if (x1 <= x0 || y1 <= y0)
            return 0;
        var count = Sum(_integral, x0, y0, x1, y1);
        return (double)count / Math.Max(1, area - Sum(_darkIntegral, x0, y0, x1, y1));
    }

    private int Sum(int[] integral, int x0, int y0, int x1, int y1) =>
        integral[y1 * (_width + 1) + x1] - integral[y0 * (_width + 1) + x1] - integral[y1 * (_width + 1) + x0] + integral[y0 * (_width + 1) + x0];

    /// <summary>The category most of the tag-coloured pixels in the square are, and their share of the square; null with none.</summary>
    public (ShopCategory? Category, double Share) Dominant(double cx, double cy, double half)
    {
        int x0 = Math.Max(0, (int)Math.Round(cx - half) - _left), x1 = Math.Min(_width, (int)Math.Round(cx + half) - _left);
        int y0 = Math.Max(0, (int)Math.Round(cy - half) - _top), y1 = Math.Min(_height, (int)Math.Round(cy + half) - _top);
        Span<int> counts = stackalloc int[3];
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (_category[y * _width + x] is >= 0 and var kind)
                    counts[kind]++;
            }
        }
        var area = Math.Max(1, (int)Math.Round(2 * half) * (int)Math.Round(2 * half));
        var best = counts[0] >= counts[1] && counts[0] >= counts[2] ? 0 : counts[1] >= counts[2] ? 1 : 2;
        return counts[best] == 0 ? (null, 0) : ((ShopCategory)best, (double)counts[best] / area);
    }
}
