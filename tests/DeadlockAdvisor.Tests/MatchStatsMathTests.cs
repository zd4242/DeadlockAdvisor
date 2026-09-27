using System.Text.Json.Nodes;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Tests;

/// <summary>Ported from the Python app's tests/test_scoring.py (match-data maths).</summary>
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
    public void TheEnemyFamilyTakesOwnPurchasesFromTheAsFamilyOfTheSameWindowOnly()
    {
        var patches = MatchStatsMath.ParsePatches(["09-16-2026 Update", "08-22-2026 Update"]);
        Item[] items = [new("one", "One", "weapon", 1, GameId: 1), new("two", "Two", "weapon", 1, GameId: 2)];

        static RankedHalves Ranked(Dictionary<long, WinTotals> half) => new(new RankedTotals(half, []), new RankedTotals(half, []));
        MatchCounts Counts(Patch asSince) => new(1790296852, patches[0], [],
        [
            new(new Family("against", "full", 2), patches[1], Ranked(EveryMatchHalf), new() { ["e"] = Ranked(AgainstHalf) }),
            new(new Family("as", "full", 2), asSince, Ranked(EveryMatchHalf), new() { ["e"] = Ranked(OwnHalf) }),
        ]);

        var matched = MatchStatsMath.Analyse(Counts(patches[1]), null, items).Families[0];
        Assert.True(matched.OwnExcluded);
        Assert.Equal(0, matched.Stats.Full[new HeroItem("e", 1)].Lift, 9);
        Assert.True(matched.Meta()["own_excluded"]!.GetValue<bool>());

        // A download from before the windows matched can't be corrected, and says so.
        var older = MatchStatsMath.Analyse(Counts(patches[0]), null, items);
        Assert.False(older.Families[0].OwnExcluded);
        Assert.Equal(50.0 / 9, older.Families[0].Stats.Full[new HeroItem("e", 1)].Lift, 9);
        Assert.Null(older.Families[1].OwnExcluded);
        Assert.False(older.Families[1].Meta().ContainsKey("own_excluded"));

        var meta = JsonNode.Parse(
            """{"families": {"against/full": {"kept": true, "rows": 5, "reliability": 0.7, "own_excluded": false}}}""")!.AsObject();
        Assert.Equal(["Enemies: 5 lifts, reliability 0.70", MatchStatsMath.OwnIncludedNote], MatchStatsMath.FamilyLines(meta));
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
    public void QueryParamsPerRelationAndScope()
    {
        Assert.Equal([new("enemy_hero_ids", "1")], MatchStatsMath.QueryParams("against", "full", 1));
        Assert.Equal([new("enemy_hero_ids", "1"), new("same_lane_filter", "true"), new("max_bought_at_s", "600")],
            MatchStatsMath.QueryParams("against", "lane", 1));
        Assert.Equal([new("hero_id", "13")], MatchStatsMath.QueryParams("as", "full", 13));
        Assert.Equal([new("max_bought_at_s", "600")], MatchStatsMath.QueryParams("as", "lane")); // lane baseline
    }

    [Fact]
    public void FamilyReliabilityStepsSplitHalfUpToTheWholeWindow()
    {
        var empty = new OrderedDictionary<HeroItem, RawLift>();

        Assert.Equal(0.8 / 1.4, new FamilyStats(empty, (empty, empty), 0, 1.0, 0.1, 0.4, 0.4).Reliability!.Value, 9);
        Assert.Equal(0.0, new FamilyStats(empty, (empty, empty), 0, 1.0, 0.1, -0.2, 0.0).Reliability);
    }

    [Fact]
    public void AYoungPatchReachesOnePatchFurtherBack()
    {
        var patches = MatchStatsMath.ParsePatches(["09-16-2026 Update", "08-22-2026 Update", "08-12-2026 Update"]);
        var against = new Family("against", "full", 1);
        var weekLater = patches[0].Start + 7 * 86400;

        Assert.Equal("09-16", MatchStatsMath.WindowStart(patches, against, weekLater).Label);
        Assert.Equal("08-22", MatchStatsMath.WindowStart(patches, against, patches[0].Start + 86400).Label);
        var asFull = new Family("as", "full", 2);
        Assert.Equal("08-12", MatchStatsMath.WindowStart(patches, asFull, patches[0].Start + 86400).Label);
    }

    [Fact]
    public void RankGroupsCoverEveryBadgeOnceWithEternusInAscendant()
    {
        var ranks = MatchStatsMath.RankBuckets(new Dictionary<int, string> { [1] = "Initiate", [5] = "Mystic", [10] = "Ascendant" });

        Assert.Equal(Enumerable.Range(1, 10), ranks.Select(rank => rank.Tier));
        Assert.Equal(("Initiate", 0, 19), (ranks[0].Name, ranks[0].MinBadge, ranks[0].MaxBadge));
        Assert.Equal(("Mystic", 50, 59), (ranks[4].Name, ranks[4].MinBadge, ranks[4].MaxBadge));
        Assert.Equal(("Ascendant", 100, 116), (ranks[9].Name, ranks[9].MinBadge, ranks[9].MaxBadge));
        Assert.Equal("Rank 2", ranks[1].Name);
        Assert.All(ranks.Zip(ranks.Skip(1)), pair => Assert.Equal(pair.First.MaxBadge + 1, pair.Second.MinBadge));
        Assert.Equal([new("hero_id", "13"), new("min_average_badge", "50"), new("max_average_badge", "59")],
            MatchStatsMath.RankParams(MatchStatsMath.QueryParams("as", "full", 13), ranks[4]));
    }

    [Fact]
    public void ARankRangeAddsUpItsGroupsAndEveryMatchTakesTheUnfilteredTotals()
    {
        var ranks = MatchStatsMath.RankBuckets(new Dictionary<int, string>());
        var byRank = ranks.Select(rank => Totals((1, rank.Tier, 10 * rank.Tier))).ToList();
        byRank[2] = Totals((1, 3, 30), (2, 1, 4));
        var totals = new RankedTotals(Totals((1, 999, 2000)), byRank);

        Assert.Equal(new WinTotals(999, 2000), totals.For(ranks, null)[1]);
        var mysticUp = totals.For(ranks, new RankRange(5, 10));
        Assert.Equal(new WinTotals(5 + 6 + 7 + 8 + 9 + 10, 450), mysticUp[1]);
        Assert.False(mysticUp.ContainsKey(2));
        var third = totals.For(ranks, new RankRange(3, 3));
        Assert.Equal([new WinTotals(3, 30), new WinTotals(1, 4)], [third[1], third[2]]);
    }

    [Fact]
    public void RankRangesAreDescribedByTheirEnds()
    {
        var names = new Dictionary<int, string> { [1] = "Initiate", [5] = "Mystic", [8] = "Oracle", [10] = "Ascendant" };
        var counts = new MatchCounts(0, new Patch("", 0), MatchStatsMath.RankBuckets(names), []);

        Assert.Equal("every match", counts.Describe(null));
        Assert.Equal("every ranked match", counts.Describe(new RankRange(1, 10)));
        Assert.Equal("Mystic+", counts.Describe(new RankRange(5, 10)));
        Assert.Equal("Ascendant+", counts.Describe(new RankRange(10, 10)));
        Assert.Equal("up to Oracle", counts.Describe(new RankRange(1, 8)));
        Assert.Equal("Mystic – Oracle", counts.Describe(new RankRange(5, 8)));
        Assert.Equal("Oracle", counts.Describe(new RankRange(8, 8)));
    }

    [Fact]
    public void TheRankRangeIsReadBackFromTheMeta()
    {
        var patch = new Patch("09-16-2026 Update", 1789603200);
        var ranged = new FetchResult([], [], patch, 1, new RankRange(5, 10), "Mystic+").Meta();
        var every = new FetchResult([], [], patch, 1, null, "every match").Meta();

        Assert.Equal(new RankRange(5, 10), MatchStatsMath.RankOf(ranged));
        Assert.Equal("Mystic+", MatchStatsMath.RankLabel(ranged));
        Assert.Contains("· Mystic+ ·", MatchStatsMath.Summary(ranged, 1));
        Assert.Contains("Ranked matches only: Mystic+.", MatchStatsMath.DataNote(ranged, 1));
        Assert.Null(MatchStatsMath.RankOf(every));
        Assert.Null(MatchStatsMath.RankLabel(every));
        Assert.Equal("all", every["rank"]!.GetValue<string>());
    }
}
