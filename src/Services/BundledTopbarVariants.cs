using System.IO;

namespace DeadlockAdvisor.Services;

/// <summary>
/// Alternate top-bar portraits shipped inside the exe: heroes whose API art doesn't look like what the
/// game draws (Apollo, Seven, Silver, Yamato), learned from corrections in the Python app. Installing
/// them with the art download spares every new install from correcting the same heroes by hand.
/// </summary>
public static class BundledTopbarVariants
{
    private const string _prefix = "TopbarVariants/";

    /// <summary>(hero id, file name) → the image, for every bundled variant.</summary>
    public static IEnumerable<(string HeroId, string FileName, byte[] Bytes)> All()
    {
        var assembly = typeof(BundledTopbarVariants).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(_prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            // MSBuild's RecursiveDir keeps the Windows separator: "TopbarVariants/apollo\bundled_01.png".
            var parts = name[_prefix.Length..].Replace('\\', '/').Split('/');
            if (parts.Length != 2)
                continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            yield return (parts[0], parts[1], copy.ToArray());
        }
    }

    /// <summary>
    /// Write the variants for <paramref name="heroIds"/> into <paramref name="topbarDir"/>/&lt;hero&gt;/,
    /// skipping any whose image is already in that folder under any name (the Python app's own copies,
    /// when sharing its data folder). Returns how many were written.
    /// </summary>
    public static int Install(string topbarDir, IEnumerable<string> heroIds)
    {
        var wanted = heroIds.ToHashSet(StringComparer.Ordinal);
        var installed = 0;
        foreach (var (heroId, fileName, bytes) in All())
        {
            if (!wanted.Contains(heroId))
                continue;
            var folder = Path.Combine(topbarDir, heroId);
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any(existing => File.ReadAllBytes(existing).AsSpan().SequenceEqual(bytes)))
                continue;
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, fileName), bytes);
            installed++;
        }
        return installed;
    }
}
