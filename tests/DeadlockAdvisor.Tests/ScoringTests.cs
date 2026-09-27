using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// Ported from the Python app's tests/test_scoring.py (scoring and match-data scores), then moved to
/// scores measured from the roster average. In <see cref="TestStore"/> the three heroes average 2 on
/// spirit damage (5, 0, 1) and -4/3 on max HP (0, -4, 0).
/// </summary>
public class ScoringTests
{
    private static MatrixKey Key(string item, string hero, Relation relation) => new(item, hero, relation);

    [Fact]
    public void WeightMatrixBasic()
    {
        var matrix = ItemScoring.BuildWeightMatrix(TestStore.Make());

        Assert.Equal(6.0, matrix[Key("spirit_resist_t1", "heavy_spirit", Relation.Against)]); // (5 - 2) * 2
        Assert.Equal(-2.0, matrix[Key("spirit_resist_t1", "generic", Relation.Against)]); // (1 - 2) * 2
        Assert.Equal(-4.0, matrix[Key("spirit_resist_t1", "low_hp", Relation.Against)]); // (0 - 2) * 2
        AssertEx.Close(-8.0 / 3, matrix[Key("pct_dmg_t3", "low_hp", Relation.Against)]); // (-4 + 4/3) * 1: lower HP than most discourages it
        Assert.False(matrix.ContainsKey(Key("irrelevant_t1", "heavy_spirit", Relation.Against)));
    }

    [Fact]
    public void AverageHeroesLeaveNoMark()
    {
        var store = TestStore.Make();
        // (5 + 0 + 2.5) / 3 = 2.5: generic now sits exactly on the spirit average.
        store.HeroScores[new ScoreKey("generic", "deals_spirit_damage_general")] = 2.5;
        // All zeros: left out of the average, and scores nothing rather than "below average at everything".
        store.Heroes["unprofiled"] = new Hero("unprofiled", "Unprofiled");

        var matrix = ItemScoring.BuildWeightMatrix(store);

        Assert.Equal(2.5, store.TraitBaselines()["deals_spirit_damage_general"]);
        Assert.False(matrix.ContainsKey(Key("spirit_resist_t1", "generic", Relation.Against)));
        Assert.False(matrix.ContainsKey(Key("spirit_resist_t1", "unprofiled", Relation.Against)));
        var match = new MatchState();
        match.SetRole("generic", Role.Enemy);
        match.SetRole("unprofiled", Role.Enemy);
        Assert.Empty(ItemScoring.ExplainItem(store, match, "spirit_resist_t1"));
    }

    [Fact]
    public void TheWholeRosterAsEnemiesScoresNothing()
    {
        var store = TestStore.Make();
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);

