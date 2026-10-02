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

        Assert.Equal(160, plan.Calls);
        Assert.Equal(TimeSpan.FromSeconds(80), estimate.Time(plan));
        Assert.Equal(1_600_000, estimate.Bytes(plan));
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

        Assert.Equal([("Patch 09-29", "refreshed: it's still collecting matches"), ("Patch 09-16", "kept as it is: complete")],
            everyMatch.Describe(stored));
        Assert.Equal(
            [("Patch 09-29", "refreshed: it's still collecting matches · with rank groups"), ("Patch 09-16", "kept, adding its rank groups · with rank groups")],
            withRanks.Describe(stored));
        Assert.Equal(2 * MatchFetchEstimate.PatchBytes, everyMatch.DiskBytes(stored));
        Assert.Equal(2 * MatchFetchEstimate.PatchWithRanksBytes, withRanks.DiskBytes(stored));
    }

    [Fact]
    public async Task TheDialogShowsBothChoicesAndStartsTheOneTaken()
    {
        var stored = Stored();
        var everyMatch = MatchFetchPlan.For(stored, _patches, _now, includeRanks: false, heroCount: 38);
        var withRanks = MatchFetchPlan.For(stored, _patches, _now, includeRanks: true, heroCount: 38);
        (MatchFetchPlan Plan, bool Ranks)? started = null;
        _fixture.Modals.ShowModal(new Core.ViewModelBase());
        using var dialog = new MatchDownloadViewModel(_fixture.Modals, stored, everyMatch, withRanks, MatchFetchEstimate.Measured, false,
            (plan, ranks) => started = (plan, ranks));

        Assert.Equal(["Patch 09-29", "Patch 09-16"], dialog.Stored.Select(fact => fact.Label));
        Assert.Equal(["1 day so far · every match", "13 days · complete · every match"], dialog.Stored.Select(fact => fact.Value));
        Assert.Equal("about 30 s · 1.2 MB", dialog.EveryMatchDetail);
        Assert.Equal("about 6 min · 13.4 MB", dialog.WithRanksDetail);
        Assert.StartsWith("80 calls, about 30 s, in the background. About 900 KB on disk", dialog.Summary);

        dialog.IncludeRanks = true;
        Assert.False(dialog.EveryMatchOnly);
        Assert.Equal("kept, adding its rank groups · with rank groups", dialog.Steps[1].Value);
        Assert.StartsWith("880 calls", dialog.Summary);

        await dialog.DownloadCommand.Execute();
        Assert.Equal((withRanks, true), started);
        Assert.False(_fixture.Modals.IsModalOpen);
    }

    [Fact]
    public async Task NothingToFetchCantBeDownloaded()
    {
        var nothing = new MatchFetchPlan(_now, [_patches[0]], []);
        using var dialog = new MatchDownloadViewModel(_fixture.Modals, [], nothing, nothing, MatchFetchEstimate.Measured, false, (_, _) => { });

        Assert.False(await dialog.DownloadCommand.CanExecute.FirstAsync());
        Assert.Equal("Every patch's counts are complete: nothing to download.", dialog.Summary);
        Assert.Equal("nothing to fetch", dialog.EveryMatchDetail);
    }
}
