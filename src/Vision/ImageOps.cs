namespace DeadlockAdvisor.Vision;

/// <summary>
/// The pixel maths behind hero matching.
/// <para>
/// The reference art has a flat, hero-specific backdrop baked in, but in game the character is
/// composited over a team-coloured ellipse instead, so comparing the two directly compares two
/// different backgrounds. Masking the backdrop out was a dead end (characters bleed to the edges).
/// What works is a high-pass: both backdrops are smooth, so subtracting a blurred copy of an image
/// from itself removes almost all of either while keeping the character's structure. Colour is
/// kept as two opponent channels, because screenshots are often soft and a hero's palette survives
/// downscaling better than their edges do.
/// </para>
/// </summary>
public static class ImageOps
{
    /// <summary>Descriptor grid, matching the 120×200 aspect of the reference art. Small on purpose: there's no fine detail to keep.</summary>
    public const int GridW = 24;
    public const int GridH = 40;

    /// <summary>Radius of the blur subtracted to kill the backdrop, in descriptor cells.</summary>
    public const int HighpassRadius = 4;

    /// <summary>Luminance, then the red-green and yellow-blue channels.</summary>
    public static readonly float[] ChannelWeights = [1.0f, 0.75f, 0.75f];

    public const int Dimensions = 3 * GridH * GridW;

    private static readonly float[] _taper = BuildTaper();

    /// <summary>Downscales of more than 2× average whole pixels; anything gentler interpolates.</summary>
    public static RgbImage ResizeRgb(RgbImage image, int width, int height) =>
        image.Resize(width, height, image.Width > width * 2 ? ResampleFilter.Box : ResampleFilter.Bilinear);

