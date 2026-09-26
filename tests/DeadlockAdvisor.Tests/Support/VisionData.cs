using System.Text.Json.Nodes;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>The reference art and captures the Python app's golden export copied into Golden/vision.</summary>
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
