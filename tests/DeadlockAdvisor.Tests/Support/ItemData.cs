using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>The item art and labelled boards in Golden/vision/items.</summary>
public static class ItemData
{
    private static readonly Lazy<ItemTemplates> _templates = new(() => ItemTemplates.Load(ArtDir, Golden.LoadStore().Items.Values));

    /// <summary>Every item's icon, at 100 pixels (the art download's are 200): a cell is 36 pixels on a 1440p screen.</summary>
    public static string ArtDir => Golden.PathOf("vision", "items", "art");

    public static string CorpusDir => Golden.PathOf("vision", "items", "corpus");

    public static ItemTemplates Templates => _templates.Value;

    /// <summary>Put the item art where the app looks for it, as the art download leaves it.</summary>
    public static void CopyArtInto(string assetsDir)
    {
        var target = Path.Combine(assetsDir, "items");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(ArtDir, "*.png"))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>
    /// A whole scoreboard as the screen capture takes it: the hero strip of the 2560×1440 fixture (so the heroes and the net worth read as
    /// <c>VisionData.Capture()</c> does) over the items and bars of a labelled board, which has no readable portraits. The board's rows are
    /// moved up to meet the fixture's grid, 13 pixels higher.
    /// </summary>
    public static ScreenCapture ScoreboardOf(ItemCorpusBoard board)
    {
        const int shift = 13;
        var strip = VisionData.Capture().Band;
        var composite = new RgbImage(strip.Width, board.Image.Height);
        var row = strip.Width * 3;
        for (var y = 0; y < composite.Height; y++)
        {
            var (from, at) = y < strip.Height ? (strip, y) : (board.Image, y + shift);
            if (at < from.Height)
                Buffer.BlockCopy(from.Pixels, at * row, composite.Pixels, y * row, row);
        }
        return new ScreenCapture(composite, 2560, 1440);
    }
}
