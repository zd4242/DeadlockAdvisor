using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Tests;

public class ModelHealthTests
{
    /// <summary>
    /// Twelve heroes rated 10..120 on one trait; "Countered" responds to it against enemies while the
    /// data says the opposite, and "Untagged" has no rules but a standout lift against hero 5.
    /// </summary>
    private static DataStore RosterStore()
    {
        var store = new DataStore(".")
        {
            Items = new()
            {
                ["countered"] = new Item("countered", "Countered", "weapon", 1),
                ["untagged"] = new Item("untagged", "Untagged", "weapon", 1),
                ["ignored"] = new Item("ignored", "Ignored", "weapon", 2),
            },
            Categories = new()
            {
                ["trait"] = new Category("trait", "Trait", 0, 200, ""),
                ["unscored"] = new Category("unscored", "Unscored", 0, 100, ""),
            },
            ItemCoefficients = new()
            {
                [new CoefficientKey("countered", "trait", Relation.Against)] = 1.0,
                [new CoefficientKey("ignored", "unscored", Relation.As)] = 2.0,
            },
        };
        for (var i = 1; i <= 12; i++)
        {
            var heroId = $"h{i}";
            store.Heroes[heroId] = new Hero(heroId, $"Hero {i}");
            store.HeroScores[new ScoreKey(heroId, "trait")] = 10 * i;
            store.HeroScores[new ScoreKey(heroId, "unscored")] = 0;
            store.MatchLift[new MatchLiftKey("countered", heroId, "against")] =
                new MatchLift("countered", heroId, "against", 5000, -0.1 * i, 0.1, -0.1 * i);
        }
        store.MatchLift[new MatchLiftKey("untagged", "h5", "against")] =
            new MatchLift("untagged", "h5", "against", 5000, 2.1, 0.2, 2.0);
        return store;
    }

    private static ModelHealthReport Build(DataStore store) => ModelHealth.Build(store, ItemScoring.BuildWeightMatrix(store));

    [Fact]
    public void NeverShownItemsAreGroupedByWhy()
    {
        var report = Build(RosterStore());

        Assert.Equal(ModelHealth.SimulatedMatches, report.Matches);
        Assert.Equal(["Untagged"], report.NoRules);
        Assert.Equal(["Ignored"], report.EmptyTraitsOnly);
        Assert.Empty(report.NeverPositive);
        Assert.Equal(["Unscored"], report.EmptyTraits);
    }

    [Fact]
    public void SharesAreDeterministicAndConsistent()
    {
        var store = RosterStore();
        var first = Build(store);
        var second = Build(store);

        Assert.Equal(first.Shares, second.Shares);
        var countered = first.Shares.Single(share => share.ItemId == "countered");
        // The only tier-1 item that can score, so whenever it shows it's in the top three.
        Assert.Equal(countered.ShownShare, countered.TopShare);
        Assert.InRange(countered.TopShare, 0.0, 1.0);
    }

    [Fact]
    public void TheBiggestSwingsListItemsByHowFarTheirScoresStray()
    {
        var report = Build(RosterStore());

        // Six of the twelve heroes' 10i - 65 as enemies: a spread of sqrt(6 · 1191.7 · 6/11) ≈ 62 around 0.
        // The others never score, so they don't swing at all.
        var swing = Assert.Single(report.BiggestSwings);
        Assert.Equal("countered", swing.ItemId);
        Assert.InRange(swing.Swing, 55, 70);
        Assert.Contains(report.Lines(), line => line.StartsWith("  T1 Countered: ±", StringComparison.Ordinal));
    }

    [Fact]
    public void DataThatRunsAgainstTheWeightsIsFlagged()
    {
        var report = Build(RosterStore());

        var disagreement = Assert.Single(report.Disagreements);
        Assert.Equal(("Countered", Relation.Against, 12), (disagreement.ItemName, disagreement.Relation, disagreement.Heroes));
        AssertEx.Close(-1.0, disagreement.R);

        var pair = Assert.Single(report.DataOnly);
        Assert.Equal(("Untagged", "Hero 5", Relation.Against), (pair.ItemName, pair.HeroName, pair.Relation));
        Assert.Equal(2.0, pair.Lift);
    }

