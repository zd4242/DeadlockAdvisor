using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// Turns the trait-based formulas into item scores for the heroes in a match:
/// <c>weight(item, hero, relation) = Σ over traits of (hero_score[hero, trait] − roster_average[trait]) × effective_coefficient[item, trait, relation]</c>,
/// summed over everyone in the match, each hero's weight times their <see cref="NetWorthWeights"/> factor
/// when scores lean on net worth, and their <see cref="FocusWeights"/> factor while enemies are focused.
/// Measuring each hero against the roster average makes a score
/// mean "this match wants the item more than a typical one does": a trait every hero has would
/// otherwise give its items the same bonus in every match. <see cref="ExplainItem"/> walks the same
/// arithmetic one item at a time; <see cref="DataScores"/> is the second opinion from real matches,
/// reported beside the hand model's score and never mixed into it.
/// </summary>
public static class ItemScoring
{
    /// <summary>The shop tiers recommendations come from.</summary>
    public static readonly IReadOnlyList<int> Tiers = [1, 2, 3, 4];

    /// <summary>Bars for <see cref="DataOnlyPicks"/>, a few times each relation's typical real lift.</summary>
    public const double PickMinAgainst = 1.0;
    public const double PickMinAs = 3.0;

    /// <summary>
    /// Your hero building an item less than this share of what the average player does, and its enemy
    /// lifts start to count for less: they measure the players who do build it (<see cref="Relevance"/>).
    /// </summary>
    public const double RareBuildRatio = 0.25;

    /// <summary>
    /// Precompute weight(item, hero, relation) for every combination reachable from a nonzero
    /// coefficient, with what best-target scoring needs. Rebuild whenever the data changes.
    /// </summary>
    public static WeightMatrix BuildWeightMatrix(DataStore store)
    {
        var baselines = store.TraitBaselines();
        var profiled = store.Heroes.Keys.Where(store.IsProfiled).ToList();
        var summed = new Dictionary<MatrixKey, double>();
        var ranked = new Dictionary<MatrixKey, double>();
        foreach (var (key, coefficient) in store.EffectiveCoefficients())
        {
            var weights = store.OnBestTargets(key.ItemId, key.CategoryId, key.Relation) ? ranked : summed;
            var baseline = baselines.GetValueOrDefault(key.CategoryId);
            foreach (var heroId in profiled)
            {
                var deviation = store.HeroScore(heroId, key.CategoryId) - baseline;
                if (deviation == 0)
                    continue;
                var cell = new MatrixKey(key.ItemId, heroId, key.Relation);
                weights[cell] = weights.GetValueOrDefault(cell) + deviation * coefficient;
            }
        }
        return new WeightMatrix(summed, ranked, profiled);
    }

    /// <summary>
    /// The match's line-up, its focused enemies counting for more; without <paramref name="netWorth"/> net
    /// worth leaves every hero as they are.
    /// </summary>
    public static LineUp RelevantHeroes(MatchState match, NetWorthWeights? netWorth = null)
    {
        var enemies = match.Enemies;
        return new LineUp(match.Allies, enemies, match.SelfHero, netWorth ?? NetWorthWeights.None, FocusWeights.For(enemies, match.Focused));
    }

    /// <summary>
    /// Everyone currently selected, every tier. Tiers map to that tier's items, highest score first;
    /// only tiers with an item above 0 appear.
    /// </summary>
    public static OrderedDictionary<int, List<ScoredItem>> FullMatchResults(
        DataStore store, WeightMatrix matrix, MatchState match, NetWorthWeights? netWorth = null) =>
        GroupPositive(store, ScoreAll(store, matrix, match, netWorth));

