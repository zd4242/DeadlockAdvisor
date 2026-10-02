using System.Text.Json.Nodes;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests;

/// <summary>The match data's maths, on inputs small enough to check by hand.</summary>
public class MatchStatsMathTests
{
    private static Dictionary<long, WinTotals> Totals(params (long Item, long Wins, long Matches)[] rows) =>
        rows.ToDictionary(row => row.Item, row => new WinTotals(row.Wins, row.Matches));

    [Fact]
    public void RawLiftComparesEachItemWithItselfThenRemovesTheTierMean()
    {
        var tiers = new Dictionary<long, int> { [1] = 1, [2] = 1, [3] = 2 };
        var baseline = Totals((1, 5000, 10000), (2, 6000, 10000), (3, 4000, 10000)); // 50%, 60%, 40%
        var query = Totals(
            (1, 2400, 4000), // 60% vs its own 50% -> +10
            (2, 3300, 6000), // 55% vs its own 60% -> -5
            (3, 1000, 2000), // 50% vs its own 40% -> +10, but alone in tier 2
            (4, 9000, 10000)); // not in scope -> ignored, even in the means

        var lifts = MatchStatsMath.RawLifts(query, baseline, tiers);

        // Tier 1's mean delta, weighted by matches: (4000*10 + 6000*-5) / 10000 = 1.
        Assert.Equal(9, lifts[1].Lift, 9);
        Assert.Equal(-6, lifts[2].Lift, 9);
        Assert.Equal(0, lifts[3].Lift, 9); // a tier of one has nothing to stand out from
        Assert.False(lifts.ContainsKey(4));
        Assert.Equal(4000, lifts[1].Matches);
        // 100 x sqrt(0.6 x 0.4 / 4000): the textbook binomial error, in points
        Assert.Equal(100 * Math.Sqrt(0.24 / 4000), lifts[1].Se, 9);
    }

    [Fact]
    public void SmallSamplesCountTowardTheMeanButGetNoLift()
    {
        var tiers = new Dictionary<long, int> { [1] = 1, [2] = 1 };
        var baseline = Totals((1, 5000, 10000), (2, 5000, 10000));
        var query = Totals((1, 2400, 4000), (2, 500, 1000)); // +10 over 4000 matches, 0 over 1000

        var lifts = MatchStatsMath.RawLifts(query, baseline, tiers, minN: 2000);

        Assert.Equal([1L], lifts.Keys);
        Assert.Equal(2, lifts[1].Lift, 9); // mean = 4000*10 / 5000 = 8
    }

    [Fact]
    public void Tau2IsSpreadMinusNoiseAndShrinkUsesIt()
    {
        RawLift[] lifts = [new(5000, 3.0, 1.0), new(5000, -3.0, 1.0)];
        var tau2 = MatchStatsMath.EstimateTau2(lifts);
        Assert.Equal(8, tau2, 9); // spread 9 - noise 1
        Assert.Equal(3.0 * 8 / 9, MatchStatsMath.Shrink(lifts[0], tau2), 9);

        // Lifts no wider than their noise: no detectable signal, everything -> 0.
        RawLift[] noiseOnly = [new(5000, 1.0, 2.0), new(5000, -1.0, 2.0)];
        Assert.Equal(0.0, MatchStatsMath.EstimateTau2(noiseOnly));
        Assert.Equal(0.0, MatchStatsMath.Shrink(noiseOnly[0], 0.0));
    }

    [Fact]
    public void NoiseScaleMeasuresHowFarOffTheStandardErrorsAre()
    {
        // Halves that differ by 2 and 2 points, each claiming se 1: expected (a - b)^2 = 1 + 1 = 2,
        // observed 4 -> errors are sqrt(2) too small.
        (RawLift, RawLift)[] pairs =
        [
            (new(5000, 1.0, 1.0), new(5000, -1.0, 1.0)),
            (new(5000, 3.0, 1.0), new(5000, 1.0, 1.0)),
        ];

        Assert.Equal(Math.Sqrt(2), MatchStatsMath.NoiseScale(pairs), 9);
        Assert.Equal(1.0, MatchStatsMath.NoiseScale([]));
        var scaled = MatchStatsMath.Rescale(new OrderedDictionary<string, RawLift> { ["x"] = pairs[0].Item1 }, 2.0);
        Assert.Equal(2.0, scaled["x"].Se);
        Assert.Equal(1.0, scaled["x"].Lift);
    }

