using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>Ported from the Python app's tests/test_scoring.py (scoring and match-data scores).</summary>
public class ScoringTests
{
    private static MatrixKey Key(string item, string hero, Relation relation) => new(item, hero, relation);

    [Fact]
    public void WeightMatrixBasic()
    {
        var matrix = ItemScoring.BuildWeightMatrix(TestStore.Make());

        Assert.Equal(10.0, matrix[Key("spirit_resist_t1", "heavy_spirit", Relation.Against)]); // 5 * 2
        Assert.Equal(2.0, matrix[Key("spirit_resist_t1", "generic", Relation.Against)]); // 1 * 2
        Assert.False(matrix.ContainsKey(Key("spirit_resist_t1", "low_hp", Relation.Against))); // score 0 -> not stored
        Assert.Equal(-4.0, matrix[Key("pct_dmg_t3", "low_hp", Relation.Against)]); // negative HP score discourages it
        Assert.False(matrix.ContainsKey(Key("irrelevant_t1", "heavy_spirit", Relation.Against)));
    }

    [Fact]
    public void FullMatchFiltersAndSorts()
    {
        var store = TestStore.Make();
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);

        var results = ItemScoring.FullMatchResults(store, ItemScoring.BuildWeightMatrix(store), match);

        // pct_dmg_t3 summed across all three enemies is -4, so it's filtered out entirely.
        var tier1 = results[1];
        Assert.Equal(["spirit_resist_t1"], tier1.Select(s => s.ItemId));
        Assert.Equal(12.0, tier1[0].Score);
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
        Assert.Equal(10.0, results[1][0].Score); // only heavy_spirit counted, not generic
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
        Assert.Equal(10.0, contributions[0].Amount);
        Assert.Equal(Relation.Against, contributions[0].Relation);
        Assert.Equal("Deals Spirit Damage", contributions[0].Parts[0].CategoryName);
        Assert.Equal(5, contributions[0].Parts[0].HeroScore);
        Assert.Equal(2.0, contributions[0].Parts[0].Coefficient);
        Assert.Equal(contributions[0].Amount, contributions[0].Parts.Sum(p => p.Amount));
        Assert.Equal(12.0, contributions.Sum(c => c.Amount));
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

    [Fact]
    public void TopHeroesForItemRanksByWeight()
    {
        var store = TestStore.Make();

        var top = ItemScoring.TopHeroesForItem(store, ItemScoring.BuildWeightMatrix(store), "spirit_resist_t1", Relation.Against);

        Assert.Equal([("Heavy Spirit", 10.0), ("Generic", 2.0)], top);
    }

    [Fact]
    public void TraitWeightScalesMatrixAndExplain()
    {
        var store = TestStore.Make();
        Assert.Equal(1.0, store.TraitWeight("deals_spirit_damage_general", Relation.Against));
        Assert.True(store.SetTraitWeight("deals_spirit_damage_general", Relation.Against, 0.5));
        Assert.False(store.SetTraitWeight("deals_spirit_damage_general", Relation.Against, 0.5));

        Assert.Equal(5.0, ItemScoring.BuildWeightMatrix(store)[Key("spirit_resist_t1", "heavy_spirit", Relation.Against)]);
        // a different relation on the same trait is untouched
        Assert.Equal(1.0, store.TraitWeight("deals_spirit_damage_general", Relation.With));

        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        var part = ItemScoring.ExplainItem(store, match, "spirit_resist_t1")[0].Parts[0];
        Assert.Equal(2.0, part.Coefficient); // the hand-typed value, unscaled
        Assert.Equal(0.5, part.Weight);
        Assert.Equal(5.0, part.Amount);

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
        Assert.Equal(25.0, ItemScoring.BuildWeightMatrix(store)[Key("irrelevant_t1", "heavy_spirit", Relation.Against)]);
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
        Assert.Equal(45.0, part.Amount); // 5 * (2 + 2.5) * 2
        Assert.Equal(45.0, ItemScoring.BuildWeightMatrix(store)[Key("spirit_resist_t1", "heavy_spirit", Relation.Against)]);
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
