namespace DeadlockAdvisor.Vision;

public enum ResampleFilter
{
    Box,
    Bilinear,
}

/// <summary>
/// An 8-bit RGB image, three bytes a pixel, row after row: what Pillow's "RGB" mode holds, and all
/// detection ever needs. Crops and resizes reproduce Pillow's byte for byte, so descriptors match
/// the Python app's.
/// </summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height, byte[]? pixels = null)
    {
        if (width < 0 || height < 0)
            throw new ArgumentOutOfRangeException(nameof(width), "An image can't have a negative size.");
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 3];
        if (Pixels.Length != width * height * 3)
            throw new ArgumentException("Pixel data doesn't match the size.", nameof(pixels));
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>R, G, B for each pixel, left to right, top to bottom.</summary>
    public byte[] Pixels { get; }

    public (byte R, byte G, byte B) this[int x, int y]
    {
        get
        {
            var at = (y * Width + x) * 3;
            return (Pixels[at], Pixels[at + 1], Pixels[at + 2]);
        }
    }

    /// <summary>Pillow's <c>crop((left, top, right, bottom))</c>, for a box inside the image.</summary>
    public RgbImage Crop(int left, int top, int right, int bottom)
    {
        if (left < 0 || top < 0 || right > Width || bottom > Height || right < left || bottom < top)
            throw new ArgumentOutOfRangeException(nameof(left), "The crop box must lie inside the image.");
        var width = right - left;
        var cropped = new RgbImage(width, bottom - top);
        for (var y = top; y < bottom; y++)
            Array.Copy(Pixels, (y * Width + left) * 3, cropped.Pixels, (y - top) * width * 3, width * 3);
        return cropped;
    }

    /// <summary>A copy with <paramref name="image"/> pasted at (x, y), clipped to this image.</summary>
    public RgbImage Paste(RgbImage image, int x, int y)
    {
        var result = new RgbImage(Width, Height, (byte[])Pixels.Clone());
        for (var row = 0; row < image.Height; row++)
        {
            var targetY = y + row;
            if (targetY < 0 || targetY >= Height)
                continue;
            for (var column = 0; column < image.Width; column++)
            {
                var targetX = x + column;
                if (targetX < 0 || targetX >= Width)
                    continue;
                Array.Copy(image.Pixels, (row * image.Width + column) * 3, result.Pixels, (targetY * Width + targetX) * 3, 3);
            }
        }
        return result;
    }

    /// <summary>
    /// Pillow's <c>resize((width, height), filter)</c>: a horizontal then a vertical pass, each with
    /// its coefficients turned into 22-bit fixed point and every intermediate rounded back to a byte.
    /// </summary>
    public RgbImage Resize(int width, int height, ResampleFilter filter)
    {
        if (width == Width && height == Height)
            return new RgbImage(width, height, (byte[])Pixels.Clone());

        var (horizontalSize, horizontalBounds, horizontalKernel) = PillowResample.Coefficients(Width, width, filter);
        var (verticalSize, verticalBounds, verticalKernel) = PillowResample.Coefficients(Height, height, filter);

        // Only the rows the vertical pass reads go through the horizontal one.
        var firstRow = verticalBounds[0];
        var lastRow = verticalBounds[(height - 1) * 2] + verticalBounds[(height - 1) * 2 + 1];

        var source = this;
        if (width != Width)
        {
            for (var i = 0; i < height; i++)
                verticalBounds[i * 2] -= firstRow;
            source = PillowResample.Horizontal(this, width, lastRow - firstRow, firstRow, horizontalSize, horizontalBounds, horizontalKernel);
        }
        return height != Height
            ? PillowResample.Vertical(source, height, verticalSize, verticalBounds, verticalKernel)
            : source;
    }
}

/// <summary>Pillow's Resample.c, for 8-bit images with the BOX and BILINEAR filters.</summary>
internal static class PillowResample
{
    private const int _precisionBits = 32 - 8 - 2;

    private static double Filter(ResampleFilter filter, double x)
    {
        if (filter == ResampleFilter.Box)
            return x > -0.5 && x <= 0.5 ? 1.0 : 0.0;
        if (x < 0.0)
            x = -x;
        return x < 1.0 ? 1.0 - x : 0.0;
    }

