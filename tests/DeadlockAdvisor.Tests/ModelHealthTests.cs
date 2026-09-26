using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;

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
            store.MatchLift[new MatchLiftKey("countered", heroId, "against", "full")] =
                new MatchLift("countered", heroId, "against", "full", 5000, -0.1 * i, 0.1, -0.1 * i);
        }
        store.MatchLift[new MatchLiftKey("untagged", "h5", "against", "full")] =
            new MatchLift("untagged", "h5", "against", "full", 5000, 2.1, 0.2, 2.0);
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
    public void TooFewProfiledHeroesSkipTheSimulation()
    {
        var report = Build(TestStore.Make());

        Assert.Equal(0, report.Matches);
        Assert.Empty(report.Shares);
        Assert.False(report.HasMatchData);
        Assert.Contains("Only 3 hero(es) are profiled; simulating a match needs 12.", report.Lines());
    }

    [Fact]
    public void TheRealDataProducesAFullReport()
    {
        var store = Golden.LoadStore();
        var report = Build(store);

        Assert.Equal(ModelHealth.SimulatedMatches, report.Matches);
        Assert.Equal(store.Items.Values.Count(item => ItemScoring.FullTiers.Contains(item.Tier)), report.Shares.Count);
        Assert.All(report.Shares, share => Assert.True(share.TopShare <= share.ShownShare));
        Assert.True(report.HasMatchData);
        Assert.NotEmpty(report.Lines());
    }
}