    [Fact]
    public void PatchesAreDatedByTitleNotByWhenTheyWerePosted()
    {
        var patches = MatchStatsMath.ParsePatches([
            "09-16-2026 Update", "08-22-2026 Update", "08-12-2026 Update", "Community update", "08-22-2026 Update (hotfix)",
        ]);

        Assert.Equal(["09-16", "08-22", "08-12"], patches.Select(p => p.Label));
        Assert.Equal(1789603200, patches[0].Start); // 2026-09-17 00:00 UTC, the day after
    }

    [Fact]
    public void TotalsAndMergingHalves()
    {
        var first = MatchStatsMath.Totals([(7, 10, 20)]);
        var second = MatchStatsMath.Totals([(7, 5, 20), (8, 1, 2)]);

        Assert.Equal(new Dictionary<long, WinTotals> { [7] = new(10, 20) }, first);
        Assert.Equal(new Dictionary<long, WinTotals> { [7] = new(15, 40), [8] = new(1, 2) }, MatchStatsMath.Merge(first, second));
        Assert.Equal(new Dictionary<long, WinTotals> { [7] = new(5, 20), [8] = new(0, 0) },
            MatchStatsMath.Subtract(MatchStatsMath.Merge(first, second), MatchStatsMath.Totals([(7, 10, 20), (8, 3, 5)])));
    }

    // Per half: other players win item 1 and item 2 half the time (2500 of 5000 each); the enemy "e"
    // buys item 1 in 4000 more and wins only 1000, so every match has item 1 at 3500 of 9000. Against
    // "e", the others still win half their games with either item.
    private static readonly Dictionary<long, int> _twoItems = new() { [1] = 1, [2] = 1 };
    private static Dictionary<long, WinTotals> EveryMatchHalf => Totals((1, 3500, 9000), (2, 2500, 5000));
    private static Dictionary<long, WinTotals> OwnHalf => Totals((1, 1000, 4000));
    private static Dictionary<long, WinTotals> AgainstHalf => Totals((1, 1500, 3000), (2, 1500, 3000));

    [Fact]
    public void AnEnemysOwnPurchasesAreTakenOutOfItsBaseline()
    {
        var heroes = new OrderedDictionary<string, Halves> { ["e"] = new(AgainstHalf, AgainstHalf) };
        var baseline = new Halves(EveryMatchHalf, EveryMatchHalf);

        // Against every match, item 1 gains 50 - 38.9 = 11.1 points and item 2 nothing; the tier mean
        // at equal weight is 5.6, so item 1 looks like a counter to "e" and item 2 like a poor buy.
        var counted = MatchStatsMath.AnalyseFamily(baseline, heroes, _twoItems);
        Assert.Equal(50.0 / 9, counted.Full[new HeroItem("e", 1)].Lift, 9);
        Assert.Equal(-50.0 / 9, counted.Full[new HeroItem("e", 2)].Lift, 9);

        // Without e's own purchases, item 1 is back at 50% and neither item moves.
        var own = new Dictionary<string, Halves> { ["e"] = new(OwnHalf, OwnHalf) };
        var excluded = MatchStatsMath.AnalyseFamily(baseline, heroes, _twoItems, own);
        Assert.Equal(0, excluded.Full[new HeroItem("e", 1)].Lift, 9);
        Assert.Equal(0, excluded.Full[new HeroItem("e", 2)].Lift, 9);
        Assert.Equal(0, excluded.Halves.First[new HeroItem("e", 1)].Lift, 9);
    }

