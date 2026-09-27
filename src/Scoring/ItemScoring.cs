using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// Turns the trait-based formulas into item scores for the heroes in a match:
/// <c>weight(item, hero, relation) = Σ over traits of (hero_score[hero, trait] − roster_average[trait]) × effective_coefficient[item, trait, relation]</c>,
/// summed over everyone in the match, each hero's weight times their <see cref="NetWorthWeights"/> factor
/// when scores lean on net worth. Measuring each hero against the roster average makes a score
/// mean "this match wants the item more than a typical one does": a trait every hero has would
/// otherwise give its items the same bonus in every match. <see cref="ExplainItem"/> walks the same
/// arithmetic one item at a time; <see cref="DataScores"/> is the second opinion from real matches,
/// reported beside the hand model's score and never mixed into it.
/// </summary>
public static class ItemScoring
{
    public static readonly IReadOnlyList<int> LaneTiers = [1, 2];
    public static readonly IReadOnlyList<int> FullTiers = [1, 2, 3, 4];

    /// <summary>Bars for <see cref="DataOnlyPicks"/>, a few times each relation's typical real lift.</summary>
    public const double PickMinAgainst = 1.0;
    public const double PickMinAs = 3.0;

    /// <summary>
    /// Precompute weight(item, hero, relation) for every combination reachable from a nonzero
    /// coefficient. Rebuild whenever the data changes.
    /// </summary>
    public static Dictionary<MatrixKey, double> BuildWeightMatrix(DataStore store)
    {
        var baselines = store.TraitBaselines();
        var profiled = store.Heroes.Keys.Where(store.IsProfiled).ToList();
        var matrix = new Dictionary<MatrixKey, double>();
        foreach (var (key, coefficient) in store.EffectiveCoefficients())
        {
            var baseline = baselines.GetValueOrDefault(key.CategoryId);
            foreach (var heroId in profiled)
            {
                var deviation = store.HeroScore(heroId, key.CategoryId) - baseline;
                if (deviation == 0)
                    continue;
                var cell = new MatrixKey(key.ItemId, heroId, key.Relation);
                matrix[cell] = matrix.GetValueOrDefault(cell) + deviation * coefficient;
            }
        }
        return matrix;
    }

    /// <summary>
    /// The match's line-up, honouring the lane-phase restriction when one is given; without
    /// <paramref name="netWorth"/> every hero counts the same.
    /// </summary>
    public static LineUp RelevantHeroes(
        MatchState match, IReadOnlyCollection<string>? restrictTo, NetWorthWeights? netWorth = null)
    {
        netWorth ??= NetWorthWeights.None;
        if (restrictTo is null)
            return new LineUp(match.Allies, match.Enemies, match.SelfHero, netWorth);

        var allowed = restrictTo.ToHashSet();
        var self = match.SelfHero is { } hero && allowed.Contains(hero) ? hero : null;
        return new LineUp(match.Allies.Where(allowed.Contains).ToList(), match.Enemies.Where(allowed.Contains).ToList(), self, netWorth);
    }

    /// <summary>
    /// Early game: only yourself and the heroes flagged as in your lane, tiers 1-2 only.
    /// Tiers map to that tier's items, highest score first; only tiers with an item above 0 appear.
    /// </summary>
    public static OrderedDictionary<int, List<ScoredItem>> LanePhaseResults(
        DataStore store, IReadOnlyDictionary<MatrixKey, double> matrix, MatchState match, NetWorthWeights? netWorth = null) =>
        GroupPositive(store, ScoreAll(store, matrix, match, LaneTiers, match.LaneHeroes, netWorth));

    /// <summary>Everyone currently selected, all four tiers.</summary>
    public static OrderedDictionary<int, List<ScoredItem>> FullMatchResults(
        DataStore store, IReadOnlyDictionary<MatrixKey, double> matrix, MatchState match, NetWorthWeights? netWorth = null) =>
        GroupPositive(store, ScoreAll(store, matrix, match, FullTiers, null, netWorth));

