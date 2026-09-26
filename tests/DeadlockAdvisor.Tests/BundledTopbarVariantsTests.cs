using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

public class BundledTopbarVariantsTests
{
    [Fact]
    public void TheBundleIsThePythonAppsLearnedVariants()
    {
        var bundled = BundledTopbarVariants.All().ToList();

        Assert.Equal(["apollo", "seven", "silver", "yamato"], bundled.Select(variant => variant.HeroId));
        Assert.All(bundled, variant => Assert.Equal("bundled_01.png", variant.FileName));
        // The golden export copied the Python repo's assets/topbar, variant folders included.
        foreach (var (heroId, _, bytes) in bundled)
            Assert.Contains(Directory.GetFiles(Path.Combine(TopbarDir, heroId)), file => File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes));
    }

    [Fact]
    public void InstallingAddsWhatsMissingAndLeavesCorrectionsAlone()
    {
        using var topbar = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(topbar.Path, "yamato"));
        File.WriteAllBytes(Path.Combine(topbar.Path, "yamato", "variant_01.png"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(topbar.Path, "seven"));
        var sevenBundled = BundledTopbarVariants.All().Single(variant => variant.HeroId == "seven").Bytes;
        File.WriteAllBytes(Path.Combine(topbar.Path, "seven", "ingame_01.png"), sevenBundled);

        // Silver isn't in this data, so it's left out.
        var installed = BundledTopbarVariants.Install(topbar.Path, ["apollo", "seven", "yamato"]);

        Assert.Equal(2, installed);
        Assert.True(File.Exists(Path.Combine(topbar.Path, "apollo", "bundled_01.png")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(topbar.Path, "yamato", "variant_01.png")));
        Assert.True(File.Exists(Path.Combine(topbar.Path, "yamato", "bundled_01.png")));
        // Seven's is already there under the Python app's name.
        Assert.Equal(["ingame_01.png"], Directory.GetFiles(Path.Combine(topbar.Path, "seven")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(topbar.Path, "silver")));
        Assert.Equal(0, BundledTopbarVariants.Install(topbar.Path, ["apollo", "seven", "yamato"]));
    }

    [Fact]
    public void TheReportMentionsInstalledAlternates()
    {
        var lines = new ArtDownloadReport([], VariantsInstalled: 4).Lines();

        Assert.Contains("Top-bar alternates: 4 installed, for heroes the game draws differently from their API art", lines);
    }
}
