using System.Reactive.Linq;
using DeadlockAdvisor.Features.MainWindow.MatchDownload;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>The dialog before a match data download, and what it estimates.</summary>
public sealed class MatchDownloadTests : IDisposable
{
    private readonly DataFixture _fixture = new();
    private static readonly List<Patch> _patches = MatchStatsMath.ParsePatches(SyntheticItemStatsApi.PatchTitles);
    private static readonly long _now = SyntheticItemStatsApi.Now.ToUnixTimeSeconds();

    public void Dispose() => _fixture.Dispose();

    /// <summary>09-16 over and settled with every match; 09-29 fetched yesterday.</summary>
    private static List<MatchSegment> Stored() =>
    [
        new(_patches[0], _patches[0].Start, _now - 86400, false, _now - 86400, SliceCounts.Empty, [], []),
        new(_patches[1], _patches[1].Start, _patches[0].Start - 1, true, _now, SliceCounts.Empty, [], []),
    ];

    [Fact]
    public void EstimatesFollowThePaceAndSizeOfTheLastDownloads()
    {
        var plan = MatchFetchPlan.For([], _patches, _now, includeRanks: false, heroCount: 38);
        var estimate = new MatchFetchEstimate(0.5, 10_000);

        Assert.Equal(166, plan.Calls);
        Assert.Equal(TimeSpan.FromSeconds(83), estimate.Time(plan));
        Assert.Equal(1_660_000, estimate.Bytes(plan));
        Assert.Equal(("about 2 min", "about 30 s", "about 5 s"),
            (MatchFetchEstimate.DescribeTime(TimeSpan.FromSeconds(80)), MatchFetchEstimate.DescribeTime(TimeSpan.FromSeconds(32)),
                MatchFetchEstimate.DescribeTime(TimeSpan.FromSeconds(1))));
        Assert.Equal(("1.6 MB", "450 KB"), (MatchFetchEstimate.DescribeBytes(1_600_000), MatchFetchEstimate.DescribeBytes(450_000)));

        // A third of the way toward how a run went; a run too short to tell changes nothing.
        var learned = estimate.Learn(100, TimeSpan.FromSeconds(80), 1_300_000);
        Assert.Equal((0.6, 11_000), (Math.Round(learned.SecondsPerCall, 9), Math.Round(learned.BytesPerCall, 9)));
        Assert.Same(estimate, estimate.Learn(10, TimeSpan.FromSeconds(80), 1_300_000));
    }

    [Fact]
    public void ThePlanSaysWhatHappensToEachPatchKept()
    {
        var stored = Stored();

        var everyMatch = MatchFetchPlan.For(stored, _patches, _now, includeRanks: false, heroCount: 38);
        var withRanks = MatchFetchPlan.For(stored, _patches, _now, includeRanks: true, heroCount: 38);

        Assert.Equal(
            [
                new PlanStep("Patch 09-29", "Still going: downloading its latest matches", true),
                new PlanStep("Patch 09-16", "Up to date: skipped", false),
            ],
            everyMatch.Describe());
        Assert.Equal(
            [
                new PlanStep("Patch 09-29", "Still going: downloading its latest matches, with rank groups", true),
                new PlanStep("Patch 09-16", "Up to date: adding just its rank groups", true),
            ],
            withRanks.Describe());
        Assert.Equal(2 * MatchFetchEstimate.PatchBytes, everyMatch.DiskBytes(stored));
        Assert.Equal(2 * MatchFetchEstimate.PatchWithRanksBytes, withRanks.DiskBytes(stored));
    }

    [Fact]
    public async Task TheDialogShowsBothChoicesAndStartsTheOneTaken()
    {
        var stored = Stored();
        var everyMatch = MatchFetchPlan.For(stored, _patches, _now, includeRanks: false, heroCount: 38);
        var withRanks = MatchFetchPlan.For(stored, _patches, _now, includeRanks: true, heroCount: 38);
        MatchDownloadChoice? started = null;
        _fixture.Modals.ShowModal(new Core.ViewModelBase());
        using var dialog = new MatchDownloadViewModel(_fixture.Modals, stored, everyMatch, withRanks, MatchFetchEstimate.Measured, false, true,
            choice => started = choice);

        Assert.Equal(["Patch 09-29", "Patch 09-16"], dialog.Stored.Select(fact => fact.Label));
        Assert.Equal(["1 day so far · all matches", "13 days · finished · all matches"], dialog.Stored.Select(fact => fact.Value));
        Assert.Equal("about 35 s · 1.3 MB", dialog.EveryMatchDetail);
        Assert.Equal("about 7 min · 13.6 MB", dialog.WithRanksDetail);
        Assert.Equal([true, false], dialog.Steps.Select(step => step.Downloads));
        Assert.Equal("Downloading 1 patch takes about 35 s. It runs in the background, so you can keep using the app, "
                     + "and uses about 900 KB of disk space.", dialog.Summary);

        dialog.IncludeRanks = true;
        Assert.False(dialog.EveryMatchOnly);
        Assert.Equal([true, true], dialog.Steps.Select(step => step.Downloads));
        Assert.StartsWith("Downloading 2 patches takes about 7 min.", dialog.Summary);

        Assert.True(dialog.KeepUpToDate);
        dialog.KeepUpToDate = false;
        await dialog.DownloadCommand.Execute();
        Assert.Equal(new MatchDownloadChoice(withRanks, true, false), started);
        Assert.False(_fixture.Modals.IsModalOpen);
    }

    [Fact]
    public async Task NothingToFetchCantBeDownloaded()
    {
        var nothing = new MatchFetchPlan(_now, [_patches[0]], []);
        using var dialog = new MatchDownloadViewModel(_fixture.Modals, [], nothing, nothing, MatchFetchEstimate.Measured, false, true, _ => { });

        Assert.False(await dialog.DownloadCommand.CanExecute.FirstAsync());
        Assert.Equal("Everything is already up to date, so there's nothing to download.", dialog.Summary);
        Assert.Equal("nothing to download", dialog.EveryMatchDetail);
    }
}
