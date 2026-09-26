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
