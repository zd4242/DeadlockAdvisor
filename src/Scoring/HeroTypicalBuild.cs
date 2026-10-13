using DeadlockAdvisor.Models;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Scoring;

/// <summary>How a hero usually spends their item souls: the share each shop gets, and what the items they buy push their damage toward.</summary>
/// <param name="Shares">By shop, adding up to 1.</param>
/// <param name="Lean">The cost-weighted mean of their purchases' leans (<see cref="ItemLean"/>): +1 all bullet damage, -1 all spirit damage.</param>
public sealed record TypicalBuild(IReadOnlyDictionary<ShopCategory, double> Shares, double Lean);

/// <summary>
/// How each hero usually builds, from the same "as" purchase counts as <see cref="MatchStatsMath.BuildRatios"/>: each item counts for its
/// tier's cost (<see cref="HeroBuild.TierCost"/>, the cost the scoreboard read gives a cell) times the matches it was bought in. A build seen
/// in a match is held against this to see how far the hero has gone from their usual (<see cref="BuildShifts"/>).
/// </summary>
public static class HeroTypicalBuild
{
    /// <summary>Under this many purchases over the patches (about 500 matches), a hero's usual build is too thin to lean on.</summary>
    public const long MinPurchases = 5000;

    /// <summary>
    /// Each hero's usual build, averaged over the patches by <paramref name="weights"/> (by patch start; without one a patch counts by its
    /// purchases, as if the patches were one). Ranks don't come into it, as for the build ratios. A hero with fewer than
    /// <see cref="MinPurchases"/> purchases is left out, so is every hero without counts.
    /// </summary>
    /// <param name="leanOf">An item's lean by item id (<see cref="DataStore.LeanOf"/>).</param>
    public static Dictionary<string, TypicalBuild> For(
        IReadOnlyList<MatchSegment> segments, IEnumerable<Item> items, Func<string, double> leanOf, IReadOnlyDictionary<long, double>? weights = null)
    {
        var result = new Dictionary<string, TypicalBuild>();
        if (segments.Count == 0)
            return result;

        var shops = Enum.GetValues<ShopCategory>().Length;
        var known = new Dictionary<long, (ShopCategory Category, int Cost, double Lean)>();
        foreach (var item in items.Where(item => item.GameId != 0 && item.Tier is >= 1 and <= 4))
        {
            if (ItemTemplates.CategoryOf(item.Category) is { } category)
                known[item.GameId] = (category, HeroBuild.TierCost(item.Tier), leanOf(item.ItemId));
        }

        // Per hero: each shop's weighted share, the weighted lean, the weight, the purchases.
        var totals = new Dictionary<string, (double[] Shares, double Lean, double Weight, long Purchases)>();
        foreach (var segment in segments)
        {
            foreach (var (heroId, halves) in segment.EveryMatch.As)
            {
                var souls = new double[shops];
                var leaning = 0.0;
                long purchases = 0;
                foreach (var (gameId, counted) in halves.Whole)
                {
                    if (!known.TryGetValue(gameId, out var item))
                        continue;
                    var spent = (double)counted.Matches * item.Cost;
                    souls[(int)item.Category] += spent;
                    leaning += spent * item.Lean;
                    purchases += counted.Matches;
                }
                var sum = souls.Sum();
                if (sum <= 0)
                    continue;
                var weight = weights?.GetValueOrDefault(segment.Patch.Start) ?? purchases;
                var (shares, lean, was, before) = totals.GetValueOrDefault(heroId, (new double[shops], 0.0, 0.0, 0L));
                for (var i = 0; i < shops; i++)
                    shares[i] += weight * souls[i] / sum;
                totals[heroId] = (shares, lean + weight * leaning / sum, was + weight, before + purchases);
            }
        }

        foreach (var (heroId, (shares, lean, weight, purchases)) in totals)
        {
            if (weight <= 0 || purchases < MinPurchases)
                continue;
            result[heroId] = new TypicalBuild(
                Enum.GetValues<ShopCategory>().ToDictionary(category => category, category => shares[(int)category] / weight), lean / weight);
        }
        return result;
    }
}
