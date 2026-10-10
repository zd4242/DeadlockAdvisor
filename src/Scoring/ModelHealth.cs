using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Scoring;

/// <param name="TopShare">Share of simulated matches with the item among its tier's top <see cref="ModelHealth.TopCount"/>.</param>
/// <param name="ShownShare">Share of simulated matches where it scores above 0, so the list shows it at all.</param>
/// <param name="Swing">How far its score typically strays from 0 across the simulated matches: the root mean square.</param>
public sealed record ItemShare(string ItemId, string ItemName, int Tier, double TopShare, double ShownShare, double Swing = 0);

/// <summary>An item whose hand weights run opposite to its real lifts across heroes.</summary>
public sealed record Disagreement(string ItemName, Relation Relation, double R, int Heroes);

/// <summary>A real lift that clears the data-only bar where the hand model gives the item nothing.</summary>
public sealed record DataOnlyPair(string ItemName, string HeroName, Relation Relation, double Lift, double Weight, double Margin);

/// <summary>
/// How well the hand weights follow the real lifts on one relation, pooled over every item: each item's
/// weights and lifts are measured from their own averages, so it's the pattern across heroes that counts.
/// </summary>
public sealed record Agreement(Relation Relation, double R, int Pairs);

/// <summary>
/// A clear slope of an item's enemy lifts across heroes on one trait, where the hand model has no
/// coefficient or one of the other sign.
/// </summary>
/// <param name="Slope">Lift points per trait point the hero sits above the roster average.</param>
/// <param name="Coefficient">
/// The slope as a coefficient typed on Item Formulas, by how many lift points a point of hand weight brings
/// over every item; null when the hand weights don't follow the lifts overall, so there's no such scale.
/// </param>
/// <param name="Hand">The item's coefficient on the trait now, typed and from stats, in the same units.</param>
public sealed record Suggestion(string ItemName, string TraitName, double Slope, double T, double? Coefficient, double Hand, int Heroes);