        // An average line-up wants no item more than usual: every total is 0, and nothing is listed.
        Assert.Empty(ItemScoring.FullMatchResults(store, ItemScoring.BuildWeightMatrix(store), match));
    }

    [Fact]
    public void FullMatchFiltersAndSorts()
    {
        var store = TestStore.Make();
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);

        var results = ItemScoring.FullMatchResults(store, ItemScoring.BuildWeightMatrix(store), match);

        // pct_dmg_t3 sums to 4/3 - 8/3 = -4/3 over these enemies, so it's filtered out entirely.
        var tier1 = results[1];
        Assert.Equal(["spirit_resist_t1"], tier1.Select(s => s.ItemId));
        Assert.Equal(2.0, tier1[0].Score); // 6 - 4
        Assert.Equal("vitality", tier1[0].ShopCategory);
        Assert.False(results.ContainsKey(3));
    }

    [Fact]
    public void LanePhaseRestrictsHeroesAndTiers()
    {
        var store = TestStore.Make();
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Enemy);
        match.SetLane("heavy_spirit", true);

        var results = ItemScoring.LanePhaseResults(store, ItemScoring.BuildWeightMatrix(store), match);

        Assert.Equal(["spirit_resist_t1"], results[1].Select(s => s.ItemId));
        Assert.Equal(6.0, results[1][0].Score); // only heavy_spirit counted, not generic
        Assert.False(results.ContainsKey(3)); // tier 3 never appears in the lane view
    }

    [Fact]
    public void ResultsAreSortedDescendingWithinTier()
    {
        var store = TestStore.Make();
        store.Items["weak_t1"] = new Item("weak_t1", "Weak Item", "vitality", 1);
        store.ItemCoefficients[new CoefficientKey("weak_t1", "deals_spirit_damage_general", Relation.Against)] = 0.1;
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);

        var results = ItemScoring.FullMatchResults(store, ItemScoring.BuildWeightMatrix(store), match);

        Assert.Equal(["spirit_resist_t1", "weak_t1"], results[1].Select(s => s.ItemId));
    }

    [Fact]
    public void ExplainItemBreaksScoreDownByHeroAndTrait()
    {
        var store = TestStore.Make();
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Enemy);

        var contributions = ItemScoring.ExplainItem(store, match, "spirit_resist_t1");

        Assert.Equal(["heavy_spirit", "generic"], contributions.Select(c => c.HeroId));
        Assert.Equal(6.0, contributions[0].Amount);
        Assert.Equal(Relation.Against, contributions[0].Relation);
        var part = contributions[0].Parts[0];
        Assert.Equal("Deals Spirit Damage", part.CategoryName);
        Assert.Equal((5.0, 2.0, 3.0), (part.HeroScore, part.Baseline, part.Deviation));
        Assert.Equal(2.0, part.Coefficient);
        Assert.Equal(contributions[0].Amount, contributions[0].Parts.Sum(p => p.Amount));
        Assert.Equal(-2.0, contributions[1].Amount); // generic is below average on spirit damage
        Assert.Equal(4.0, contributions.Sum(c => c.Amount));
    }

    [Fact]
    public void ExplainRespectsLaneRestriction()
    {
        var store = TestStore.Make();
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Enemy);
        match.SetLane("heavy_spirit", true);

        var contributions = ItemScoring.ExplainItem(store, match, "spirit_resist_t1", match.LaneHeroes);

        Assert.Equal(["heavy_spirit"], contributions.Select(c => c.HeroId));
    }

    [Theory]
    [InlineData(15_000, 10_000, 1.25)] // 1 + 0.5 × (1.5 − 1)
    [InlineData(5_000, 10_000, 0.75)]
    [InlineData(30_000, 10_000, 1.3)] // capped at ±30%
    [InlineData(0, 10_000, 0.7)]
    [InlineData(400, 300, 1.0)] // still on starting souls: no lean at all
    public void TheNetWorthFactorLeansTowardWhoeverIsAheadWithinLimits(int souls, double average, double factor) =>
        AssertEx.Close(factor, NetWorthWeights.FactorFor(souls, average));

    [Fact]
    public void EachHeroIsMeasuredAgainstTheirOwnReading()
    {
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);
        match.SetRole("generic", Role.Ally);
        var at = new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
        match.NetWorth.Add(new NetWorthSnapshot(at, new Dictionary<string, int> { ["heavy_spirit"] = 3_000, ["low_hp"] = 1_000, ["generic"] = 2_000 }));
        // The ally side didn't add up this time; someone no longer in the match is ignored.
        match.NetWorth.Add(new NetWorthSnapshot(at.AddMinutes(2), new Dictionary<string, int> { ["heavy_spirit"] = 6_000, ["low_hp"] = 2_000, ["gone"] = 50_000 }));

        var weights = NetWorthWeights.For(match);

        Assert.Equal(new NetWorthStanding(6_000, 4_000, 1.25), weights.StandingOf("heavy_spirit"));
        Assert.Equal(new NetWorthStanding(2_000, 4_000, 0.75), weights.StandingOf("low_hp"));
        // Against the 2,000 average of its own, older reading, not the newer 4,000.
        Assert.Equal(new NetWorthStanding(2_000, 2_000, 1.0), weights.StandingOf("generic"));
        Assert.Null(weights.StandingOf("gone"));
        Assert.Equal(1.0, NetWorthWeights.None.Factor("heavy_spirit"));
    }

    [Fact]
    public void NetWorthScalesEachHerosWholeShare()
    {
        var store = TestStore.Make();
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);
        match.NetWorth.Add(new NetWorthSnapshot(DateTimeOffset.UnixEpoch,
            new Dictionary<string, int> { ["heavy_spirit"] = 15_000, ["low_hp"] = 5_000 }));
        var matrix = ItemScoring.BuildWeightMatrix(store);
        var weights = NetWorthWeights.For(match);

        // A fed heavy_spirit makes Spirit Resist more wanted: 6 × 1.25 − 4 × 0.75, against 6 − 4 without.
        Assert.Equal(4.5, ItemScoring.FullMatchResults(store, matrix, match, weights)[1][0].Score);
        Assert.Equal(2.0, ItemScoring.FullMatchResults(store, matrix, match)[1][0].Score);

        var contributions = ItemScoring.ExplainItem(store, match, "spirit_resist_t1", netWorth: weights);
        Assert.Equal([(7.5, 1.25), (-3.0, 0.75)], contributions.Select(c => (c.Amount, c.Factor)));
        // The trait lines stay unweighted; the factor applies to the hero's sum.
        Assert.Equal(6.0, contributions[0].Parts.Sum(p => p.Amount));
        Assert.Equal(15_000, contributions[0].NetWorth!.Souls);
    }

    [Fact]
    public void TopHeroesForItemRanksByWeight()
    {
        var store = TestStore.Make();

        var top = ItemScoring.TopHeroesForItem(store, ItemScoring.BuildWeightMatrix(store), "spirit_resist_t1", Relation.Against);

        Assert.Equal([("Heavy Spirit", 6.0), ("Generic", -2.0), ("Low HP", -4.0)], top);
    }

    [Fact]
    public void TraitWeightScalesMatrixAndExplain()
    {
        var store = TestStore.Make();
        Assert.Equal(1.0, store.TraitWeight("deals_spirit_damage_general", Relation.Against));
        Assert.True(store.SetTraitWeight("deals_spirit_damage_general", Relation.Against, 0.5));
        Assert.False(store.SetTraitWeight("deals_spirit_damage_general", Relation.Against, 0.5));

        Assert.Equal(3.0, ItemScoring.BuildWeightMatrix(store)[Key("spirit_resist_t1", "heavy_spirit", Relation.Against)]);
        // a different relation on the same trait is untouched
        Assert.Equal(1.0, store.TraitWeight("deals_spirit_damage_general", Relation.With));

        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        var part = ItemScoring.ExplainItem(store, match, "spirit_resist_t1")[0].Parts[0];
        Assert.Equal(2.0, part.Coefficient); // the hand-typed value, unscaled
        Assert.Equal(0.5, part.Weight);
        Assert.Equal(3.0, part.Amount);

        // back to 1 drops the row rather than storing a no-op
        store.SetTraitWeight("deals_spirit_damage_general", Relation.Against, 1.0);
        Assert.Empty(store.TraitWeights);
    }

    [Fact]
    public void TraitWeightZeroSilencesTheTrait()
    {
        var store = TestStore.Make();
        store.SetTraitWeight("deals_spirit_damage_general", Relation.Against, 0);

        Assert.False(ItemScoring.BuildWeightMatrix(store).ContainsKey(Key("spirit_resist_t1", "heavy_spirit", Relation.Against)));
    }

    [Fact]
    public void StatRuleDerivesACoefficientFromTheStat()
    {
        var store = TestStore.WithStats(TestStore.Make());

        // 20% * 0.25 = 5, no hand-typed rule needed
        Assert.Equal(5.0, store.DerivedCoefficient("irrelevant_t1", "deals_spirit_damage_general", Relation.Against));
        Assert.Equal(5.0, store.EffectiveCoefficient("irrelevant_t1", "deals_spirit_damage_general", Relation.Against));
        Assert.Equal(15.0, ItemScoring.BuildWeightMatrix(store)[Key("irrelevant_t1", "heavy_spirit", Relation.Against)]); // (5 - 2) * 5
        // nothing on a relation the rule doesn't name
        Assert.Equal(0, store.DerivedCoefficient("irrelevant_t1", "deals_spirit_damage_general", Relation.With));
    }

    [Fact]
    public void StatsAddToTheManualCoefficientAndWeightScalesBoth()
    {
        var store = TestStore.WithStats(TestStore.Make());
        // manual 2 + stats 2.5 = 4.5
        Assert.Equal(4.5, store.EffectiveCoefficient("spirit_resist_t1", "deals_spirit_damage_general", Relation.Against));
        store.SetTraitWeight("deals_spirit_damage_general", Relation.Against, 2.0);
        Assert.Equal(9.0, store.EffectiveCoefficient("spirit_resist_t1", "deals_spirit_damage_general", Relation.Against));

        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        var part = ItemScoring.ExplainItem(store, match, "spirit_resist_t1")[0].Parts[0];
        Assert.Equal(2.0, part.Coefficient);
        Assert.Equal(2.5, part.FromStats);
        Assert.Equal(27.0, part.Amount); // (5 - 2) * (2 + 2.5) * 2
        Assert.Equal(27.0, ItemScoring.BuildWeightMatrix(store)[Key("spirit_resist_t1", "heavy_spirit", Relation.Against)]);
    }

    [Fact]
    public void DataScoresKeepEnemiesAndSelfApart()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Enemy);
        match.SetRole("low_hp", Role.Self);

        Assert.Equal([new("against", 1.25), new("as", 3.0)], ItemScoring.DataScores(store, match, "spirit_resist_t1"));
        Assert.Empty(ItemScoring.DataScores(store, match, "pct_dmg_t3")); // no rows -> nothing to show
        var results = ItemScoring.FullMatchResults(store, ItemScoring.BuildWeightMatrix(store), match);
        Assert.Equal([new("against", 1.25), new("as", 3.0)], results[1][0].Data);
    }

    [Fact]
    public void LaneDataUsesTheLaneScopeAndOnlyLaneHeroes()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Self);

        // heavy_spirit isn't flagged in-lane, and "against" has no lane rows anyway.
        Assert.Equal([new("as", 2.0)], ItemScoring.DataScores(store, match, "spirit_resist_t1", match.LaneHeroes));
    }

    [Fact]
    public void DataOnlyPicksListWhatTheHandModelSkips()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);

        var picks = ItemScoring.DataOnlyPicks(store, ItemScoring.BuildWeightMatrix(store), match, ItemScoring.FullTiers);

        // irrelevant_t1 has no rules (hand score 0) but +1.2 against, so it's listed; spirit_resist_t1
        // is already recommended by the hand model, so it isn't.
        Assert.Equal(["irrelevant_t1"], picks.Select(p => p.ItemId));
        Assert.Equal([new("against", 1.2)], picks[0].Data);
    }
}
