using DeadlockAdvisor.Models;
using DeadlockAdvisor.Vision.Items;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// How each hero usually divides what they spend on items among the three shops, from the same "as" purchase counts as
/// <see cref="MatchStatsMath.BuildRatios"/>: each item counts for its tier's cost (<see cref="HeroBuild.TierCost"/>, the cost the
/// scoreboard read gives a cell) times the matches it was bought in. A build seen in a match is held against this to see how far the
/// hero has gone from their usual (<see cref="BuildShifts"/>).
/// </summary>
public static class HeroTypicalBuild
{
    /// <summary>Under this many purchases over the patches (about 500 matches), a hero's usual build is too thin to lean on.</summary>
    public const long MinPurchases = 5000;

    /// <summary>
    /// Each hero's share of their item souls in each shop, averaged over the patches by <paramref name="weights"/> (by patch start;
    /// without one a patch counts by its purchases, as if the patches were one). Ranks don't come into it, as for the build ratios.
    /// A hero with fewer than <see cref="MinPurchases"/> purchases is left out, so is every hero without counts.
    /// </summary>
    public static Dictionary<string, IReadOnlyDictionary<ShopCategory, double>> For(
        IReadOnlyList<MatchSegment> segments, IEnumerable<Item> items, IReadOnlyDictionary<long, double>? weights = null)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<ShopCategory, double>>();
        if (segments.Count == 0)
            return result;

        var known = new Dictionary<long, (ShopCategory Category, int Cost)>();
        foreach (var item in items.Where(item => item.GameId != 0 && item.Tier is >= 1 and <= 4))
        {
            if (ItemTemplates.CategoryOf(item.Category) is { } category)
                known[item.GameId] = (category, HeroBuild.TierCost(item.Tier));
        }

        var totals = new Dictionary<string, (double[] Shares, double Weight, long Purchases)>();
        foreach (var segment in segments)
        {
            foreach (var (heroId, halves) in segment.EveryMatch.As)
            {
                var souls = new double[Enum.GetValues<ShopCategory>().Length];
                long purchases = 0;
                foreach (var (gameId, counted) in halves.Whole)
                {
                    if (!known.TryGetValue(gameId, out var item))
                        continue;
                    souls[(int)item.Category] += (double)counted.Matches * item.Cost;
                    purchases += counted.Matches;
                }
                var sum = souls.Sum();
                if (sum <= 0)
                    continue;
                var weight = weights?.GetValueOrDefault(segment.Patch.Start) ?? purchases;
                var (shares, was, before) = totals.GetValueOrDefault(heroId, (new double[souls.Length], 0.0, 0L));
                for (var i = 0; i < souls.Length; i++)
                    shares[i] += weight * souls[i] / sum;
                totals[heroId] = (shares, was + weight, before + purchases);
            }
        }

        foreach (var (heroId, (shares, weight, purchases)) in totals)
        {
            if (weight <= 0 || purchases < MinPurchases)
                continue;
            result[heroId] = Enum.GetValues<ShopCategory>().ToDictionary(category => category, category => shares[(int)category] / weight);
        }
        return result;
    }
}