    /// <summary>
    /// An RGB crop → a unit-length vector comparable by dot product, or null for a patch with no
    /// structure left after the high-pass, which is what a dead player's flat silhouette gives.
    /// </summary>
    public static float[]? Descriptor(RgbImage image)
    {
        var small = ResizeRgb(image, GridW, GridH).Pixels;
        const int cells = GridW * GridH;
        var channels = new float[3 * cells];
        for (var i = 0; i < cells; i++)
        {
            float r = small[i * 3], g = small[i * 3 + 1], b = small[i * 3 + 2];
            channels[i] = 0.299f * r + 0.587f * g + 0.114f * b;
            channels[cells + i] = r - g;
            channels[2 * cells + i] = 0.5f * (r + g) - b;
        }

        var vector = new float[Dimensions];
        var blurred = new float[cells];
        double sum = 0;
        for (var channel = 0; channel < 3; channel++)
        {
            var plane = channels.AsSpan(channel * cells, cells);
            BoxBlur(plane, blurred);
            var weight = ChannelWeights[channel];
            for (var i = 0; i < cells; i++)
            {
                var value = (plane[i] - blurred[i]) * _taper[i] * weight;
                vector[channel * cells + i] = value;
                sum += value;
            }
        }

        var mean = (float)(sum / Dimensions);
        double squares = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] -= mean;
            squares += (double)vector[i] * vector[i];
        }
        var norm = Math.Sqrt(squares);
        if (norm < 1e-6)
            return null;
        var scale = (float)(1.0 / norm);
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= scale;
        return vector;
    }

    /// <summary>
    /// A separable moving average:
    /// a (2r+1)-wide window, edge-padded, over rows then columns.
    /// </summary>
    internal static void BoxBlur(ReadOnlySpan<float> plane, Span<float> output)
    {
        const int cells = GridW * GridH;
        var kDown = WindowFor(GridH);
        var kAcross = WindowFor(GridW);

        // Down each column first (axis 0)...
        Span<double> down = stackalloc double[cells];
        for (var x = 0; x < GridW; x++)
        {
            for (var y = 0; y < GridH; y++)
            {
                double total = 0;
                for (var offset = -(kDown / 2); offset <= kDown / 2; offset++)
                    total += plane[Math.Clamp(y + offset, 0, GridH - 1) * GridW + x];
                down[y * GridW + x] = total / kDown;
            }
        }

        // ...then along each row (axis 1).
        for (var y = 0; y < GridH; y++)
        {
            for (var x = 0; x < GridW; x++)
            {
                double total = 0;
                for (var offset = -(kAcross / 2); offset <= kAcross / 2; offset++)
                    total += down[y * GridW + Math.Clamp(x + offset, 0, GridW - 1)];
                output[y * GridW + x] = (float)(total / kAcross);
            }
        }
    }

    /// <summary>The window length along an axis of <paramref name="n"/> cells: 2r+1, or less for a short axis.</summary>
    private static int WindowFor(int n) => Math.Min(2 * HighpassRadius + 1, n % 2 == 1 ? n : n - 1);

    /// <summary>
    /// A mild raised-cosine window: the backdrop shows most around the rim, but characters reach the
    /// edges too, so only the outer fifth is tapered, and never below 0.35.
    /// </summary>
    private static float[] BuildTaper()
    {
        static float[] Ramp(int n)
        {
            const float edge = 0.2f;
            var w = new float[n];
            for (var i = 0; i < n; i++)
            {
                var x = (float)(n == 1 ? 0.0 : i == n - 1 ? 1.0 : i * (1.0 / (n - 1)));
                var weight = 1.0f;
                if (x < edge)
                    weight = 0.5f * (1.0f - MathF.Cos(MathF.PI * x / edge));
                else if (x > 1.0f - edge)
                    weight = 0.5f * (1.0f - MathF.Cos(MathF.PI * (1.0f - x) / edge));
                w[i] = 0.35f + 0.65f * weight;
            }
            return w;
        }

        var rows = Ramp(GridH);
        var columns = Ramp(GridW);
        var taper = new float[GridH * GridW];
        for (var y = 0; y < GridH; y++)
        {
            for (var x = 0; x < GridW; x++)
                taper[y * GridW + x] = rows[y] * columns[x];
        }
        return taper;
    }

    /// <summary>
    /// An image laid over a flat colour by its opacity. Art is kept this way rather than with its
    /// alpha dropped: the colour under a fully transparent pixel is whatever the exporter left there.
    /// </summary>
    public static RgbImage Composite(RgbImage image, byte[] alpha, (byte R, byte G, byte B) background)
    {
        var result = new RgbImage(image.Width, image.Height);
        byte[] under = [background.R, background.G, background.B];
        for (var i = 0; i < alpha.Length; i++)
        {
            var a = alpha[i];
            for (var c = 0; c < 3; c++)
                result.Pixels[i * 3 + c] = (byte)((image.Pixels[i * 3 + c] * a + under[c] * (255 - a) + 127) / 255);
        }
        return result;
    }

    /// <summary>
    /// How much a portrait's brightness varies over its upper two thirds (the art, above the pill), 0-1.
    /// A faded portrait is washed into the world behind it: 0.09 typically, against 0.23 for one drawn
    /// in full, and on the labelled captures no portrait drawn in full was under 0.14.
    /// </summary>
    public static double Contrast(RgbImage portrait)
    {
        double sum = 0, squares = 0;
        var count = 0;
        for (var y = 0; y < portrait.Height * 2 / 3; y++)
        {
            for (var x = 0; x < portrait.Width; x++)
            {
                var at = (y * portrait.Width + x) * 3;
                var value = Math.Max(portrait.Pixels[at], Math.Max(portrait.Pixels[at + 1], portrait.Pixels[at + 2])) / 255.0;
                sum += value;
                squares += value * value;
                count++;
            }
        }
        if (count == 0)
            return 0;
        var mean = sum / count;
        return Math.Sqrt(Math.Max(0, squares / count - mean * mean));
    }

    /// <summary>An RGB colour (0-255) as hue in degrees, and saturation and brightness from 0 to 1.</summary>
    public static (double Hue, double Saturation, double Value) Hsv(IReadOnlyList<float> rgb)
    {
        double r = rgb[0] / 255.0, g = rgb[1] / 255.0, b = rgb[2] / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        var hue = delta == 0 ? 0
            : max == r ? 60 * ((g - b) / delta % 6)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        return (hue < 0 ? hue + 360 : hue, max == 0 ? 0 : delta / max, max);
    }

    /// <summary>
    /// (mean RGB, mean per-channel spread) over a box, clipped to the image. Used on the backplate
    /// above each portrait: your own slot is filled with a flat team colour, the others show the map.
    /// </summary>
    public static (float[] Mean, float Spread) MeanColor(RgbImage image, int left, int top, int right, int bottom)
    {
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(image.Width, right);
        bottom = Math.Min(image.Height, bottom);
        if (right - left < 2 || bottom - top < 2)
            return (new float[3], 0f);

        var count = (right - left) * (bottom - top);
        var mean = new float[3];
        var spread = 0.0;
        for (var c = 0; c < 3; c++)
        {
            double total = 0;
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                    total += image.Pixels[(y * image.Width + x) * 3 + c];
            }
            var channelMean = total / count;
            double squares = 0;
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var d = image.Pixels[(y * image.Width + x) * 3 + c] - channelMean;
                    squares += d * d;
                }
            }
            mean[c] = (float)channelMean;
            spread += Math.Sqrt(squares / count);
        }
        return (mean, (float)(spread / 3));
    }
}