    [Fact]
    public void WeightsRunningAgainstTheLiftsGiveNoScaleForCoefficients()
    {
        var report = Build(RosterStore());

        // Countered's one item with lifts falls 0.1 a hero as its weight rises; Untagged's single lift has nothing to vary against.
        var against = report.Agreements.Single(agreement => agreement.Relation == Relation.Against);
        Assert.Equal(12, against.Pairs);
        AssertEx.Close(-1.0, against.R);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal(("Countered", "Trait", 1.0), (suggestion.ItemName, suggestion.TraitName, suggestion.Hand));
        AssertEx.Close(-0.01, suggestion.Slope);
        Assert.Null(suggestion.Coefficient);
    }

    /// <summary>
    /// "Agree" responds to the trait and its lifts rise 0.01 a trait point; "Missing" has no rule but rises
    /// 0.02. With a point of hand weight worth 0.01 lift points, Missing's slope is a coefficient of 2.
    /// </summary>
    [Fact]
    public void ALiftTheHandModelLacksIsSuggestedAsACoefficient()
    {
        var store = RosterStore();
        store.Items["agree"] = new Item("agree", "Agree", "weapon", 1);
        store.Items["missing"] = new Item("missing", "Missing", "weapon", 1);
        store.ItemCoefficients[new CoefficientKey("agree", "trait", Relation.Against)] = 1.0;
        store.MatchLift.Clear();
        for (var i = 1; i <= 12; i++)
        {
            var heroId = $"h{i}";
            var deviation = 10 * i - 65;
            var noise = i % 2 == 0 ? 0.05 : -0.05;
            store.MatchLift[new MatchLiftKey("agree", heroId, "against")] =
                new MatchLift("agree", heroId, "against", 5000, 0.01 * deviation + noise, 0.1, 0.01 * deviation + noise);
            store.MatchLift[new MatchLiftKey("missing", heroId, "against")] =
                new MatchLift("missing", heroId, "against", 5000, 0.02 * deviation + noise, 0.1, 0.02 * deviation + noise);
        }

        var report = Build(store);

        // Missing's lifts vary four times as much where the hand model sees nothing, which holds r near 1/sqrt(5).
        Assert.InRange(report.Agreements.Single(agreement => agreement.Relation == Relation.Against).R, 0.4, 0.5);
        var suggestion = Assert.Single(report.Suggestions);
        Assert.Equal(("Missing", "Trait", 0.0, 12), (suggestion.ItemName, suggestion.TraitName, suggestion.Hand, suggestion.Heroes));
        Assert.Equal(2.0, suggestion.Coefficient!.Value, 1);
        Assert.True(suggestion.T > ModelHealth.SuggestT);
        Assert.Contains(report.Lines(), line => line.StartsWith("  Missing · Trait: +0.20 pts per 10 points above average", StringComparison.Ordinal));
    }

    [Fact]
    public void TooFewProfiledHeroesSkipTheSimulation()
    {
        var report = Build(TestStore.Make());

        Assert.Equal(0, report.Matches);
        Assert.Empty(report.Shares);
        Assert.False(report.HasMatchData);
        Assert.Contains("Only 3 hero(es) are profiled; simulating a match needs 12.", report.Lines());
    }

    /// <summary>
    /// Focus moves weight between the enemies and measures a single-target item against teams focused the same
    /// way, so over random matches every item still averages about 0. Measuring it against unfocused teams
    /// lifted the single-target items by about a tenth of their spread, and up to 0.15.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FocusLeavesEveryItemAveragingAbout0(int focused)
    {
        const int matches = 4000;
        var store = Golden.LoadStore();
        var matrix = ItemScoring.BuildWeightMatrix(store);
        var pool = store.Heroes.Keys.Where(store.IsProfiled).ToArray();
        var items = store.Items.Values.Where(item => ItemScoring.Tiers.Contains(item.Tier)).Select(item => item.ItemId).ToList();
        var (sums, squares) = (new double[items.Count], new double[items.Count]);
        var random = new Random(1);
        var shape = LineUpShape.FullMatch with { Focused = focused };
        for (var match = 0; match < matches; match++)
        {
            var lineUp = shape.Draw(random, pool);
            for (var i = 0; i < items.Count; i++)
            {
                var score = ItemScoring.Total(matrix, items[i], lineUp);
                sums[i] += score;
                squares[i] += score * score;
            }
        }

        // The mean in units of the item's spread; random noise alone keeps the largest under about 0.05.
        var drift = Enumerable.Range(0, items.Count)
            .Where(i => squares[i] > 0)
            .Select(i => (Item: items[i], Mean: sums[i] / matches / Math.Sqrt(squares[i] / matches)))
            .ToList();
        Assert.All(drift, item => Assert.InRange(item.Mean, -0.06, 0.06));
        var ranked = drift.Where(item => store.Items[item.Item].CastOn is not null).Select(item => item.Mean).ToList();
        Assert.NotEmpty(ranked);
        Assert.InRange(ranked.Average(), -0.02, 0.02);
    }

