using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Explain;

public sealed record TraitLine(string TraitName, string? Source, string? SourceTip, string Arithmetic, DisplayAmount Share);

/// <param name="Note">Beside the name: the hero's best-target rank and net worth standing, when they scale its share.</param>
/// <param name="Info">Behind an info badge: a plain explanation of a line that isn't a hero, such as the typical team.</param>
public sealed record ContributionCard(
    string HeroName,
    string RelationText,
    Color RelationColor,
    DisplayAmount Amount,
    IReadOnlyList<TraitLine> Traits,
    string? Note = null,
    string? NoteTip = null,
    string? Info = null);

public sealed record DataLine(string HeroName, string RelationText, Color RelationColor, string Detail, DisplayAmount Share);

/// <summary>"enemies ▲1.3": one relation's summed lift.</summary>
public sealed record DataTotal(string Word, Color Color, DisplayAmount Value)
{
    public IBrush Brush => new SolidColorBrush(Color);
}

/// <summary>"Formula ▲1.6 · data ▲0.7 → ▲2.3": each opinion's part in the formula-and-data ranking, and their sum.</summary>
/// <param name="Formula">Null when no rule of the item's applies to the line-up.</param>
/// <param name="Data">Null when the match data has nothing on the item for these heroes.</param>
public sealed record BlendVerdict(double? Formula, double? Data, double Total);

/// <param name="Source">Beneath the lines: which matches the data comes from and how old it is.</param>
/// <param name="Info">Behind the title's info badge: what the numbers measure and how to read a line.</param>
/// <param name="Relevance">Why the enemy lifts count for less, when your hero rarely builds the item.</param>
public sealed record MatchDataCard(
    IReadOnlyList<DataTotal> Totals,
    IReadOnlyList<DataLine> Lines,
    string Source,
    string? Info,
    string? Relevance = null);

/// <summary>
/// "Why this item?": the same arithmetic scoring did, spelled out one hero and one trait at a time,
/// plus the match data's view of the item.
/// </summary>
public class ExplainViewModel : ViewModelBase
{
    public const string IdleTitle = "Select an item to see why it's recommended";

    public const string IdleHint =
        "Click a result to see which heroes and traits produced its score, and what real matches say about it. "
        + "Click it again to come back here.";

    [Reactive] public bool HasItem { get; private set; }
    [Reactive] public string? ItemId { get; private set; }
    [Reactive] public string ItemName { get; private set; } = IdleTitle;
    [Reactive] public Color ShopColor { get; private set; }
    [Reactive] public DisplayAmount Total { get; private set; }
    [Reactive] public bool NoContributions { get; private set; }
    [Reactive] public IReadOnlyList<ContributionCard> Contributions { get; private set; } = [];
    [Reactive] public MatchDataCard? MatchData { get; private set; }

    /// <summary>Ranking by formula and data together: each one's part in the item's rank, and their sum.</summary>
    [Reactive] public BlendVerdict? Verdict { get; private set; }

    /// <param name="blend">The formula-and-data ranking's units, when the list is ranked that way.</param>
    public void ShowItem(DataStore store, MatchState match, string? itemId, double now, NetWorthWeights? netWorth = null,
        BlendScale? blend = null)
    {
        if (itemId is null || !store.Items.TryGetValue(itemId, out var item))
        {
            ShowIdle();
            return;
        }

        var contributions = ItemScoring.ExplainItem(store, match, itemId, netWorth);
        var total = 0.0;
        foreach (var contribution in contributions)
            total += contribution.Amount;

        HasItem = true;
        ItemId = itemId;
        ItemName = item.ItemName;
        ShopColor = Palette.ShopColor(item.Category);
        Total = new DisplayAmount(total);
        NoContributions = contributions.Count == 0;
        Contributions = contributions.Select(contribution => Card(contribution, TypicalInfo(item.ItemName, contribution, contributions))).ToList();
        var parts = ItemScoring.DataParts(store, match, itemId);
        var self = match.SelfHero;
        MatchData = parts.Count > 0 ? DataCard(store, parts, now, self, ItemScoring.BuildRatio(store, itemId, self)) : null;
        Verdict = blend is { } scale
            ? ExplainText.Verdict(new ScoredItem(itemId, item.ItemName, item.Tier, total, item.Category,
                ItemScoring.DataScores(store, match, itemId)), scale, NoContributions)
            : null;
    }

