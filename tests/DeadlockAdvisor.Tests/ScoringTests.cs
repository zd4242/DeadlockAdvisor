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
    public void TheTypicalBestTargetSumIsTheAverageOverEveryTeam()
    {
        double[] roster = [5, 3, 0, -2, -6];

        for (var count = 1; count <= roster.Length; count++)
        {
            var teams = Teams(roster, count).ToList();
            Assert.Equal(teams.Average(BestTargets.Sum), BestTargets.Expected(roster, count), 9);
        }
        Assert.Equal(0, BestTargets.Expected(roster, 0));
        // 5 in full, 0 at half, -6 at a quarter.
        Assert.Equal(3.5, BestTargets.Sum([0, -6, 5]));
    }

    private static IEnumerable<List<double>> Teams(IReadOnlyList<double> roster, int count, int from = 0)
    {
        if (count == 0)
        {
            yield return [];
            yield break;
        }
        for (var i = from; i <= roster.Count - count; i++)
        {
            foreach (var rest in Teams(roster, count - 1, i + 1))
                yield return [roster[i], .. rest];
        }
    }

    [Fact]
    public void ASingleTargetItemCountsItsBestTargetsAgainstATypicalTeam()
    {
        var store = TestStore.Make();
        store.Items["spirit_resist_t1"] = store.Items["spirit_resist_t1"] with { CastOn = Relation.Against };
        var matrix = ItemScoring.BuildWeightMatrix(store);
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Enemy);

        // Against weights: heavy_spirit (5 - 2) * 2 = 6, low_hp -4, generic -2. The pair counts
        // 6 + -4/2 = 4; a typical pair, (6 - 2 + 6 - 1 - 2 - 2) / 3 = 5/3. Summed it would be 6 - 4 = 2.
        Assert.Equal(5.0 / 3, matrix.Typical("spirit_resist_t1", 2), 9);
        var scored = ItemScoring.ScoreAll(store, matrix, match).Single(item => item.ItemId == "spirit_resist_t1");
        Assert.Equal(4 - 5.0 / 3, scored.Score, 9);

        var explained = ItemScoring.ExplainItem(store, match, "spirit_resist_t1");
        Assert.Equal([("heavy_spirit", 6.0, (int?)1), ("", Math.Round(-5.0 / 3, 9), null), ("low_hp", -2.0, 2)],
            explained.Select(contribution => (contribution.HeroId, Math.Round(contribution.Amount, 9), contribution.Rank)));
        Assert.Equal(2, explained[1].TypicalOf);
        Assert.Equal(scored.Score, explained.Sum(contribution => contribution.Amount), 9);

        // The whole roster is its own typical team; one enemy is just its weight, with nothing taken off.
        match.SetRole("generic", Role.Enemy);
        Assert.Equal(0, ItemScoring.Total(matrix, "spirit_resist_t1", ItemScoring.RelevantHeroes(match)), 9);
        var alone = new MatchState();
        alone.SetRole("heavy_spirit", Role.Enemy);
        Assert.Equal(6, ItemScoring.Total(matrix, "spirit_resist_t1", ItemScoring.RelevantHeroes(alone)), 9);
        Assert.Single(ItemScoring.ExplainItem(store, alone, "spirit_resist_t1"));
    }

    [Theory]
    [InlineData(Relation.Against, Role.Ally)]
    [InlineData(Relation.With, Role.Enemy)]
    public void ASingleTargetItemSumsTheTeamItIsNotCastOn(Relation castOn, Role other)
    {
        var store = TestStore.Make();
        store.Items["spirit_resist_t1"] = store.Items["spirit_resist_t1"] with { CastOn = castOn };
        store.ItemCoefficients[new CoefficientKey("spirit_resist_t1", "deals_spirit_damage_general", Relation.With)] = 1.0;
        var match = new MatchState();
        match.SetRole("heavy_spirit", other);
        match.SetRole("low_hp", other);

        // Against weights 6 and -4, with weights 3 and -2: a plain sum either way, nothing ranked or taken off.
        var expected = other == Role.Enemy ? 6 - 4 : 3 - 2;
        Assert.Equal(expected, ItemScoring.Total(ItemScoring.BuildWeightMatrix(store), "spirit_resist_t1", ItemScoring.RelevantHeroes(match)), 9);
        var explained = ItemScoring.ExplainItem(store, match, "spirit_resist_t1");
        Assert.Equal(["heavy_spirit", "low_hp"], explained.Select(contribution => contribution.HeroId));
        Assert.All(explained, contribution => Assert.Null(contribution.Rank));
    }

    [Fact]
    public void BlendScalesAreTheSpreadOfNonzeroOpinionsOverRandomLineUps()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        var scales = new ScoreScales(store, ItemScoring.BuildWeightMatrix(store));

        // One enemy, each hero a third of the time. Formula: the trinket's 6, -4, -2 and the percent item's
        // 4/3, -8/3, 4/3, so sqrt((56/3 + 96/27) / 2) = 3.33. Data: 1.5 and 1.2 against heavy_spirit and
        // -0.25 against generic, one value a draw on average, so sqrt((2.25 + 1.44 + 0.0625) / 3) = 1.12.
        var scale = scales.For(new LineUpShape(1, 0, false));
        Assert.InRange(scale.Formula, 3.0, 3.7);
        Assert.InRange(scale.Data, 1.0, 1.25);
        Assert.Equal(scale, scales.For(new LineUpShape(1, 0, false)));
        // No one to draw, or more than the roster holds: plain units.
        Assert.Equal(BlendScale.One, scales.For(new LineUpShape(0, 0, false)));
        Assert.Equal(BlendScale.One, scales.For(LineUpShape.FullMatch));
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
    public void EnemyLiftsCountLessWhenYourHeroRarelyBuildsTheItem()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        store.Items["spirit_resist_t1"] = store.Items["spirit_resist_t1"] with { GameId = 1 };
        store.Items["irrelevant_t1"] = store.Items["irrelevant_t1"] with { GameId = 2 };
        store.Items["pct_dmg_t3"] = store.Items["pct_dmg_t3"] with { GameId = 3 };

        // Everyone splits tier 1 evenly between the two items. low_hp puts 1 in 20 of its tier-1 buys
        // into the trinket, a tenth of the average share; heavy_spirit never buys it; generic buys
        // nothing in tier 1, so there's no telling.
        static RankedHalves Bought(params (long Item, long Wins, long Matches)[] rows) =>
            new(new RankedTotals(MatchStatsMath.Totals(rows), []), new RankedTotals([], []));
        var patch = new Patch("09-16-2026 Update", 0);
        store.MatchCounts = new MatchCounts(1, patch, [],
        [
            new(new Family("as", 2), patch, Bought((1, 500, 1000), (2, 500, 1000)), new()
            {
                ["low_hp"] = Bought((1, 25, 50), (2, 475, 950)),
                ["heavy_spirit"] = Bought((2, 500, 1000)),
                ["generic"] = Bought((3, 10, 20)),
            }),
        ]);

        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Enemy);
        match.SetRole("low_hp", Role.Self);

        // 0.1 of the average share, under the 0.25 bar: the enemies' 1.25 counts × 0.4. "as" is untouched.
        Assert.Equal(0.1, ItemScoring.BuildRatio(store, "spirit_resist_t1", "low_hp")!.Value, 9);
        var data = ItemScoring.DataScores(store, match, "spirit_resist_t1");
        Assert.Equal(0.5, data["against"], 9);
        Assert.Equal(3.0, data["as"]);
        var scored = ItemScoring.ScoreAll(store, ItemScoring.BuildWeightMatrix(store), match)
            .Single(item => item.ItemId == "spirit_resist_t1");
        Assert.True(scored.RarelyBuilt);
        Assert.Equal(0.5 + 3.0 / 3, scored.DataStrength, 9);

        // Never bought: the enemy lifts don't count. No purchases in the tier at all: they count in full.
        Assert.Equal(0, ItemScoring.Relevance(ItemScoring.BuildRatio(store, "spirit_resist_t1", "heavy_spirit")));
        Assert.Null(ItemScoring.BuildRatio(store, "spirit_resist_t1", "generic"));
        Assert.Equal(1, ItemScoring.Relevance(null));
        // irrelevant_t1 is built 1.9x as often as average by low_hp: in full.
        Assert.Equal(1, ItemScoring.Relevance(ItemScoring.BuildRatio(store, "irrelevant_t1", "low_hp")));
    }

    [Fact]
    public void DataOnlyPicksListWhatTheHandModelSkips()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);

        var picks = ItemScoring.DataOnlyPicks(store, ItemScoring.BuildWeightMatrix(store), match);

        // irrelevant_t1 has no rules (hand score 0) but +1.2 against, so it's listed; spirit_resist_t1
        // is already recommended by the hand model, so it isn't.
        Assert.Equal(["irrelevant_t1"], picks.Select(p => p.ItemId));
        Assert.Equal([new("against", 1.2)], picks[0].Data);
    }

    [Fact]
    public void DataStrengthNetsTheRelationsInBars()
    {
        Assert.Equal(1.0, ItemScoring.DataStrength(new() { ["against"] = 0.5, ["as"] = 1.5 })); // 0.5 / 1 + 1.5 / 3
        Assert.Equal(-0.5, ItemScoring.DataStrength(new() { ["against"] = 1.5, ["as"] = -6.0 }));
        Assert.Equal(0.0, ItemScoring.DataStrength([]));
    }

    [Fact]
    public void AStrongCounterYourHeroDoesBadlyWithIsNoDataPick()
    {
        var store = TestStore.Make();
        TestStore.AddLifts(store);
        store.MatchLift[new MatchLiftKey("irrelevant_t1", "low_hp", "as")] =
            new MatchLift("irrelevant_t1", "low_hp", "as", 5000, -6.1, 0.5, -6.0);
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("low_hp", Role.Self);

        // enemies +1.2 clears its bar alone, but -6.0 on you nets it to 1.2 - 2 = -0.8.
        Assert.Empty(ItemScoring.DataOnlyPicks(store, ItemScoring.BuildWeightMatrix(store), match));
    }

    [Fact]
    public void ScoreAllListsEveryItemWhateverItScores()
    {
        var store = TestStore.Make();
        var matrix = ItemScoring.BuildWeightMatrix(store);
        var match = new MatchState();
        match.SetRole("low_hp", Role.Enemy);

        var all = ItemScoring.ScoreAll(store, matrix, match);

        // 0, then (-4 + 4/3) * 1, then (0 - 2) * 2.
        Assert.Equal(["irrelevant_t1", "pct_dmg_t3", "spirit_resist_t1"], all.Select(item => item.ItemId));
        AssertEx.Close(-8.0 / 3, all[1].Score);
        Assert.Equal(-4.0, all[2].Score);
        Assert.Empty(ItemScoring.FullMatchResults(store, matrix, match));
    }
}
