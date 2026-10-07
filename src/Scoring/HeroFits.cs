using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Scoring;

/// <summary>How much more one hero wins with an item than everyone who builds it does, that hero's players included, next to its other items of the same tier.</summary>
/// <param name="EveryoneWinRate">The share of every player's matches with the item that were won, as a fraction.</param>
/// <param name="TierAverage">What the hero's items of the item's tier gain over everyone's, in win-rate points: the lift's zero.</param>
/// <param name="Raw">The lift in win-rate points, before shrinking.</param>
/// <param name="Shown">The lift with a small or noisy sample pulled toward 0, as the Match tab's hero fit (its "you" number) is.</param>
public sealed record HeroFit(double EveryoneWinRate, double TierAverage, double Raw, double Shown);

/// <summary>
/// The Match tab's hero fit, its "you" lift (<see cref="MatchStatsMath.RawLifts"/>, then shrunk), over the matches a hero's
/// item table counts, so it follows the table's patches, match mode and ranks. Everyone's win rate with an item
/// is every hero's own purchases added up, which is every match's whatever the mode or ranks.
/// </summary>
public static class HeroFits
{
    /// <summary>An item bought in fewer matches than this gets no fit, though it still counts toward its tier's average.</summary>
    public const int MinMatches = MatchStatsMath.MinN / 4;

    /// <summary>
    /// How far off the textbook standard errors are, measured from the patches' every-match halves like the
    /// Match tab's. Ranked-only and rank-group counts have no halves, so every match's stand in for them.
    /// </summary>
    public static double NoiseScale(IEnumerable<MatchSegment> patches, IReadOnlyDictionary<long, int> tiers) =>
        MatchStatsMath.NoiseScale(patches.SelectMany(patch =>
            MatchStatsMath.LiftsOf(new FamilyWindow(patch.EveryMatch.Baseline, patch.EveryMatch.As), tiers, MinMatches)
                .HalfPairs(MatchStatsMath.MinN / 2)));

    /// <summary>One hero's fit for each of its items with enough matches, by game item id.</summary>
    /// <param name="heroes">Every hero's matches and purchases over the same window.</param>
    public static Dictionary<long, HeroFit> Of(
        string heroId, IReadOnlyDictionary<string, HeroCounts> heroes, IReadOnlyDictionary<long, int> tiers, double noiseScale)
    {
        var everyone = new Dictionary<long, WinTotals>();
        foreach (var hero in heroes.Values)
            everyone = MatchStatsMath.Merge(everyone, hero.Items);

        var lifts = heroes.ToDictionary(
            hero => hero.Key,
            hero => MatchStatsMath.Rescale(MatchStatsMath.RawLifts(hero.Value.Items, everyone, tiers, MinMatches), noiseScale));
        var fits = new Dictionary<long, HeroFit>();
        if (!lifts.TryGetValue(heroId, out var mine))
            return fits;

        var tau2 = MatchStatsMath.EstimateTau2(lifts.Values.SelectMany(heroLifts => heroLifts.Values));
        foreach (var (item, lift) in mine)
        {
            var (wins, matches) = heroes[heroId].Items[item];
            var (everyoneWins, everyoneMatches) = everyone[item];
            var everyoneRate = (double)everyoneWins / everyoneMatches;
            var gain = 100 * ((double)wins / matches - everyoneRate);
            fits[item] = new HeroFit(everyoneRate, gain - lift.Lift, lift.Lift, MatchStatsMath.Shrink(lift, tau2));
        }
        return fits;
    }
}