/// <param name="Matches">How many matches were simulated; 0 when too few heroes are profiled to fill one.</param>
/// <param name="HasMatchData">False skips the data sections: Download Match Data hasn't run.</param>
/// <param name="DataSource">Which patches and ranks the match data comes from, and how much it moves from patch to patch.</param>
public sealed record ModelHealthReport(
    int Matches,
    int ProfiledHeroes,
    IReadOnlyList<ItemShare> Shares,
    IReadOnlyList<string> NoRules,
    IReadOnlyList<string> EmptyTraitsOnly,
    IReadOnlyList<string> NeverPositive,
    IReadOnlyList<string> EmptyTraits,
    bool HasMatchData,
    IReadOnlyList<Disagreement> Disagreements,
    IReadOnlyList<DataOnlyPair> DataOnly,
    IReadOnlyList<string> DataSource,
    IReadOnlyList<Agreement> Agreements,
    IReadOnlyList<Suggestion> Suggestions)
{
    public IEnumerable<ItemShare> AlwaysOn =>
        Shares.Where(share => share.TopShare >= ModelHealth.AlwaysOnShare)
            .OrderBy(share => share.Tier)
            .ThenByDescending(share => share.TopShare);

    /// <summary>The items whose scores swing furthest, biggest first: a coefficient much larger than the rest shows up here.</summary>
    public IEnumerable<ItemShare> BiggestSwings =>
        Shares.Where(share => share.Swing > 0)
            .OrderByDescending(share => share.Swing)
            .ThenBy(share => share.ItemName, StringComparer.Ordinal)
            .Take(ModelHealth.SwingCount);

    public List<string> Lines()
    {
        var lines = new List<string>();
        if (Matches == 0)
        {
            lines.Add($"Only {ProfiledHeroes} hero(es) are profiled; simulating a match needs {ModelHealth.MatchSize}.");
        }
        else
        {
            lines.Add($"Simulated {Format.Thousands(Matches)} random full matches (you + 5 allies + 6 enemies, "
                      + $"drawn from the {ProfiledHeroes} profiled heroes).");
            var alwaysOn = AlwaysOn.ToList();
            lines.Add("");
            lines.Add(alwaysOn.Count == 0
                ? $"No item is in its tier's top {ModelHealth.TopCount} in {Percent(ModelHealth.AlwaysOnShare)} or more of matches."
                : $"Recommended whatever the heroes -- top {ModelHealth.TopCount} of its tier in {Percent(ModelHealth.AlwaysOnShare)}+ of matches:");
            lines.AddRange(alwaysOn.Select(share => $"  T{share.Tier} {share.ItemName}: {Percent(share.TopShare)}"));

            var neverShown = NoRules.Count + EmptyTraitsOnly.Count + NeverPositive.Count;
            lines.Add("");
            lines.Add(neverShown == 0 ? "Every item is recommended in at least one simulated match." : $"Never recommended in any simulated match ({neverShown}):");
            Group(lines, "No rules at all", NoRules);
            Group(lines, "Rules only on traits no hero is scored on", EmptyTraitsOnly);
            Group(lines, "Rules never add up to more than 0", NeverPositive);

            var swings = BiggestSwings.ToList();
            if (swings.Count > 0)
            {
                var typical = Shares.Where(share => share.Swing > 0).Select(share => share.Swing).OrderBy(swing => swing).ToList();
                lines.Add("");
                lines.Add($"Biggest swings -- how far the score typically strays from 0 (the median item's is {Format.Num(Math.Round(typical[typical.Count / 2]))}); "
                          + "these reach the top and the bottom of every list:");
                lines.AddRange(swings.Select(share => $"  T{share.Tier} {share.ItemName}: ±{Format.Num(Math.Round(share.Swing))}"));
            }
        }

        if (EmptyTraits.Count > 0)
        {
            lines.Add("");
            lines.Add($"Traits rules use but every hero scores 0 on: {string.Join(", ", EmptyTraits)}");
        }

        lines.Add("");
        if (!HasMatchData)
        {
            lines.Add("No match data yet: Data → Download Match Data adds a comparison with real match results.");
            return lines;
        }
        lines.AddRange(DataSource);
        if (Agreements.Count > 0)
        {
            lines.Add("");
            lines.Add("Formula vs match data, across heroes within each item (the number to compare before and after tuning): "
                      + string.Join(", ", Agreements.Select(a => $"{Word(a.Relation)} r = {NumberFormat.Fixed(a.R, 2)} over {Format.Thousands(a.Pairs)} pairs"))
                      + ".");
        }
        lines.Add("");
        lines.Add(Disagreements.Count == 0
            ? "No item's hand weights run clearly against its real lifts."
            : $"Match data disagrees -- hand weights run opposite to real lifts across heroes (r ≤ {NumberFormat.Fixed(ModelHealth.DisagreeR, 1)}):");
        lines.AddRange(Disagreements.Select(d =>
            $"  {d.ItemName} ({Word(d.Relation)}): r = {NumberFormat.Fixed(d.R, 2)} over {d.Heroes} heroes"));
        lines.Add("");
        lines.Add(DataOnly.Count == 0
            ? "The hand model covers every standout real lift."
            : "Real standouts the hand model gives nothing -- lift clears the data-only bar, weight ≤ 0:");
        lines.AddRange(DataOnly.Select(pair =>
            $"  {pair.ItemName} {(pair.Relation == Relation.Against ? "vs" : "on")} {pair.HeroName}: "
            + $"{Format.SignedFixed(pair.Lift, 1)} pts, "
            + (pair.Weight == 0 ? "no hand weight" : $"hand weight {Format.SignedFixed(pair.Weight, 1)}")));

        lines.Add("");
        lines.Add(Suggestions.Count == 0
            ? "The enemy lifts follow no trait clearly where the hand model has nothing, or the opposite."
            : $"Traits the enemy lifts follow where the hand model has nothing, or the opposite (|t| ≥ {Format.Num(ModelHealth.SuggestT)}); "
              + "leads only, since traits overlap:");
        lines.AddRange(Suggestions.Select(s =>
            $"  {s.ItemName} · {s.TraitName}: {Format.SignedFixed(s.Slope * 10, 2)} pts per 10 points above average (t = {NumberFormat.Fixed(s.T, 1)}), "
            + (s.Coefficient is { } coefficient ? $"about {Format.SignedFixed(coefficient, 1)} as a coefficient, " : "")
            + $"hand {(s.Hand == 0 ? "0" : Format.SignedFixed(s.Hand, 1))}"));
        return lines;
    }

    private static void Group(List<string> lines, string heading, IReadOnlyList<string> names)
    {
        if (names.Count > 0)
            lines.Add($"  {heading} ({names.Count}): {string.Join(", ", names)}");
    }

    private static string Percent(double share) => $"{Math.Round(share * 100):0}%";

    private static string Word(Relation relation) => relation == Relation.Against ? "enemies" : "you";
}

