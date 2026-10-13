using DeadlockAdvisor.Vision;
using SkiaSharp;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>Pictures in a row with a couple of lines of text under them, laid out in a grid: what a person checks a draft label by.</summary>
public static class ContactSheet
{
    public sealed record Tile(IReadOnlyList<RgbImage?> Images, IReadOnlyList<string> Captions);

    public static void Save(IReadOnlyList<Tile> tiles, string path, int perRow = 6, int size = 80)
    {
        const int caption = 12;
        var pictures = tiles.Count == 0 ? 1 : tiles.Max(tile => tile.Images.Count);
        var lines = tiles.Count == 0 ? 2 : tiles.Max(tile => tile.Captions.Count);
        var (tileWidth, tileHeight) = (pictures * size + 4, size + lines * caption + 4);
        var rows = Math.Max(1, (tiles.Count + perRow - 1) / perRow);
        using var surface = SKSurface.Create(new SKImageInfo(perRow * tileWidth, rows * tileHeight));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(24, 24, 28));
        using var font = new SKFont(SKTypeface.Default, 10);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var i = 0; i < tiles.Count; i++)
        {
            var (x, y) = (i % perRow * tileWidth, i / perRow * tileHeight);
            for (var picture = 0; picture < tiles[i].Images.Count; picture++)
            {
                if (tiles[i].Images[picture] is { } image)
                    Draw(canvas, image, x + picture * size, y, size);
            }
            for (var line = 0; line < tiles[i].Captions.Count; line++)
                canvas.DrawText(tiles[i].Captions[line], x + 2, y + size + 10 + line * caption, font, text);
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