    [Fact]
    public void AnalyseFamilyCalibratesNoiseFromTheHalves()
    {
        // One hero, two tier-1 items. Item 1 beats its baseline by 10 points in both halves, item 2
        // by -5 in both: the halves agree exactly, so the measured noise is 0 and nothing needs shrinking.
        var tiers = new Dictionary<long, int> { [1] = 1, [2] = 1 };
        var baseHalf = Totals((1, 2500, 5000), (2, 2500, 5000)); // 50% each
        var heroHalf = Totals((1, 1800, 3000), (2, 1350, 3000)); // 60% and 45%

        var stats = MatchStatsMath.AnalyseFamily(
            new Halves(baseHalf, baseHalf),
            new OrderedDictionary<string, Halves> { ["h"] = new Halves(heroHalf, heroHalf) },
            tiers);

        Assert.Equal(2, stats.Pairs);
        Assert.Equal(0.0, stats.Scale);
        // Tier mean of (+10, -5) at equal weight is 2.5 -> lifts +7.5 and -7.5.
        Assert.Equal(7.5, stats.Full[new HeroItem("h", 1)].Lift, 9);
        Assert.Equal(0, stats.Full[new HeroItem("h", 1)].Se, 9);
        Assert.Equal(6000, stats.Full[new HeroItem("h", 1)].Matches);
    }


    [Fact]
    public void FamilyReliabilityStepsSplitHalfUpToTheWholeWindow()
    {
        var empty = new OrderedDictionary<HeroItem, RawLift>();

        Assert.Equal(0.8 / 1.4, new FamilyStats(empty, (empty, empty), 0, 1.0, 0.1, 0.4, 0.4).Reliability!.Value, 9);
        Assert.Equal(0.0, new FamilyStats(empty, (empty, empty), 0, 1.0, 0.1, -0.2, 0.0).Reliability);
    }

    private static readonly Patch _patch = new("09-16-2026 Update", 1789603200);
    private static readonly Item[] _items = [new("one", "One", "weapon", 1, GameId: 1), new("two", "Two", "weapon", 1, GameId: 2)];

    private static SliceCounts Slice(Halves baseline, params (string Hero, Halves As, Halves Against)[] heroes) =>
        new(baseline,
            new OrderedDictionary<string, Halves>(heroes.Select(hero => KeyValuePair.Create(hero.Hero, hero.As))),
            new OrderedDictionary<string, Halves>(heroes.Select(hero => KeyValuePair.Create(hero.Hero, hero.Against))));

    private static MatchSegment Segment(SliceCounts everyMatch, IReadOnlyList<RankBucket>? ranks = null, IReadOnlyList<SliceCounts>? byRank = null) =>
        new(_patch, _patch.Start, _patch.Start + 10 * 86400, false, _patch.Start + 10 * 86400, everyMatch, ranks ?? [], byRank ?? []);

    private static Halves Both(Dictionary<long, WinTotals> half) => new(half, half);

    [Fact]
    public void TheEnemyFamilyTakesEachEnemysOwnPurchasesFromTheSameSegment()
    {
        var segment = Segment(Slice(Both(EveryMatchHalf), ("e", Both(OwnHalf), Both(AgainstHalf))));

        var result = MatchStatsMath.Analyse([segment], null, _items);

        var against = result.Families.Single(report => report.Relation == "against");
        Assert.Equal(0, against.Stats.Full[new HeroItem("e", 1)].Lift, 9);
        Assert.Equal(0, against.Stats.Full[new HeroItem("e", 2)].Lift, 9);
        Assert.False(against.Meta().ContainsKey("own_excluded"));

        // Meta from a download before own purchases were taken out still says so.
        var meta = JsonNode.Parse(
            """{"families": {"against/full": {"kept": true, "rows": 5, "reliability": 0.7, "own_excluded": false}}}""")!.AsObject();
        Assert.Equal(["Enemies: 5 measurements, reliability 0.70", MatchStatsMath.OwnIncludedNote], MatchStatsMath.FamilyLines(meta));
    }

    [Fact]
    public void QueryParamsPerKind()
    {
        Assert.Empty(MatchStatsMath.BaselineParams());
        Assert.Equal([new("bucket", "hero")], MatchStatsMath.AsParams());
        Assert.Equal([new("enemy_hero_ids", "13")], MatchStatsMath.AgainstParams(13));
        Assert.Equal(
            "https://api.deadlock-api.com/v1/analytics/item-stats?enemy_hero_ids=13&min_matches=1&min_unix_timestamp=10&max_unix_timestamp=20",
            MatchStatsService.ItemStatsUrl(MatchStatsMath.AgainstParams(13), 10, 20));
    }