    /// <summary>
    /// What the team already has takes a share off an item's effect, never adds one: whatever its allies hold, over random matches
    /// every item still averages about 0, single-target ones included (their typical sum is scaled with the weights).
    /// </summary>
    [Fact]
    public void AnEffectTheTeamHasLeavesEveryItemAveragingAbout0()
    {
        const int matches = 4000;
        var store = Golden.LoadStore();
        var matrix = ItemScoring.BuildWeightMatrix(store);
        var pool = store.Heroes.Keys.Where(store.IsProfiled).ToArray();
        var items = store.Items.Values.Where(item => ItemScoring.Tiers.Contains(item.Tier)).Select(item => item.ItemId).ToList();
        var shared = store.ItemEffects.Keys.Where(store.Items.ContainsKey).ToList();
        var (sums, squares) = (new double[items.Count], new double[items.Count]);
        var (random, holding) = (new Random(1), new Random(2));
        var covered = 0;
        for (var match = 0; match < matches; match++)
        {
            var drawn = LineUpShape.FullMatch.Draw(random, pool);
            var held = Enumerable.Range(0, 1 + holding.Next(4))
                .Select(_ => new HeldItem(drawn.Allies[holding.Next(drawn.Allies.Count)], shared[holding.Next(shared.Count)]));
            var lineUp = drawn with { Overlap = TeamOverlap.Of(store, held) };
            for (var i = 0; i < items.Count; i++)
            {
                var score = ItemScoring.Total(matrix, items[i], lineUp);
                covered += lineUp.Overlap.Coverage(items[i]) > 0 ? 1 : 0;
                sums[i] += score;
                squares[i] += score * score;
            }
        }

        Assert.True(covered > matches, "The held items should have covered something in most matches.");
        var drift = Enumerable.Range(0, items.Count)
            .Where(i => squares[i] > 0)
            .Select(i => (Item: items[i], Mean: sums[i] / matches / Math.Sqrt(squares[i] / matches)))
            .ToList();
        Assert.All(drift, item => Assert.InRange(item.Mean, -0.06, 0.06));
        var ranked = drift.Where(item => store.Items[item.Item].CastOn is not null).Select(item => item.Mean).ToList();
        Assert.NotEmpty(ranked);
        Assert.InRange(ranked.Average(), -0.02, 0.02);
    }