/// <summary>
/// How the hand model behaves across the whole roster rather than one match: which items it
/// recommends whatever the heroes, which it never recommends, and where real match results
/// disagree with it. For tuning the data; nothing here changes a score.
/// </summary>
public static class ModelHealth
{
    public static readonly int MatchSize = LineUpShape.FullMatch.Size;
    public const int SimulatedMatches = 2000;
    public const int TopCount = 3;

    /// <summary>A top-<see cref="TopCount"/> share this high means the heroes in the match barely matter to the item.</summary>
    public const double AlwaysOnShare = 0.8;

    /// <summary>Fewer heroes with a lift than this and a correlation says little.</summary>
    public const int MinHeroesForR = 10;

    public const double DisagreeR = -0.2;
    public const int DataOnlyLimit = 15;
    public const int SwingCount = 10;

    /// <summary>
    /// How many standard errors a fitted coefficient needs to be suggested. Strict, since every item is
    /// fitted against every trait: a few thousand fits would throw up dozens of flukes at the usual 2.
    /// </summary>
    public const double SuggestT = 3.5;
    public const int SuggestionLimit = 15;

    public static ModelHealthReport Build(DataStore store, WeightMatrix matrix, int seed = 1)
    {
        var unprofiled = store.UnprofiledHeroes().ToHashSet();
        var profiled = store.Heroes.Keys.Where(heroId => !unprofiled.Contains(heroId)).ToList();
        var matches = profiled.Count >= MatchSize ? SimulatedMatches : 0;
        var shares = matches > 0 ? Simulate(store, matrix, profiled, matches, seed) : [];

        var emptyTraits = EmptyTraits(store);
        var noRules = store.UncoveredItems().ToHashSet();
        var neverShown = shares.Where(share => share.ShownShare == 0).Select(share => share.ItemId).ToList();
        var ruleCategories = RuleCategories(store);
        var pairs = new[] { Relation.Against, Relation.As }.ToDictionary(relation => relation, relation => CenteredPairs(store, matrix, relation));
        var agreements = pairs
            .Select(entry => MatchStatsMath.WeightedPearson(entry.Value) is { } r ? new Agreement(entry.Key, r, entry.Value.Count) : null)
            .OfType<Agreement>()
            .ToList();

        return new ModelHealthReport(
            matches,
            profiled.Count,
            shares,
            NamesOf(store, neverShown.Where(noRules.Contains)),
            NamesOf(store, neverShown.Where(itemId => !noRules.Contains(itemId) && ruleCategories[itemId].All(emptyTraits.Contains))),
            NamesOf(store, neverShown.Where(itemId => !noRules.Contains(itemId) && !ruleCategories[itemId].All(emptyTraits.Contains))),
            emptyTraits.Select(categoryId => store.Categories[categoryId].CategoryName).ToList(),
            store.MatchLift.Count > 0,
            Disagreements(store, matrix),
            DataOnlyPairs(store, matrix),
            DataSource(store.MatchMeta),
            agreements,
            Suggestions(store, pairs[Relation.Against], profiled));
    }

    /// <summary>"Match data: patch 09-29 (2 days so far · 18%), patch 09-16 (13 days · 82%), every match", then the drift between patches.</summary>
    private static List<string> DataSource(System.Text.Json.Nodes.JsonObject meta)
    {
        var patches = string.Join(", ", MatchStatsMath.PatchFacts(meta).Select(fact => $"{fact.Label.ToLowerInvariant()} ({fact.Value})"));
        var lines = new List<string> { $"Match data: {patches}; {MatchStatsMath.RankLabel(meta) ?? "every match"}." };
        if (MatchStatsMath.DriftLine(meta) is { } drift)
            lines.Add(drift + ".");
        return lines;
    }

