using System.IO;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Features.Match.Detect;

/// <summary>
/// Captures kept in &lt;data root&gt;/captures, each band beside a note of what was made of it, the
/// latest few of each kind. A read at a resolution or in a scene the test fixtures don't cover is
/// exactly what the next fixture should be made from.
/// </summary>
/// <param name="Prefix">What starts each file's name, which is also how one kind is pruned without touching another.</param>
/// <param name="Keep">Enough to learn from without filling the disk.</param>
/// <param name="NoteExtension">The note's file extension, with its dot.</param>
public sealed record CaptureArchive(string Prefix, int Keep, string NoteExtension)
{
    public const string FolderName = "captures";

    /// <summary>Captures net worth couldn't be read off (a side whose pills didn't add up), with what was read.</summary>
    public static CaptureArchive NetWorth { get; } = new("networth", 20, ".txt");

    /// <summary>Every applied detection, labelled with the heroes that were applied: the corpus detection is measured on.</summary>
    public static CaptureArchive Detections { get; } = new("detect", 60, ".json");

    /// <summary>Save the band and its note; returns the image's path, or null if it couldn't be written.</summary>
    public string? Save(string dataRoot, RgbImage band, DateTimeOffset at, string note)
    {
        try
        {
            var folder = Path.Combine(dataRoot, FolderName);
            Directory.CreateDirectory(folder);
            var name = $"{Prefix}_{at.ToLocalTime():yyyyMMdd_HHmmss_fff}";
            var image = Path.Combine(folder, name + ".png");
            Png.Save(band, image);
            File.WriteAllText(Path.Combine(folder, name + NoteExtension), note + Environment.NewLine);
            Prune(folder);
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Prune(string folder)
    {
        var old = Directory.GetFiles(folder, $"{Prefix}_*.png").Order(StringComparer.Ordinal).SkipLast(Keep);
        foreach (var image in old)
        {
            File.Delete(image);
            File.Delete(Path.ChangeExtension(image, NoteExtension));
        }
    }
}
