using System.IO;
using SkiaSharp;

namespace DeadlockAdvisor.Vision;

/// <summary>Loading reference art and captures as RGB, whatever format they're in.</summary>
public static class ImageFile
{
    public static readonly IReadOnlyList<string> Suffixes = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <summary>
    /// PNG through our own decoder (exactly Pillow's pixels); anything else through Skia, unpremultiplied
    /// and without colour management, which is as close as another decoder gets.
    /// </summary>
    public static RgbImage Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            return Png.Decode(bytes);

        using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new InvalidDataException($"Can't read {Path.GetFileName(path)}.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"Can't decode {Path.GetFileName(path)}.");
        var rgba = bitmap.Bytes;
        var image = new RgbImage(info.Width, info.Height);
        for (var i = 0; i < info.Width * info.Height; i++)
        {
            image.Pixels[i * 3] = rgba[i * 4];
            image.Pixels[i * 3 + 1] = rgba[i * 4 + 1];
            image.Pixels[i * 3 + 2] = rgba[i * 4 + 2];
        }
        return image;
    }
}
