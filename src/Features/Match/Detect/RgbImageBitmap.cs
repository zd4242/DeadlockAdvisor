using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Features.Match.Detect;

public static class RgbImageBitmap
{
    /// <summary>A screen crop as something Avalonia can draw.</summary>
    public static WriteableBitmap ToBitmap(RgbImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var row = new byte[image.Width * 4];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (r, g, b) = image[x, y];
                row[x * 4] = b;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = r;
                row[x * 4 + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }
        return bitmap;
    }
}
