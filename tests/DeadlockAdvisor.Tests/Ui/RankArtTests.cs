using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests.Ui;

public class RankArtTests
{
    /// <summary>A solid red image of the given size, saved where a badge's art would be.</summary>
    public static void SaveBadge(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
            context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, width, height));
        bitmap.Save(path);
    }

    /// <summary>Badges are emblems wider than tall: cropping one to a square would cut its wings off.</summary>
    [AvaloniaFact]
    public void ABadgeIsFittedWholeIntoItsSquareNotCroppedToIt()
    {
        using var root = new TempDirectory();
        var art = new ArtService(new FakeLoggingService());
        art.SetAssetsDir(root.Path);
        SaveBadge(Path.Combine(art.FolderOf(ArtKind.Rank), "01.png"), 40, 20);

        Assert.True(art.Has(ArtKind.Rank, "01"));
        Assert.Equal(1, art.Count(ArtKind.Rank));
        var fitted = art.Get(ArtKind.Rank, "01", 32)!;

        Assert.Equal(new PixelSize(32, 32), fitted.PixelSize);
        // The whole width is used, so the badge fills the middle rows and leaves the top and bottom clear.
        Assert.Equal(255, AlphaAt(fitted, 1, 16));
        Assert.Equal(255, AlphaAt(fitted, 30, 16));
        Assert.Equal(0, AlphaAt(fitted, 16, 2));
        Assert.Equal(0, AlphaAt(fitted, 16, 29));
    }

    private static byte AlphaAt(Bitmap bitmap, int x, int y)
    {
        var pixel = new byte[4];
        var handle = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), pixel.Length, 4);
        }
        finally
        {
            handle.Free();
        }
        return pixel[3];
    }
}
