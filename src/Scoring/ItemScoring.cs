using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// Turns the trait-based formulas into item scores for the heroes in a match:
/// <c>weight(item, hero, relation) = Σ over traits of (hero_score[hero, trait] − roster_average[trait]) × effective_coefficient[item, trait, relation]</c>,
/// summed over everyone in the match. Measuring each hero against the roster average makes a score
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

    /// <summary>The match split into (allies, enemies, you), honouring the lane-phase restriction when one is given.</summary>
    public static (List<string> Allies, List<string> Enemies, string? Self) RelevantHeroes(
        MatchState match, IReadOnlyCollection<string>? restrictTo)
    {
        if (restrictTo is null)
            return (match.Allies, match.Enemies, match.SelfHero);

        var allowed = restrictTo.ToHashSet();
        var self = match.SelfHero is { } hero && allowed.Contains(hero) ? hero : null;
        return (match.Allies.Where(allowed.Contains).ToList(), match.Enemies.Where(allowed.Contains).ToList(), self);
    }

    /// <summary>
    /// Early game: only yourself and the heroes flagged as in your lane, tiers 1-2 only.
    /// Tiers map to that tier's items, highest score first; only tiers with an item above 0 appear.
    /// </summary>
    public static OrderedDictionary<int, List<ScoredItem>> LanePhaseResults(
        DataStore store, IReadOnlyDictionary<MatrixKey, double> matrix, MatchState match) =>
        ScoreItems(store, matrix, match, LaneTiers, match.LaneHeroes);

    /// <summary>Everyone currently selected, all four tiers.</summary>
    public static OrderedDictionary<int, List<ScoredItem>> FullMatchResults(
        DataStore store, IReadOnlyDictionary<MatrixKey, double> matrix, MatchState match) =>
        ScoreItems(store, matrix, match, FullTiers, null);

    private static OrderedDictionary<int, List<ScoredItem>> ScoreItems(
        DataStore store,
        IReadOnlyDictionary<MatrixKey, double> matrix,
        MatchState match,
        IReadOnlyList<int> tiers,
        IReadOnlyCollection<string>? restrictTo)
    {
        var (allies, enemies, self) = RelevantHeroes(match, restrictTo);

        var grouped = new OrderedDictionary<int, List<ScoredItem>>();
        foreach (var (itemId, item) in store.Items)
        {
            if (!tiers.Contains(item.Tier))
                continue;
            var total = Total(matrix, itemId, allies, enemies, self);

            // Only items actually worth buying.
            if (!(total > 0))
                continue;
            if (!grouped.TryGetValue(item.Tier, out var tierItems))
            {
                tierItems = [];
                grouped[item.Tier] = tierItems;
            }
            tierItems.Add(new ScoredItem(itemId, item.ItemName, item.Tier, total, item.Category,
                DataScores(store, match, itemId, restrictTo)));
        }

        foreach (var tier in grouped.Keys.ToList())
        {
            grouped[tier] = grouped[tier]
                .OrderByDescending(scored => scored.Score)
                .ThenBy(scored => scored.ItemName, StringComparer.Ordinal)
                .ToList();
        }
        return grouped;
    }

    /// <summary>One item's score for one line-up: "against" over the enemies, "with" over the allies, "as" for you.</summary>
    public static double Total(
        IReadOnlyDictionary<MatrixKey, double> matrix, string itemId,
        IEnumerable<string> allies, IEnumerable<string> enemies, string? self)
    {
        var total = 0.0;
        foreach (var heroId in enemies)
            total += matrix.GetValueOrDefault(new MatrixKey(itemId, heroId, Relation.Against));
        foreach (var heroId in allies)
            total += matrix.GetValueOrDefault(new MatrixKey(itemId, heroId, Relation.With));
        if (self is not null)
            total += matrix.GetValueOrDefault(new MatrixKey(itemId, self, Relation.As));
        return total;
    }

    // -- "why is this recommended?" -------------------------------------------

    /// <summary>
    /// One item's score broken down per hero, then per trait, biggest contributor first. Recomputed
    /// from the store because the per-trait detail isn't kept in the matrix.
    /// </summary>
    public static List<HeroContribution> ExplainItem(
        DataStore store, MatchState match, string itemId, IReadOnlyCollection<string>? restrictTo = null)
    {
        var (allies, enemies, self) = RelevantHeroes(match, restrictTo);
        var targets = enemies.Select(hero => (hero, Relation.Against))
            .Concat(allies.Select(hero => (hero, Relation.With)))
            .ToList();
        if (self is not null)
            targets.Add((self, Relation.As));

        var baselines = store.TraitBaselines();
        return targets
            .Select(target => Contribution(store, baselines, itemId, target.hero, target.Item2))
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
        DataStore store, IReadOnlyDictionary<string, double> baselines, string itemId, string heroId, Relation relation)
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
        return new HeroContribution(heroId, heroName, relation, amount, parts);
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

        var (_, enemies, self) = RelevantHeroes(match, restrictTo);
        var scope = restrictTo is null ? "full" : "lane";
        var targets = enemies.Select(hero => (hero, Relation.Against.Key())).ToList();
        if (self is not null)
            targets.Add((self, Relation.As.Key()));

        var found = new List<MatchLift>();
        foreach (var (heroId, relation) in targets)
        {
            if (store.MatchLift.TryGetValue(new MatchLiftKey(itemId, heroId, relation, scope), out var lift))
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
    /// Items the match data likes that the hand model doesn't recommend at all (score ≤ 0): an
    /// enemies lift of at least <see cref="PickMinAgainst"/> or a you lift of at least
    /// <see cref="PickMinAs"/>, best first by whichever clears its bar by more.
    /// </summary>
    public static List<ScoredItem> DataOnlyPicks(
        DataStore store,
        IReadOnlyDictionary<MatrixKey, double> matrix,
        MatchState match,
        IReadOnlyList<int> tiers,
        IReadOnlyCollection<string>? restrictTo = null,
        int limit = 6)
    {
        var picks = new List<(double Margin, ScoredItem Item)>();
        foreach (var (itemId, item) in store.Items)
        {
            if (!tiers.Contains(item.Tier))
                continue;
            var data = DataScores(store, match, itemId, restrictTo);
            var margin = Math.Max(data.GetValueOrDefault("against") / PickMinAgainst, data.GetValueOrDefault("as") / PickMinAs);
            if (margin < 1)
                continue;
            var (allies, enemies, self) = RelevantHeroes(match, restrictTo);
            var score = Total(matrix, itemId, allies, enemies, self);
            if (score > 0)
                continue;
            picks.Add((margin, new ScoredItem(itemId, item.ItemName, item.Tier, score, item.Category, data)));
        }
        return picks.OrderBy(pick => -pick.Margin).Take(limit).Select(pick => pick.Item).ToList();
    }

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