    private void ShowIdle()
    {
        HasItem = false;
        ItemId = null;
        ItemName = IdleTitle;
        Total = default;
        NoContributions = false;
        Contributions = [];
        MatchData = null;
        Verdict = null;
    }

    /// <summary>The typical team's explanation, against what the same relation's ranked heroes come to; null for a hero.</summary>
    private static string? TypicalInfo(string itemName, HeroContribution contribution, IReadOnlyList<HeroContribution> contributions)
    {
        if (contribution.TypicalOf is not { } count)
            return null;
        var targets = contributions
            .Where(other => other.Relation == contribution.Relation && other.Rank is not null)
            .Sum(other => other.Amount);
        return ExplainText.TypicalInfo(itemName, contribution.Relation, count, -contribution.Amount, targets);
    }

    private static ContributionCard Card(HeroContribution contribution, string? info)
    {
        var notes = new[] { ExplainText.Rank(contribution.Rank), ExplainText.NetWorth(contribution.NetWorth) }.OfType<string>().ToList();
        if (contribution.TypicalOf is { } count)
            notes.Add(ExplainText.Typical(count));
        var tips = new List<string>();
        if (contribution.Rank is not null)
            tips.Add(ExplainText.BestTargetsTip);
        if (contribution.NetWorth is { Factor: not 1.0 } standing)
            tips.Add(ExplainText.NetWorthTooltip(standing));

        return new ContributionCard(
            contribution.HeroName,
            ExplainText.RelationWord(contribution.Relation).ToUpperInvariant(),
            Palette.RelationColor(contribution.Relation),
            new DisplayAmount(contribution.Amount),
            contribution.Parts.Select(part => new TraitLine(
                part.CategoryName,
                ExplainText.CoefficientSource(part),
                ExplainText.CoefficientSource(part) is null ? null : ExplainText.CoefficientTooltip(part),
                ExplainText.Arithmetic(part),
                new DisplayAmount(part.Amount))).ToList(),
            notes.Count > 0 ? string.Join(" · ", notes) : null,
            tips.Count > 0 ? string.Join("\n\n", tips) : null,
            info);
    }

    /// <summary>
    /// One line per hero with match data for this item, then where the numbers come from. The enemies
    /// total counts for less when your hero rarely builds the item, as it does in the list.
    /// </summary>
    private static MatchDataCard DataCard(DataStore store, IReadOnlyList<MatchLift> parts, double now, string? self, double? buildRatio)
    {
        var relevance = ItemScoring.Relevance(buildRatio);
        var totals = new List<DataTotal>();
        foreach (var relation in new[] { Relation.Against, Relation.As })
        {
            var key = relation.Key();
            if (!parts.Any(part => part.Relation == key))
                continue;
            var sum = 0.0;
            foreach (var part in parts.Where(part => part.Relation == key))
                sum += part.LiftShrunk;
            if (relation == Relation.Against)
                sum *= relevance;
            totals.Add(new DataTotal(ExplainText.DataWord(key), Palette.RelationColor(relation), new DisplayAmount(sum)));
        }
        string? relevanceNote = null;
        if (buildRatio is { } ratio && relevance < 1 && parts.Any(part => part.Relation == Relation.Against.Key()))
            relevanceNote = ExplainText.RarelyBuilt(self is not null && store.Heroes.TryGetValue(self, out var selfHero) ? selfHero.HeroName : "Your hero", ratio);

        var lines = parts.OrderBy(part => -part.LiftShrunk).Select(part =>
        {
            var relation = Relations.TryParse(part.Relation, out var parsed) ? parsed : Relation.As;
            var heroName = store.Heroes.TryGetValue(part.HeroId, out var hero) ? hero.HeroName : part.HeroId;
            return new DataLine(
                heroName,
                ExplainText.RelationWord(relation).ToUpperInvariant(),
                Palette.RelationColor(relation),
                $"raw {Format.SignedFixed(part.Lift, 2)} ± {NumberFormat.Fixed(part.Se, 2)} · {Format.Compact(part.Matches)} matches",
                new DisplayAmount(part.LiftShrunk));
        }).ToList();

        var meaning = MatchStatsMath.DataMeaning(store.MatchMeta);
        return new MatchDataCard(
            totals,
            lines,
            MatchStatsMath.DataSource(store.MatchMeta, now),
            meaning.Length > 0 ? meaning + "\n\n" + ExplainText.DataLinesTip : null,
            relevanceNote);
    }
}
