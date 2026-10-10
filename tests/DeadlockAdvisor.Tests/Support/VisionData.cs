using System.Text.Json.Nodes;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>The reference art and captures in Golden/vision.</summary>
public static class VisionData
{
    private static readonly Lazy<TemplateBank> _bank = new(() => TemplateBank.Load(TopbarDir));

    public static string TopbarDir => Golden.PathOf("vision", "topbar");

    public static TemplateBank Bank => _bank.Value;

    /// <summary>An image by its path under Golden/vision, e.g. "fixtures/cropped_strip_1769.png".</summary>
    public static RgbImage Image(string relative) => ImageFile.Load(Golden.PathOf(["vision", .. relative.Split('/')]));

    /// <summary>A fixture's .json: what it should read as, and the search bounds a crop without a screen size needs.</summary>
    public static JsonNode FixtureSpec(string fixture) =>
        JsonNode.Parse(File.ReadAllText(Golden.PathOf("vision", "fixtures", fixture + ".json")))!;

    /// <summary>Detect a fixture the way its spec says to.</summary>
    public static Detection? DetectFixture(string fixture)
    {
        var spec = FixtureSpec(fixture);
        return Detector.Detect(Image($"fixtures/{fixture}.png"), Bank,
            pitchRange: spec["pitch_range"] is JsonArray pitch ? ((double)pitch[0]!, (double)pitch[1]!) : null,
            topRange: spec["top_range"] is JsonArray top ? ((double)top[0]!, (double)top[1]!) : null,
            screenHeight: (int?)spec["screen_height"]);
    }

    /// <summary>A capture of a 2560×1440 screen whose expected reading is in the fixture's .json.</summary>
    public static ScreenCapture Capture(string fixture = "screen_2560x1440_band_2") =>
        new(Image($"fixtures/{fixture}.png"), 2560, 1440);

    /// <summary>
    /// A capture whose strip above every portrait is greyed out: every hero still reads, but nobody is lit as you. A slot
    /// can be given the bright teal a kill streak turns its backplate.
    /// </summary>
    public static ScreenCapture CaptureWithoutYou(string fixture = "screen_2560x1440_band", int? streakSlot = null)
    {
        var capture = Capture(fixture);
        var band = capture.Band;
        var pixels = (byte[])band.Pixels.Clone();
        var grid = Detector.Detect(band, Bank, screenHeight: capture.ScreenHeight)!.Geometry;
        var bottom = (int)Math.Round(grid.Top - 0.12 * grid.ArtHeight);

        void Paint(int from, int to, (byte R, byte G, byte B) color)
        {
            for (var y = 1; y < bottom; y++)
            {
                for (var x = Math.Max(0, from); x < Math.Min(band.Width, to); x++)
                    (pixels[(y * band.Width + x) * 3], pixels[(y * band.Width + x) * 3 + 1], pixels[(y * band.Width + x) * 3 + 2]) = color;
            }
        }

        Paint(0, band.Width, (90, 90, 90));
        if (streakSlot is { } slot)
        {
            var center = grid.Centers()[slot];
            var half = grid.Pitch * 0.3;
            Paint((int)Math.Round(center - half), (int)Math.Round(center + half), (150, 255, 220));
        }
        return new(new RgbImage(band.Width, band.Height, pixels), capture.ScreenWidth, capture.ScreenHeight);
    }

    /// <summary>Copy the reference art into an assets folder, as the art download would.</summary>
    public static void CopyTopbarInto(string assetsDir)
    {
        foreach (var file in Directory.GetFiles(TopbarDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(assetsDir, "topbar", Path.GetRelativePath(TopbarDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
