using System.IO;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Features.Match.Detect;

/// <summary>
/// Keeps the captures net worth couldn't be read off (a side whose pills didn't add up), with what
/// was read, in &lt;data root&gt;/captures: a failed read at a resolution or in a scene the test
/// fixtures don't cover is exactly what the next fixture should be made from.
/// </summary>
public static class NetWorthCaptures
{
    public const string FolderName = "captures";

    /// <summary>Enough to catch a problem without filling the disk if every read fails.</summary>
    public const int Keep = 20;

    /// <summary>Save the band and a note of what was read beside it; returns the image's path, or null if it couldn't be written.</summary>
    public static string? Save(string dataRoot, RgbImage band, DateTimeOffset at, string note)
    {
        try
        {
            var folder = Path.Combine(dataRoot, FolderName);
            Directory.CreateDirectory(folder);
            var name = $"networth_{at.ToLocalTime():yyyyMMdd_HHmmss_fff}";
            var image = Path.Combine(folder, name + ".png");
            Png.Save(band, image);
            File.WriteAllText(Path.Combine(folder, name + ".txt"), note + Environment.NewLine);
            Prune(folder);
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Prune(string folder)
    {
        var old = Directory.GetFiles(folder, "networth_*.png").Order(StringComparer.Ordinal).SkipLast(Keep);
        foreach (var image in old)
        {
            File.Delete(image);
            File.Delete(Path.ChangeExtension(image, ".txt"));
        }
    }
}