    /// <summary>
    /// A build moves a hero's damage mix by how far it is from their usual one, and an inventory drawn from the usual build is as likely
    /// to lean one way as the other, so over random matches every item still averages about 0 and the shifts stay bounded.
    /// </summary>
    [Fact]
    public void BuildsDrawnFromTheUsualOnesLeaveEveryItemAveragingAbout0()
    {
        const int matches = 4000;
        var store = Golden.LoadStore();
        var matrix = ItemScoring.BuildWeightMatrix(store);
        var pool = store.Heroes.Keys.Where(store.IsProfiled).ToArray();
        var items = store.Items.Values.Where(item => ItemScoring.Tiers.Contains(item.Tier)).Select(item => item.ItemId).ToList();
        var costs = BuildShifts.MedianCosts(store);
        var usual = pool.ToDictionary(heroId => heroId, heroId =>
        {
            var weights = new Random(heroId.Sum(letter => letter) * 31 + 7);
            var raw = Enum.GetValues<ShopCategory>().ToDictionary(category => category, _ => 0.2 + weights.NextDouble());
            return (IReadOnlyDictionary<ShopCategory, double>)raw.ToDictionary(pair => pair.Key, pair => pair.Value / raw.Values.Sum());
        });
        var (sums, squares, plain) = (new double[items.Count], new double[items.Count], new double[items.Count]);
        var (random, building) = (new Random(1), new Random(2));
        var (moved, net) = (0.0, 0.0);
        var shiftedHeroes = 0;
        for (var match = 0; match < matches; match++)
        {
            var drawn = LineUpShape.FullMatch.Draw(random, pool);
            var shifts = BuildShifts.Of(drawn.Enemies.Concat(drawn.Allies).Append(drawn.Self!)
                .Select(heroId => BuildShifts.Shift(store, heroId, Inventory(building, usual[heroId]), usual[heroId], costs))
                .OfType<HeroShift>());
            foreach (var shift in shifts.All)
            {
                moved += Math.Abs(shift.PointsOn(BuildShifts.BulletTrait));
                net += shift.PointsOn(BuildShifts.BulletTrait);
                shiftedHeroes++;
                Assert.InRange(shift.Lean, -2.0, 2.0);
            }
            var lineUp = drawn with { Shifts = shifts };
            for (var i = 0; i < items.Count; i++)
            {
                var score = ItemScoring.Total(matrix, items[i], lineUp);
                sums[i] += score;
                squares[i] += score * score;
                plain[i] += Math.Pow(ItemScoring.Total(matrix, items[i], drawn), 2);
            }
        }

        // A hero moves a few points on a scale of a hundred, and a little more up than down: the cost of a cell ties the lean to the ramp.
        Assert.InRange(moved / shiftedHeroes, 1.0, BuildShifts.Alpha);
        Assert.InRange(net / shiftedHeroes, -0.1 * moved / shiftedHeroes, 0.1 * moved / shiftedHeroes);

        // The mean in units of the item's spread, as for focus and overlap, with room for that.
        var drift = Enumerable.Range(0, items.Count)
            .Where(i => squares[i] > 0)
            .Select(i => (Item: items[i], Mean: sums[i] / matches / Math.Sqrt(squares[i] / matches)))
            .ToList();
        Assert.All(drift, item => Assert.InRange(item.Mean, -0.12, 0.12));
        Assert.InRange(drift.Average(item => item.Mean), -0.03, 0.03);
        var ranked = drift.Where(item => store.Items[item.Item].CastOn is not null).Select(item => item.Mean).ToList();
        Assert.NotEmpty(ranked);
        Assert.InRange(ranked.Average(), -0.03, 0.03);

        // The spread of an item's scores grows by a small share at most.
        var grown = Enumerable.Range(0, items.Count).Where(i => plain[i] > 0).Select(i => Math.Sqrt(squares[i] / plain[i])).ToList();
        Assert.InRange(grown.Max(), 0.9, 1.5);
        Assert.InRange(grown.Average(), 1.0, 1.15);
    }

    /// <summary>Six to twelve cells, each of a shop in the hero's usual proportions and any tier, as a scoreboard read would show.</summary>
    private static HeroBuild Inventory(Random random, IReadOnlyDictionary<ShopCategory, double> usual)
    {
        var cells = new List<BuildCell>();
        for (var cell = 0; cell < 6 + random.Next(7); cell++)
        {
            var pick = random.NextDouble();
            var category = ShopCategory.Spirit;
            foreach (var (shop, share) in usual)
            {
                pick -= share;
                if (pick > 0)
                    continue;
                category = shop;
                break;
            }
            cells.Add(new BuildCell(cell, CellKind.Item, category, null, 1 + random.Next(4)));
        }
        return new HeroBuild(cells, null, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void TheRealDataProducesAFullReport()
    {
        var store = Golden.LoadStore();
        var report = Build(store);

        Assert.Equal(ModelHealth.SimulatedMatches, report.Matches);
        Assert.Equal(store.Items.Values.Count(item => ItemScoring.Tiers.Contains(item.Tier)), report.Shares.Count);
        Assert.All(report.Shares, share => Assert.True(share.TopShare <= share.ShownShare));
        Assert.True(report.HasMatchData);
        Assert.NotEmpty(report.Lines());
    }
}