    private static double Support(ResampleFilter filter) => filter == ResampleFilter.Box ? 0.5 : 1.0;

    /// <summary>precompute_coeffs then normalize_coeffs_8bpc: each output pixel's input window and fixed-point weights.</summary>
    public static (int KernelSize, int[] Bounds, int[] Kernel) Coefficients(int inSize, int outSize, ResampleFilter filter)
    {
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = Support(filter) * filterScale;
        var kernelSize = (int)Math.Ceiling(support) * 2 + 1;

        var bounds = new int[outSize * 2];
        var weights = new double[outSize * kernelSize];
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = (xx + 0.5) * scale;
            var total = 0.0;
            var ss = 1.0 / filterScale;
            var xmin = (int)(center - support + 0.5);
            if (xmin < 0)
                xmin = 0;
            var xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
                xmax = inSize;
            xmax -= xmin;
            var k = xx * kernelSize;
            for (var x = 0; x < xmax; x++)
            {
                var w = Filter(filter, (x + xmin - center + 0.5) * ss);
                weights[k + x] = w;
                total += w;
            }
            for (var x = 0; x < xmax; x++)
            {
                if (total != 0.0)
                    weights[k + x] /= total;
            }
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        var kernel = new int[weights.Length];
        for (var i = 0; i < weights.Length; i++)
        {
            kernel[i] = weights[i] < 0
                ? (int)(-0.5 + weights[i] * (1 << _precisionBits))
                : (int)(0.5 + weights[i] * (1 << _precisionBits));
        }
        return (kernelSize, bounds, kernel);
    }

    private static byte Clip8(int value)
    {
        var shifted = value >> _precisionBits;
        return shifted < 0 ? (byte)0 : shifted > 255 ? (byte)255 : (byte)shifted;
    }

    public static RgbImage Horizontal(RgbImage source, int width, int height, int rowOffset, int kernelSize, int[] bounds, int[] kernel)
    {
        var result = new RgbImage(width, height);
        var input = source.Pixels;
        var output = result.Pixels;
        for (var yy = 0; yy < height; yy++)
        {
            var rowStart = (yy + rowOffset) * source.Width * 3;
            for (var xx = 0; xx < width; xx++)
            {
                var xmin = bounds[xx * 2];
                var xmax = bounds[xx * 2 + 1];
                var k = xx * kernelSize;
                int ss0 = 1 << (_precisionBits - 1), ss1 = ss0, ss2 = ss0;
                for (var x = 0; x < xmax; x++)
                {
                    var at = rowStart + (x + xmin) * 3;
                    ss0 += input[at] * kernel[k + x];
                    ss1 += input[at + 1] * kernel[k + x];
                    ss2 += input[at + 2] * kernel[k + x];
                }
                var outAt = (yy * width + xx) * 3;
                output[outAt] = Clip8(ss0);
                output[outAt + 1] = Clip8(ss1);
                output[outAt + 2] = Clip8(ss2);
            }
        }
        return result;
    }

    public static RgbImage Vertical(RgbImage source, int height, int kernelSize, int[] bounds, int[] kernel)
    {
        var width = source.Width;
        var result = new RgbImage(width, height);
        var input = source.Pixels;
        var output = result.Pixels;
        for (var yy = 0; yy < height; yy++)
        {
            var k = yy * kernelSize;
            var ymin = bounds[yy * 2];
            var ymax = bounds[yy * 2 + 1];
            for (var xx = 0; xx < width; xx++)
            {
                int ss0 = 1 << (_precisionBits - 1), ss1 = ss0, ss2 = ss0;
                for (var y = 0; y < ymax; y++)
                {
                    var at = ((y + ymin) * width + xx) * 3;
                    ss0 += input[at] * kernel[k + y];
                    ss1 += input[at + 1] * kernel[k + y];
                    ss2 += input[at + 2] * kernel[k + y];
                }
                var outAt = (yy * width + xx) * 3;
                output[outAt] = Clip8(ss0);
                output[outAt + 1] = Clip8(ss1);
                output[outAt + 2] = Clip8(ss2);
            }
        }
        return result;
    }
}