    /// <summary>
    /// Scores every item in <paramref name="matches"/> random line-ups of distinct profiled heroes,
    /// ranking each tier the way the Full Match list does.
    /// </summary>
    private static List<ItemShare> Simulate(
        DataStore store, WeightMatrix matrix, List<string> heroes, int matches, int seed)
    {
        var items = store.Items.Values.Where(item => ItemScoring.Tiers.Contains(item.Tier)).ToList();
        var top = new int[items.Count];
        var shown = new int[items.Count];
        var squares = new double[items.Count];
        var byTier = items.Select((item, index) => (item, index)).GroupBy(pair => pair.item.Tier).ToList();
        var scores = new double[items.Count];
        var random = new Random(seed);
        var pool = heroes.ToArray();

        for (var match = 0; match < matches; match++)
        {
            // Random line-ups have no net worth: the report measures the hand model alone.
            var lineUp = LineUpShape.FullMatch.Draw(random, pool);

            for (var i = 0; i < items.Count; i++)
            {
                scores[i] = ItemScoring.Total(matrix, items[i].ItemId, lineUp);
                squares[i] += scores[i] * scores[i];
                if (scores[i] > 0)
                    shown[i]++;
            }
            foreach (var tier in byTier)
            {
                var best = tier
                    .Where(pair => scores[pair.index] > 0)
                    .OrderByDescending(pair => scores[pair.index])
                    .ThenBy(pair => pair.item.ItemName, StringComparer.Ordinal)
                    .Take(TopCount);
                foreach (var (_, index) in best)
                    top[index]++;
            }
        }

        return items
            .Select((item, i) => new ItemShare(
                item.ItemId, item.ItemName, item.Tier, (double)top[i] / matches, (double)shown[i] / matches, Math.Sqrt(squares[i] / matches)))
            .ToList();
    }

    /// <summary>Traits some rule uses that every hero still scores 0 on: those rules do nothing yet.</summary>
    private static HashSet<string> EmptyTraits(DataStore store)
    {
        var used = store.ItemCoefficients.Keys.Select(key => key.CategoryId)
            .Concat(store.StatRules.Values.Select(rule => rule.CategoryId))
            .ToHashSet();
        return store.Categories.Keys
            .Where(categoryId => used.Contains(categoryId) && store.Heroes.Keys.All(heroId => store.HeroScore(heroId, categoryId) is null or 0))
            .ToHashSet();
    }

    /// <summary>Item → the traits its rules (typed or from stats) touch.</summary>
    private static Dictionary<string, HashSet<string>> RuleCategories(DataStore store)
    {
        var result = store.Items.Keys.ToDictionary(itemId => itemId, _ => new HashSet<string>());
        foreach (var key in store.EffectiveCoefficients().Keys)
        {
            if (result.TryGetValue(key.ItemId, out var categories))
                categories.Add(key.CategoryId);
        }
        return result;
    }

    /// <summary>Per item and relation, the correlation across heroes between the hand weight and the real lift.</summary>
    private static List<Disagreement> Disagreements(DataStore store, IReadOnlyDictionary<MatrixKey, double> matrix)
    {
        var pairs = new Dictionary<(string ItemId, Relation Relation), List<(double X, double Y)>>();
        foreach (var lift in store.MatchLift.Values)
        {
            if (!Relations.TryParse(lift.Relation, out var relation) || !store.Items.ContainsKey(lift.ItemId))
                continue;
            var key = (lift.ItemId, relation);
            if (!pairs.TryGetValue(key, out var list))
            {
                list = [];
                pairs[key] = list;
            }
            list.Add((matrix.GetValueOrDefault(new MatrixKey(lift.ItemId, lift.HeroId, relation)), lift.LiftShrunk));
        }

        var found = new List<Disagreement>();
        foreach (var ((itemId, relation), list) in pairs)
        {
            if (list.Count < MinHeroesForR || MatchStatsMath.Pearson(list) is not { } r || r > DisagreeR)
                continue;
            found.Add(new Disagreement(store.Items[itemId].ItemName, relation, r, list.Count));
        }
        return found.OrderBy(d => d.R).ToList();
    }