    /// <summary>
    /// Every item in <see cref="Tiers"/> with its score for the line-up and its match data, however
    /// it scores: highest score first, then by name.
    /// </summary>
    public static List<ScoredItem> ScoreAll(DataStore store, WeightMatrix matrix, MatchState match, NetWorthWeights? netWorth = null)
    {
        var lineUp = RelevantHeroes(match, netWorth);
        return store.Items
            .Where(entry => Tiers.Contains(entry.Value.Tier))
            .Select(entry => new ScoredItem(entry.Key, entry.Value.ItemName, entry.Value.Tier, Total(matrix, entry.Key, lineUp),
                entry.Value.Category, DataScores(store, lineUp, entry.Key), BuildRatio(store, entry.Key, lineUp.Self), entry.Value.Cost))
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
    /// you, each hero's weight times their net worth and focus factors. The part of a weight from best-target lines
    /// (<see cref="DataStore.OnBestTargets"/>) counts by <see cref="BestTargets"/> over its team instead of summing,
    /// less what a typical team focused the same way comes to.
    /// </summary>
    public static double Total(WeightMatrix matrix, string itemId, LineUp lineUp)
    {
        var total = 0.0;
        Dictionary<Relation, List<string>>? targets = null;
        foreach (var (heroId, relation) in lineUp.Members())
        {
            total += lineUp.Factor(heroId) * matrix.Summed(new MatrixKey(itemId, heroId, relation));
            if (!matrix.OnBestTargets(itemId, relation) || !matrix.IsProfiled(heroId))
                continue;
            targets ??= [];
            if (!targets.TryGetValue(relation, out var team))
                targets[relation] = team = [];
            team.Add(heroId);
        }
        foreach (var (relation, team) in targets ?? [])
        {
            total += BestTargets.Sum(team.Select(heroId => lineUp.Factor(heroId) * matrix.Ranked(new MatrixKey(itemId, heroId, relation))))
                     - lineUp.Focus.Typical(relation, team, focused => matrix.Typical(itemId, relation, team.Count, focused));
        }
        return total;
    }

    // -- "why is this recommended?" -------------------------------------------

    /// <summary>
    /// One item's score broken down per hero, then per trait, biggest contributor first. Recomputed
    /// from the store because the per-trait detail isn't kept in the matrix. On a relation with best-target
    /// lines, each hero's best-target lines count at the hero's rank beside the lines that sum, with a
    /// typical team's sum taken off as one more line.
    /// </summary>
    public static List<HeroContribution> ExplainItem(DataStore store, MatchState match, string itemId, NetWorthWeights? netWorth = null)
    {
        var lineUp = RelevantHeroes(match, netWorth);
        var baselines = store.TraitBaselines();
        var contributions = new List<HeroContribution>();
        foreach (var relation in new[] { Relation.Against, Relation.With, Relation.As })
        {
            var team = lineUp.Members().Where(member => member.Relation == relation).Select(member => member.HeroId).ToList();
            List<HeroContribution> Found(bool? ranked) => team
                .Select(heroId => Contribution(store, baselines, itemId, heroId, relation,
                    lineUp.NetWorth.StandingOf(heroId), lineUp.Focus.Factor(heroId), lineUp.Focus.IsFocused(heroId), ranked))
                .OfType<HeroContribution>()
                .ToList();
            contributions.AddRange(store.HasBestTargetLines(itemId, relation)
                ? OnBestTargets(store, baselines, itemId, relation, team.Where(store.IsProfiled).ToList(), lineUp.Focus, Found(null), Found(true))
                : Found(null));
        }
        return contributions.OrderBy(contribution => -contribution.Amount).ToList();
    }

    /// <summary>
    /// Each hero on one relation with their best-target lines at the hero's <see cref="BestTargets"/> rank, then the
    /// typical team's sum, focused as this one is, as a line of its own. Every profiled hero on the team takes a
    /// rank, even one no rule touches, exactly as <see cref="Total"/> counts them.
    /// </summary>
    /// <param name="found">Each hero's every line.</param>
    /// <param name="ranked">Each hero's best-target lines only: what ranks them.</param>
    private static IEnumerable<HeroContribution> OnBestTargets(
        DataStore store, IReadOnlyDictionary<string, double> baselines, string itemId, Relation relation,
        IReadOnlyList<string> team, FocusWeights focus, IReadOnlyList<HeroContribution> found, IReadOnlyList<HeroContribution> ranked)
    {
        if (team.Count == 0)
            yield break;
        var weights = ranked.ToDictionary(contribution => contribution.HeroId, contribution => contribution.Amount);
        var order = team.OrderByDescending(heroId => weights.GetValueOrDefault(heroId)).ToList();
        foreach (var contribution in found)
        {
            var rank = order.IndexOf(contribution.HeroId) + 1;
            if (!weights.ContainsKey(contribution.HeroId))
            {
                yield return contribution;
                continue;
            }
            var parts = contribution.Parts
                .Select(part => store.OnBestTargets(itemId, part.CategoryId, relation) ? part with { Rank = rank } : part)
                .OrderByDescending(part => part.Share)
                .ToList();
            var amount = 0.0;
            foreach (var part in parts)
                amount += part.Share;
            yield return contribution with { Amount = contribution.Factor * amount, Parts = parts, Rank = rank };
        }

        // One hero's typical value is the roster's average weight, which is 0: nothing to take off.
        if (team.Count == 1)
            yield break;
        var roster = store.Heroes.Keys
            .Where(store.IsProfiled)
            .Select(heroId => Contribution(store, baselines, itemId, heroId, relation, ranked: true)?.Amount ?? 0.0)
            .ToList();
        var count = Math.Min(team.Count, BestTargets.MaxTeam);
        var typical = focus.Typical(relation, team, focused => BestTargets.Expected(roster, count, focused, FocusWeights.Ratio));
        if (typical != 0)
            yield return new HeroContribution("", relation == Relation.Against ? "Typical enemy team" : "Typical allies", relation, -typical, [], TypicalOf: team.Count);
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
    /// One hero's share of one item's score, biggest increase first; null when no trait of the item
    /// touches the hero, or the hero isn't profiled yet.
    /// </summary>
    /// <param name="focus">The hero's <see cref="FocusWeights"/> factor, and whether they're focused.</param>
    /// <param name="ranked">Only the best-target lines (true) or only the summed ones (false); every line when null.</param>
    private static HeroContribution? Contribution(
        DataStore store, IReadOnlyDictionary<string, double> baselines, string itemId, string heroId, Relation relation,
        NetWorthStanding? netWorth = null, double focus = 1.0, bool isFocused = false, bool? ranked = null)
    {
        if (!store.IsProfiled(heroId))
            return null;
        var parts = new List<TraitPart>();
        foreach (var (categoryId, category) in store.Categories)
        {
            if (store.EffectiveCoefficient(itemId, categoryId, relation) == 0)
                continue;
            if (ranked is { } wanted && store.OnBestTargets(itemId, categoryId, relation) != wanted)
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

        parts = parts.OrderByDescending(part => part.Amount).ToList();
        var amount = 0.0;
        foreach (var part in parts)
            amount += part.Amount;
        var heroName = store.Heroes.TryGetValue(heroId, out var hero) ? hero.HeroName : heroId;
        return new HeroContribution(heroId, heroName, relation, (netWorth?.Factor ?? 1.0) * focus * amount, parts, netWorth,
            Focus: focus, IsFocused: isFocused);
    }

    // -- the match-data second opinion ------------------------------------------

    /// <summary>
    /// The match-data rows behind one item's data numbers: each enemy's "against" lift and your own
    /// hero's "as" lift. Allies have no data (the API can't filter by teammate), and heroes without a
    /// row are simply absent.
    /// </summary>
    public static List<MatchLift> DataParts(DataStore store, MatchState match, string itemId) =>
        DataParts(store, RelevantHeroes(match), itemId);

    /// <inheritdoc cref="DataParts(DataStore, MatchState, string)"/>
    public static List<MatchLift> DataParts(DataStore store, LineUp lineUp, string itemId)
    {
        if (store.MatchLift.Count == 0)
            return [];

        var found = new List<MatchLift>();
        foreach (var (heroId, relation) in lineUp.Members())
        {
            if (relation != Relation.With
                && store.MatchLift.TryGetValue(new MatchLiftKey(itemId, heroId, relation.Key()), out var lift))
                found.Add(lift);
        }
        return found;
    }

    /// <summary>
    /// Relation → summed lift_shrunk, only for relations with data. Kept apart rather than added up:
    /// "against" is a small counter effect, "as" a much bigger one that also reflects who plays the
    /// hero, so one sum would drown the counters. The "against" sum is counted by <see cref="Relevance"/>,
    /// each enemy's lift by their focus factor: focus asks which enemies the items should answer, which the
    /// data knows as well as the formula. Net worth never weights it.
    /// </summary>
    public static OrderedDictionary<string, double> DataScores(DataStore store, MatchState match, string itemId) =>
        DataScores(store, RelevantHeroes(match), itemId);

    /// <inheritdoc cref="DataScores(DataStore, MatchState, string)"/>
    public static OrderedDictionary<string, double> DataScores(DataStore store, LineUp lineUp, string itemId)
    {
        var result = new OrderedDictionary<string, double>();
        foreach (var lift in DataParts(store, lineUp, itemId))
            result[lift.Relation] = result.GetValueOrDefault(lift.Relation) + lineUp.Focus.Factor(lift.HeroId) * lift.LiftShrunk;
        if (result.TryGetValue(Relation.Against.Key(), out var against))
            result[Relation.Against.Key()] = against * Relevance(BuildRatio(store, itemId, lineUp.Self));
        return result;
    }

    /// <summary>How often your hero builds the item next to the average player; null without a hero or download counts.</summary>
    public static double? BuildRatio(DataStore store, string itemId, string? heroId) =>
        heroId is not null && store.BuildRatios.TryGetValue((itemId, heroId), out var ratio) ? ratio : null;

    /// <summary>
    /// How much the enemy lifts count: in full once your hero builds the item at least
    /// <see cref="RareBuildRatio"/> as often as the average player, in proportion below that, not at all
    /// when it never does. The lifts average over the players who build it, and a hero that doesn't is
    /// unlike them. In full when the ratio is unknown.
    /// </summary>
    public static double Relevance(double? buildRatio) =>
        buildRatio is { } ratio ? Math.Clamp(ratio / RareBuildRatio, 0.0, 1.0) : 1.0;

    /// <summary>
    /// The data's net verdict on an item, in bars: the enemies lift over <see cref="PickMinAgainst"/>
    /// plus the hero fit (the "you" lift) over <see cref="PickMinAs"/>. 1 or more is a standout. Netted rather than
    /// taking the better relation, so a strong counter your own hero does badly with doesn't count.
    /// </summary>
    public static double DataStrength(OrderedDictionary<string, double> data) =>
        data.GetValueOrDefault("against") / PickMinAgainst + data.GetValueOrDefault("as") / PickMinAs;

    /// <summary>
    /// Items the match data likes that the hand model doesn't recommend at all (score ≤ 0): a
    /// <see cref="DataStrength"/> of at least 1, strongest first.
    /// </summary>
    public static List<ScoredItem> DataOnlyPicks(
        DataStore store, WeightMatrix matrix, MatchState match, NetWorthWeights? netWorth = null, int limit = 6) =>
        DataOnlyPicks(ScoreAll(store, matrix, match, netWorth), limit);

    /// <inheritdoc cref="DataOnlyPicks(DataStore, WeightMatrix, MatchState, NetWorthWeights?, int)"/>
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