    [Fact]
    public void RankGroupsCoverEveryBadgeOnceTwoRanksEach()
    {
        var ranks = MatchStatsMath.RankBuckets(new Dictionary<int, string>
        {
            [1] = "Initiate", [2] = "Seeker", [5] = "Mystic", [6] = "Ritualist", [9] = "Phantom", [11] = "Eternus",
        });

        Assert.Equal([(1, 2), (3, 4), (5, 6), (7, 8), (9, 11)], ranks.Select(rank => (rank.FirstTier, rank.LastTier)));
        Assert.Equal(("Initiate – Seeker", 0, 29), (ranks[0].Name, ranks[0].MinBadge, ranks[0].MaxBadge));
        Assert.Equal(("Mystic – Ritualist", 50, 69), (ranks[2].Name, ranks[2].MinBadge, ranks[2].MaxBadge));
        Assert.Equal(("Phantom – Eternus", 90, 116), (ranks[4].Name, ranks[4].MinBadge, ranks[4].MaxBadge));
        Assert.Equal("Rank 3 – Rank 4", ranks[1].Name);
        Assert.All(ranks.Zip(ranks.Skip(1)), pair => Assert.Equal(pair.First.MaxBadge + 1, pair.Second.MinBadge));
        Assert.Equal([new("enemy_hero_ids", "13"), new("min_average_badge", "50"), new("max_average_badge", "69")],
            MatchStatsMath.RankParams(MatchStatsMath.AgainstParams(13), ranks[2]));
    }

    [Fact]
    public void ARankRangeAddsUpItsGroupsAndEveryMatchTakesTheUnfilteredTotals()
    {
        var ranks = MatchStatsMath.RankBuckets(new Dictionary<int, string>());
        var byRank = ranks.Select(rank => Slice(Both(Totals((1, rank.FirstTier, 10 * rank.FirstTier))))).ToList();
        byRank[1] = Slice(Both(Totals((1, 3, 30), (2, 1, 4))));
        var segment = Segment(Slice(Both(Totals((1, 999, 2000)))), ranks, byRank);

        Assert.Equal(new WinTotals(999, 2000), segment.Slice(null)!.Baseline.First[1]);
        // Any group the range touches counts: tiers 5 to 10 take Mystic – Ritualist and up.
        var mysticUp = segment.Slice(new RankRange(5, 10))!.Baseline.First;
        Assert.Equal(new WinTotals(5 + 7 + 9, 210), mysticUp[1]);
        Assert.False(mysticUp.ContainsKey(2));
        var second = segment.Slice(new RankRange(3, 4))!.Baseline.First;
        Assert.Equal([new WinTotals(3, 30), new WinTotals(1, 4)], [second[1], second[2]]);
        Assert.Null(Segment(SliceCounts.Empty).Slice(new RankRange(3, 4)));

        var everyMatch = segment.Slice(null)!;
        Assert.Equal(new WinTotals(999 - 3, 2000 - 30), everyMatch.Minus(byRank[1]).Baseline.First[1]);
        Assert.Equal(new WinTotals(999 + 3, 2000 + 30), everyMatch.Plus(byRank[1]).Baseline.Second[1]);
    }

    [Fact]
    public void RankRangesAreDescribedByTheirEnds()
    {
        var ranks = MatchStatsMath.RankBuckets(new Dictionary<int, string>
        {
            [1] = "Initiate", [2] = "Seeker", [5] = "Mystic", [6] = "Ritualist", [7] = "Emissary", [8] = "Oracle", [9] = "Phantom", [11] = "Eternus",
        });

        Assert.Equal("every match", MatchStatsMath.DescribeRange(ranks, null));
        Assert.Equal("every ranked match", MatchStatsMath.DescribeRange(ranks, new RankRange(1, 11)));
        Assert.Equal("Mystic+", MatchStatsMath.DescribeRange(ranks, new RankRange(5, 11)));
        Assert.Equal("Phantom+", MatchStatsMath.DescribeRange(ranks, new RankRange(9, 11)));
        Assert.Equal("up to Oracle", MatchStatsMath.DescribeRange(ranks, new RankRange(1, 8)));
        Assert.Equal("Mystic – Oracle", MatchStatsMath.DescribeRange(ranks, new RankRange(5, 8)));
        Assert.Equal("Emissary – Oracle", MatchStatsMath.DescribeRange(ranks, new RankRange(7, 8)));
    }

