using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision;
using static DeadlockAdvisor.Tests.Support.VisionData;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// Street Brawl (4v4) captures in Golden/vision/corpus, named streetbrawl_*: their outer slots are
/// blank, so they settle without a review. No six-a-side capture may be taken for one, since that
/// would apply a match with two heroes missing from each team.
/// </summary>
public class StreetBrawlTests
{
    [Fact]
    public void TheStreetBrawlCapturesSettleWithTheirOuterSlotsBlank()
    {
        var brawls = CorpusCapture.LoadAll(Golden.PathOf("vision", "corpus")).Where(capture => capture.Name.StartsWith("streetbrawl_", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(brawls);
        foreach (var capture in brawls)
        {
            var detection = Detector.Detect(capture.Image, Bank, capture.Labels.Grid, screenHeight: capture.Labels.ScreenHeight);
            Assert.NotNull(detection);
            Assert.Equal(StreetBrawl.OuterSlots, detection.BlankSlots);
            Assert.True(detection.IsSettled, $"{capture.Name} isn't settled");
            Assert.All(capture.Labels.Heroes, pair => Assert.Equal(pair.Value, detection.HeroAt(pair.Key)));
            Assert.Equal(capture.Labels.SelfSlot, detection.SelfSlot);
        }
    }

    [Fact]
    public void NoSixAsideCaptureIsTakenForStreetBrawl()
    {
        var sixAside = VisionCorpusTests.Labelled().Where(capture => !capture.Name.StartsWith("streetbrawl_", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(sixAside);
        foreach (var outcome in VisionEval.Run(sixAside, Bank))
            Assert.Empty(outcome.Detection.BlankSlots);
    }
}
