namespace DeadlockAdvisor.Vision.Items;

/// <summary>
/// How a cell, or an icon as the game draws it, becomes a vector comparable by dot product: a square of the image,
/// area-averaged to <see cref="Size"/> pixels, with the tier corner (which the game draws over the icon's top right) and
/// the strip "ACTIVE" is printed on (only active items carry it) left out, each colour channel zero-mean and all of them
/// unit length together, so a dot product of two is a correlation.
/// </summary>
internal static class ItemFrame
{
    public const int Size = 32;

    private static readonly bool[] _keep = BuildMask();
    private static readonly int _kept = _keep.Count(k => k);

    /// <summary>A square of the image, <paramref name="side"/> pixels wide around (cx, cy), as size×size RGB, averaged as it shrinks.</summary>
    public static RgbImage Sample(RgbImage image, double cx, double cy, double side, int size)
    {
        var output = new RgbImage(size, size);
        var step = side / size;
        var taps = Math.Max(1, (int)Math.Ceiling(step));
        var n = taps * taps;
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

    private static bool[] BuildMask()
    {
        var keep = new bool[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                double xf = (x + 0.5) / Size, yf = (y + 0.5) / Size;
                keep[y * Size + x] = xf + (1 - yf) < 1.55 && !(yf > 0.70 && xf > 0.1 && xf < 0.9) && xf is > 0.06 and < 0.94 && yf is > 0.06 and < 0.94;
            }
        }
        return keep;
    }

    /// <summary>The vector of a <see cref="Size"/>×<see cref="Size"/> frame.</summary>
    public static float[] Normalise(RgbImage frame)
    {
        var vector = new float[_kept * 3];
        var at = 0;
        for (var i = 0; i < _keep.Length; i++)
        {
            if (!_keep[i])
                continue;
            for (var c = 0; c < 3; c++)
                vector[c * _kept + at] = frame.Pixels[i * 3 + c];
            at++;
        }
        double squares = 0;
        for (var c = 0; c < 3; c++)
        {
            double total = 0;
            for (var i = 0; i < _kept; i++)
                total += vector[c * _kept + i];
            var mean = (float)(total / _kept);
            for (var i = 0; i < _kept; i++)
            {
                vector[c * _kept + i] -= mean;
                squares += vector[c * _kept + i] * (double)vector[c * _kept + i];
            }
        }
        var scale = (float)(1.0 / (Math.Sqrt(squares) + 1e-6));
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= scale;
        return vector;
    }

    /// <summary>
    /// How much the kept part of a frame varies, as the standard deviation of its luminance, 0 to 255: an icon has a good deal,
    /// the panel behind an empty cell (even with a glare through it) very little.
    /// </summary>
    public static double Contrast(RgbImage frame)
    {
        double total = 0, squares = 0;
        for (var i = 0; i < _keep.Length; i++)
        {
            if (!_keep[i])
                continue;
            var luminance = 0.299 * frame.Pixels[i * 3] + 0.587 * frame.Pixels[i * 3 + 1] + 0.114 * frame.Pixels[i * 3 + 2];
            total += luminance;
            squares += luminance * luminance;
        }
        var mean = total / _kept;
        return Math.Sqrt(Math.Max(0, squares / _kept - mean * mean));
    }

    /// <summary>The cell of a capture as a vector, sampled where the game draws it.</summary>
    public static float[] Cell(RgbImage image, double cx, double cy, double side) => Normalise(Sample(image, cx, cy, side, Size));
}