    [Fact]
    public void TheRankRangeIsReadBackFromTheMeta()
    {
        MatchSegment[] segments = [Segment(SliceCounts.Empty)];
        var ranged = new FetchResult([], [], segments, new RankRange(5, 11), "Mystic+").Meta();
        var every = new FetchResult([], [], segments, null, "every match").Meta();

        Assert.Equal(new RankRange(5, 11), MatchStatsMath.RankOf(ranged));
        Assert.Equal("Mystic+", MatchStatsMath.RankLabel(ranged));
        Assert.Contains("· Mystic+ ·", MatchStatsMath.Summary(ranged, 1));
        Assert.Contains("Leaning toward Mystic+ where it plays differently.", MatchStatsMath.DataNote(ranged, 1));
        Assert.Null(MatchStatsMath.RankOf(every));
        Assert.Null(MatchStatsMath.RankLabel(every));
        Assert.Equal("all", every["rank"]!.GetValue<string>());
        Assert.Equal("09-16", every["segments"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public void ASegmentRoundTripsThroughItsFileAndIsCompleteOnceItsMatchesSettle()
    {
        var ranks = MatchStatsMath.RankBuckets(new Dictionary<int, string> { [1] = "Initiate" });
        var byRank = ranks.Select(rank => Slice(Both(Totals((1, rank.FirstTier, 10))), ("e", new Halves(Totals((2, 1, 3)), []), Both(Totals((1, 2, 5)))))).ToList();
        var segment = Segment(Slice(new Halves(Totals((1, 5, 10)), Totals((2, 6, 12))), ("e", Both(OwnHalf), Both(AgainstHalf))), ranks, byRank)
            with { Ended = true };

        var parsed = MatchSegment.Parse(segment.ToJsonBytes());

        Assert.Equal(segment.ToJsonBytes(), parsed.ToJsonBytes());
        Assert.Equal(("Initiate – Rank 2", 5), (parsed.Ranks[0].Name, parsed.Ranks.Count));
        Assert.Equal(new WinTotals(6, 12), parsed.EveryMatch.Baseline.Second[2]);
        Assert.False(parsed.EveryMatch.Baseline.Second.ContainsKey(1));
        Assert.Equal(new WinTotals(1, 3), parsed.ByRank[4].As["e"].First[2]);
        Assert.Empty(parsed.ByRank[4].As["e"].Second);
        Assert.Equal("2026-09-16.json", parsed.FileName);

        // Fetched at the moment it ended: matches still coming in. A day later: settled.
        Assert.False(segment.Complete);
        Assert.True((segment with { FetchedAt = segment.Until + MatchSegment.SettleSeconds }).Complete);
        Assert.False((segment with { Ended = false, FetchedAt = segment.Until + MatchSegment.SettleSeconds }).Complete);
    }

    private static OrderedDictionary<HeroItem, RawLift> Lifts(params (string Hero, long Item, int Matches, double Lift, double Se)[] lifts) =>
        new(lifts.Select(lift => KeyValuePair.Create(new HeroItem(lift.Hero, lift.Item), new RawLift(lift.Matches, lift.Lift, lift.Se))));

    [Fact]
    public void PatchesAreWeighedByTheirNoisePlusHowFarBackTheyAre()
    {
        var newer = Lifts(("h", 1, 1500, 2.0, 1.0), ("h", 2, 1500, 1.0, 1.0));
        var older = Lifts(("h", 1, 1500, 0.0, 0.5), ("h", 3, 1000, 4.0, 1.0));

        // The older patch's se² of 0.25 plus one patch of drift, 0.75, matches the newer one's 1: equal weight.
        var combined = MatchStatsMath.CombinePatches([newer, older], 0.75, 2000, out var shares);
        Assert.Equal([new HeroItem("h", 1)], combined.Keys);
        Assert.Equal(1.0, combined[new HeroItem("h", 1)].Lift, 9);
        Assert.Equal(1 / Math.Sqrt(2), combined[new HeroItem("h", 1)].Se, 9);
        Assert.Equal(3000, combined[new HeroItem("h", 1)].Matches);
        Assert.Equal([0.5, 0.5], shares);

        // Without drift the older, less noisy lift counts four times as much.
        Assert.Equal(0.4, MatchStatsMath.CombinePatches([newer, older], 0, 2000, out shares)[new HeroItem("h", 1)].Lift, 9);
        Assert.Equal([0.2, 0.8], shares.Select(share => Math.Round(share, 9)));

        // A patch with nothing over the range adds nothing. A lift from one patch back alone keeps its
        // number, but says less about the current patch: its se takes the drift too.
        var alone = MatchStatsMath.CombinePatches([null, older], 0.75, 1000, out shares);
        var lone = alone[new HeroItem("h", 3)];
        Assert.Equal((1000, 4.0), (lone.Matches, lone.Lift));
        Assert.Equal(Math.Sqrt(1.75), lone.Se, 9);
        Assert.Equal([0.0, 1.0], shares);
    }

    [Fact]
    public void ALiftWithNoNoiseOutweighsTheRest()
    {
        var exact = MatchStatsMath.CombinePatches([Lifts(("h", 1, 3000, 2.0, 0.0)), Lifts(("h", 1, 3000, 9.0, 1.0))], 0, 2000, out _);

        Assert.Equal(new RawLift(6000, 2.0, 0.0), exact[new HeroItem("h", 1)]);
    }

    [Fact]
    public void DriftIsHowFarTheSameLiftsMoveBetweenPatchesBeyondTheirNoise()
    {
        // 100 lifts, each 1 point apart between the patches either way, with se 0.5 in both: 1 - 0.5 = 0.5.
        var pairs = Enumerable.Range(0, 100).ToList();
        var newer = Lifts([.. pairs.Select(i => ("h", (long)i, 3000, 0.0, 0.5))]);
        var older = Lifts([.. pairs.Select(i => ("h", (long)i, 3000, i % 2 == 0 ? 1.0 : -1.0, 0.5))]);

        Assert.Equal(0.5, MatchStatsMath.EstimateDrift2(newer, older)!.Value, 9);
        Assert.Null(MatchStatsMath.EstimateDrift2(newer, Lifts([.. pairs.Skip(1).Select(i => ("h", (long)i, 3000, 0.0, 0.5))])));
        // No more movement than the noise explains: no drift.
        Assert.Equal(0.0, MatchStatsMath.EstimateDrift2(newer, newer));
    }

    [Fact]
    public void BuildRatiosCountEachPatchByItsShareOfTheLifts()
    {
        // Everyone splits tier 1 evenly. In the old patch the hero puts a tenth of its tier-1 buys into
        // item 1; in the new patch, half.
        Halves Bought(long one, long two) => new(Totals((1, one / 2, one), (2, two / 2, two)), []);
        MatchSegment PatchOf(Patch patch, long one, long two) =>
            Segment(new SliceCounts(Bought(1000, 1000), new() { ["h"] = Bought(one, two) }, [])) with { Patch = patch };
        var older = PatchOf(_patch, 100, 900);
        var newer = PatchOf(new Patch("09-29-2026", 1790726400), 500, 500);

        var weighed = MatchStatsMath.BuildRatios([newer, older], _items, new Dictionary<long, double> { [newer.Patch.Start] = 0.8, [older.Patch.Start] = 0.2 });
        Assert.Equal((0.8 * 0.5 + 0.2 * 0.1) / 0.5, weighed[("one", "h")], 9);

        // Without shares, by purchases: as if the two were one patch, 600 of 2000.
        Assert.Equal(0.3 / 0.5, MatchStatsMath.BuildRatios([newer, older], _items)[("one", "h")], 9);
    }

    private static FamilyStats Family(params (string Hero, long Item, int Matches, double Full, double First, double Second)[] lifts)
    {
        OrderedDictionary<HeroItem, RawLift> Of(Func<(string Hero, long Item, int Matches, double Full, double First, double Second), double> lift) =>
            new(lifts.Select(row => KeyValuePair.Create(new HeroItem(row.Hero, row.Item), new RawLift(row.Matches, lift(row), 0.1))));
        return new FamilyStats(Of(row => row.Full), (Of(row => row.First), Of(row => row.Second)), lifts.Length, 1.0, 0.5, 1.0, 1.0);
    }

    [Fact]
    public void LeaningMovesEachLiftByTheRestsShareOfAShrunkDifference()
    {
        // The range differs from the rest by ±2 on every lift, in both halves alike: real. The rest has
        // three quarters of the matches.
        var inRange = Family(("h", 1, 1000, 2, 2, 2), ("h", 2, 1000, -2, -2, -2), ("h", 3, 1000, 2, 2, 2), ("h", 4, 1000, -2, -2, -2));
        var rest = Family(("h", 1, 3000, 0, 0, 0), ("h", 2, 3000, 0, 0, 0), ("h", 3, 3000, 0, 0, 0), ("h", 4, 3000, 0, 0, 0));

        var lean = MatchStatsMath.LeanTowards(inRange, rest, 0.5);

        // σ² = 4 - (0.01 + 0.01); each difference shrinks by σ² / (σ² + 0.02), then counts at 0.75.
        Assert.Equal(3.98, lean.Sigma2, 9);
        Assert.Equal(0.75 * 2 * 3.98 / 4, lean.Shifts[new HeroItem("h", 1)], 9);
        Assert.Equal(-0.75 * 2 * 3.98 / 4, lean.Shifts[new HeroItem("h", 2)], 9);
        Assert.Equal((4, 4), (lean.Pairs, lean.Moved));
        Assert.Equal(1.0, lean.Reliability);
    }

    [Fact]
    public void ARangeWhoseLiftsSpreadWiderThanEveryMatchCountsTheExtraAsNoise()
    {
        var inRange = Family(("h", 1, 1000, 2, 2, 2), ("h", 2, 1000, -2, -2, -2), ("h", 3, 1000, 2, 2, 2), ("h", 4, 1000, -2, -2, -2))
            with { Tau2 = 3.0 };
        var rest = Family(("h", 1, 3000, 0, 0, 0), ("h", 2, 3000, 0, 0, 0), ("h", 3, 3000, 0, 0, 0), ("h", 4, 3000, 0, 0, 0));

        var lean = MatchStatsMath.LeanTowards(inRange, rest, 0.5);

        // 2.5 points² more spread than every match: each difference's noise is 0.02 + 2.5.
        Assert.Equal(4 - 2.52, lean.Sigma2, 9);
        Assert.Equal(0.75 * 2 * 1.48 / 4, lean.Shifts[new HeroItem("h", 1)], 9);
    }

    [Fact]
    public void DifferencesTheHalvesDisagreeOnMoveNothing()
    {
        var inRange = Family(("h", 1, 1000, 0, 2, -2), ("h", 2, 1000, 0, -2, 2), ("h", 3, 1000, 0, 2, -2), ("h", 4, 1000, 0, -2, 2));
        var rest = Family(("h", 1, 3000, 2, 0, 0), ("h", 2, 3000, -2, 0, 0), ("h", 3, 3000, 2, 0, 0), ("h", 4, 3000, -2, 0, 0));

        var lean = MatchStatsMath.LeanTowards(inRange, rest, 0.5);

        Assert.Equal(0.0, lean.Reliability);
        Assert.Equal(0.0, lean.Sigma2);
        Assert.All(lean.Shifts.Values, shift => Assert.Equal(0.0, shift));
        Assert.Equal(0, lean.Moved);
        Assert.Equal("Phantom+ isn't detectably different from every match, so these are every match's numbers",
            new FamilyReport("against", _patch, rest, lean).LeanText("Phantom+"));
    }
}