    /// <summary>One relation's usable lifts (a known item and hero, a standard error), per item.</summary>
    private static Dictionary<string, List<MatchLift>> LiftsByItem(DataStore store, Relation relation) =>
        store.MatchLift.Values
            .Where(lift => lift.Relation == relation.Key() && lift.Se > 0 && store.Items.ContainsKey(lift.ItemId) && store.Heroes.ContainsKey(lift.HeroId))
            .GroupBy(lift => lift.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

    /// <summary>
    /// Every (hand weight, raw lift) pair on one relation, each counted by 1/se², with both measured from
    /// their item's own weighted averages: what's left is how each item varies across heroes.
    /// </summary>
    private static List<(double X, double Y, double W)> CenteredPairs(DataStore store, IReadOnlyDictionary<MatrixKey, double> matrix, Relation relation)
    {
        var pooled = new List<(double X, double Y, double W)>();
        foreach (var (itemId, lifts) in LiftsByItem(store, relation))
        {
            if (lifts.Count < 2)
                continue;
            var points = lifts
                .Select(lift => (X: matrix.GetValueOrDefault(new MatrixKey(itemId, lift.HeroId, relation)), Y: lift.Lift, W: 1 / (lift.Se * lift.Se)))
                .ToList();
            var sumW = points.Sum(point => point.W);
            var meanX = points.Sum(point => point.W * point.X) / sumW;
            var meanY = points.Sum(point => point.W * point.Y) / sumW;
            pooled.AddRange(points.Select(point => (point.X - meanX, point.Y - meanY, point.W)));
        }
        return pooled;
    }

    /// <summary>
    /// For every item and trait, the slope of the enemy lifts on the heroes' distance from the trait's
    /// average. Kept where it clears <see cref="SuggestT"/> and the hand coefficient is 0 or the other sign,
    /// strongest first. When the hand weights follow the lifts overall (a positive slope over
    /// <paramref name="against"/>), that slope turns each one into a coefficient.
    /// </summary>
    private static List<Suggestion> Suggestions(DataStore store, List<(double X, double Y, double W)> against, IReadOnlyList<string> profiled)
    {
        var calibration = MatchStatsMath.Slope(against) is { Value: > 0 } overall ? overall.Value : (double?)null;
        var baselines = store.TraitBaselines();
        var isProfiled = profiled.ToHashSet();
        var found = new List<Suggestion>();
        foreach (var (itemId, lifts) in LiftsByItem(store, Relation.Against))
        {
            var rated = lifts.Where(lift => isProfiled.Contains(lift.HeroId)).ToList();
            if (rated.Count < MinHeroesForR)
                continue;
            foreach (var (categoryId, category) in store.Categories)
            {
                var traitWeight = store.TraitWeight(categoryId, Relation.Against);
                var baseline = baselines.GetValueOrDefault(categoryId);
                var points = rated
                    .Where(lift => store.HeroScore(lift.HeroId, categoryId) is not null)
                    .Select(lift => (store.HeroScore(lift.HeroId, categoryId)!.Value - baseline, lift.Lift, 1 / (lift.Se * lift.Se)))
                    .ToList();
                if (traitWeight == 0 || MatchStatsMath.Slope(points) is not { } slope || Math.Abs(slope.T) < SuggestT)
                    continue;
                var hand = store.EffectiveCoefficient(itemId, categoryId, Relation.Against) / traitWeight;
                if (hand != 0 && Math.Sign(hand) == Math.Sign(slope.Value))
                    continue;
                found.Add(new Suggestion(store.Items[itemId].ItemName, category.CategoryName, slope.Value, slope.T,
                    slope.Value / calibration / traitWeight, hand, rated.Count));
            }
        }
        return found.OrderByDescending(suggestion => Math.Abs(suggestion.T)).ThenBy(suggestion => suggestion.ItemName, StringComparer.Ordinal).Take(SuggestionLimit).ToList();
    }

    /// <summary>(item, hero) lifts that clear <see cref="ItemScoring.DataOnlyPicks"/>'s bars where the hand weight is 0 or less.</summary>
    private static List<DataOnlyPair> DataOnlyPairs(DataStore store, IReadOnlyDictionary<MatrixKey, double> matrix)
    {
        var found = new List<DataOnlyPair>();
        foreach (var lift in store.MatchLift.Values)
        {
            if (!Relations.TryParse(lift.Relation, out var relation)
                || !store.Items.TryGetValue(lift.ItemId, out var item) || !store.Heroes.TryGetValue(lift.HeroId, out var hero))
                continue;
            var bar = relation == Relation.Against ? ItemScoring.PickMinAgainst : ItemScoring.PickMinAs;
            var weight = matrix.GetValueOrDefault(new MatrixKey(lift.ItemId, lift.HeroId, relation));
            if (lift.LiftShrunk < bar || weight > 0)
                continue;
            found.Add(new DataOnlyPair(item.ItemName, hero.HeroName, relation, lift.LiftShrunk, weight, lift.LiftShrunk / bar));
        }
        return found.OrderByDescending(pair => pair.Margin).Take(DataOnlyLimit).ToList();
    }

    private static List<string> NamesOf(DataStore store, IEnumerable<string> itemIds) =>
        itemIds.Select(itemId => store.Items[itemId].ItemName).ToList();
}