    /// <summary>
    /// Every item in <paramref name="tiers"/> with its score for the line-up and its match data, however
    /// it scores: highest score first, then by name.
    /// </summary>
    public static List<ScoredItem> ScoreAll(
        DataStore store,
        IReadOnlyDictionary<MatrixKey, double> matrix,
        MatchState match,
        IReadOnlyList<int> tiers,
        IReadOnlyCollection<string>? restrictTo = null,
        NetWorthWeights? netWorth = null)
    {
        var lineUp = RelevantHeroes(match, restrictTo, netWorth);
        return store.Items
            .Where(entry => tiers.Contains(entry.Value.Tier))
            .Select(entry => new ScoredItem(entry.Key, entry.Value.ItemName, entry.Value.Tier, Total(matrix, entry.Key, lineUp),
                entry.Value.Category, DataScores(store, match, entry.Key, restrictTo)))
            .OrderByDescending(scored => scored.Score)
            .ThenBy(scored => scored.ItemName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The items actually worth buying (score above 0), by tier; only tiers with one appear, in the
    /// order the store first lists an item of theirs.
    /// </summary>
    private static OrderedDictionary<int, List<ScoredItem>> GroupPositive(DataStore store, IReadOnlyList<ScoredItem> ranked)
    {
        var positive = ranked.Where(scored => scored.Score > 0).ToDictionary(scored => scored.ItemId);
        var grouped = new OrderedDictionary<int, List<ScoredItem>>();
        foreach (var itemId in store.Items.Keys)
        {
            if (positive.TryGetValue(itemId, out var scored))
                grouped.TryAdd(scored.Tier, []);
        }
        foreach (var scored in ranked.Where(scored => scored.Score > 0))
            grouped[scored.Tier].Add(scored);
        return grouped;
    }

    /// <summary>
    /// One item's score for one line-up: "against" over the enemies, "with" over the allies, "as" for
    /// you, each hero's weight times their net worth factor.
    /// </summary>
    public static double Total(IReadOnlyDictionary<MatrixKey, double> matrix, string itemId, LineUp lineUp)
    {
        var total = 0.0;
        foreach (var (heroId, relation) in lineUp.Members())
            total += lineUp.NetWorth.Factor(heroId) * matrix.GetValueOrDefault(new MatrixKey(itemId, heroId, relation));
        return total;
    }

    // -- "why is this recommended?" -------------------------------------------

    /// <summary>
    /// One item's score broken down per hero, then per trait, biggest contributor first. Recomputed
    /// from the store because the per-trait detail isn't kept in the matrix.
    /// </summary>
    public static List<HeroContribution> ExplainItem(
        DataStore store, MatchState match, string itemId, IReadOnlyCollection<string>? restrictTo = null,
        NetWorthWeights? netWorth = null)
    {
        var lineUp = RelevantHeroes(match, restrictTo, netWorth);
        var baselines = store.TraitBaselines();
        return lineUp.Members()
            .Select(member => Contribution(store, baselines, itemId, member.HeroId, member.Relation, lineUp.NetWorth.StandingOf(member.HeroId)))
            .OfType<HeroContribution>()
            .OrderBy(contribution => -contribution.Amount)
            .ToList();
    }

    /// <summary>
    /// Every hero this item's rules respond to on one relation, strongest first, with the per-trait
    /// pieces: the formula editor's live preview.
    /// </summary>
    public static List<HeroContribution> ItemContributions(DataStore store, string itemId, Relation relation, int limit = 8)
    {
        var baselines = store.TraitBaselines();
        return store.Heroes.Keys
            .Select(heroId => Contribution(store, baselines, itemId, heroId, relation))
            .OfType<HeroContribution>()
            .Where(contribution => contribution.Amount != 0)
            .OrderBy(contribution => -contribution.Amount)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// One hero's share of one item's score, biggest trait first; null when no trait of the item
    /// touches the hero, or the hero isn't profiled yet.
    /// </summary>
    private static HeroContribution? Contribution(
        DataStore store, IReadOnlyDictionary<string, double> baselines, string itemId, string heroId, Relation relation,
        NetWorthStanding? netWorth = null)
    {
        if (!store.IsProfiled(heroId))
            return null;
        var parts = new List<TraitPart>();
        foreach (var (categoryId, category) in store.Categories)
        {
            if (store.EffectiveCoefficient(itemId, categoryId, relation) == 0)
                continue;
            var heroScore = store.HeroScore(heroId, categoryId);
            var baseline = baselines.GetValueOrDefault(categoryId);
            if (heroScore == baseline)
                continue;
            parts.Add(new TraitPart(
                categoryId, category.CategoryName, heroScore,
                store.Coefficient(itemId, categoryId, relation),
                store.DerivedParts(itemId, categoryId, relation).ToList(),
                store.TraitWeight(categoryId, relation),
                baseline));
        }
        if (parts.Count == 0)
            return null;

        parts = parts.OrderBy(part => -Math.Abs(part.Amount)).ToList();
        var amount = 0.0;
        foreach (var part in parts)
            amount += part.Amount;
        var heroName = store.Heroes.TryGetValue(heroId, out var hero) ? hero.HeroName : heroId;
        return new HeroContribution(heroId, heroName, relation, (netWorth?.Factor ?? 1.0) * amount, parts, netWorth);
    }

    // -- the match-data second opinion ------------------------------------------

    /// <summary>
    /// The match-data rows behind one item's data numbers: each enemy's "against" lift and your own
    /// hero's "as" lift, in the lane scope when the view is restricted to your lane. Allies have no
    /// data (the API can't filter by teammate), and heroes without a row are simply absent.
    /// </summary>
    public static List<MatchLift> DataParts(
        DataStore store, MatchState match, string itemId, IReadOnlyCollection<string>? restrictTo = null)
    {
        if (store.MatchLift.Count == 0)
            return [];

        var scope = restrictTo is null ? "full" : "lane";
        var found = new List<MatchLift>();
        foreach (var (heroId, relation) in RelevantHeroes(match, restrictTo).Members())
        {
            if (relation != Relation.With
                && store.MatchLift.TryGetValue(new MatchLiftKey(itemId, heroId, relation.Key(), scope), out var lift))
                found.Add(lift);
        }
        return found;
    }

    /// <summary>
    /// Relation → summed lift_shrunk, only for relations with data. Kept apart rather than added up:
    /// "against" is a small counter effect, "as" a much bigger one that also reflects who plays the
    /// hero, so one sum would drown the counters.
    /// </summary>
    public static OrderedDictionary<string, double> DataScores(
        DataStore store, MatchState match, string itemId, IReadOnlyCollection<string>? restrictTo = null)
    {
        var result = new OrderedDictionary<string, double>();
        foreach (var lift in DataParts(store, match, itemId, restrictTo))
            result[lift.Relation] = result.GetValueOrDefault(lift.Relation) + lift.LiftShrunk;
        return result;
    }

    /// <summary>
    /// The data's net verdict on an item, in bars: the enemies lift over <see cref="PickMinAgainst"/>
    /// plus the you lift over <see cref="PickMinAs"/>. 1 or more is a standout. Netted rather than
    /// taking the better relation, so a strong counter your own hero does badly with doesn't count.
    /// </summary>
    public static double DataStrength(OrderedDictionary<string, double> data) =>
        data.GetValueOrDefault("against") / PickMinAgainst + data.GetValueOrDefault("as") / PickMinAs;

    /// <summary>
    /// Items the match data likes that the hand model doesn't recommend at all (score ≤ 0): a
    /// <see cref="DataStrength"/> of at least 1, strongest first.
    /// </summary>
    public static List<ScoredItem> DataOnlyPicks(
        DataStore store,
        IReadOnlyDictionary<MatrixKey, double> matrix,
        MatchState match,
        IReadOnlyList<int> tiers,
        IReadOnlyCollection<string>? restrictTo = null,
        NetWorthWeights? netWorth = null,
        int limit = 6) =>
        DataOnlyPicks(ScoreAll(store, matrix, match, tiers, restrictTo, netWorth), limit);

    /// <inheritdoc cref="DataOnlyPicks(DataStore, IReadOnlyDictionary{MatrixKey, double}, MatchState, IReadOnlyList{int}, IReadOnlyCollection{string}?, NetWorthWeights?, int)"/>
    public static List<ScoredItem> DataOnlyPicks(IEnumerable<ScoredItem> scored, int limit = 6) =>
        scored
            .Where(item => !(item.Score > 0) && item.DataStrength >= 1)
            .OrderByDescending(item => item.DataStrength)
            .Take(limit)
            .ToList();

    /// <summary>
    /// (hero name, weight) pairs showing who this item's rules respond to most strongly, read off
    /// the matrix. <see cref="ItemContributions"/> is the same list with the per-trait pieces.
    /// </summary>
    public static List<(string HeroName, double Weight)> TopHeroesForItem(
        DataStore store, IReadOnlyDictionary<MatrixKey, double> matrix, string itemId, Relation relation, int limit = 8) =>
        matrix
            .Where(entry => entry.Key.ItemId == itemId && entry.Key.Relation == relation
                            && store.Heroes.ContainsKey(entry.Key.HeroId) && entry.Value != 0)
            .Select(entry => (store.Heroes[entry.Key.HeroId].HeroName, entry.Value))
            .OrderBy(pair => -pair.Value)
            .Take(limit)
            .ToList();
}
