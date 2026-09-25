using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Theme;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Explain;

public sealed record TraitLine(string TraitName, string? Source, string? SourceTip, string Arithmetic, string Share);

public sealed record ContributionCard(
    string HeroName,
    string RelationText,
    Color RelationColor,
    string AmountText,
    IReadOnlyList<TraitLine> Traits);

public sealed record DataLine(string HeroName, string RelationText, Color RelationColor, string Detail, string Share);

public sealed record DataTotal(string Text, Color Color)
{
    public IBrush Brush => new SolidColorBrush(Color);
}

public sealed record MatchDataCard(IReadOnlyList<DataTotal> Totals, IReadOnlyList<DataLine> Lines, string Note);

public sealed record DataPick(
    string ItemId,
    string ItemName,
    Color ShopColor,
    string TierText,
    Color TierColor,
    OrderedDictionary<string, double> Data,
    string DataTip);

/// <summary>
/// "Why this item?": the same arithmetic scoring did, spelled out one hero and one trait at a time,
/// plus the match data's view of the item. With nothing selected, the items only the data likes.
/// </summary>
public class ExplainViewModel : ViewModelBase
{
    public const string IdleTitle = "Select an item to see why it's recommended";

    [Reactive] public bool HasItem { get; private set; }
    [Reactive] public string? ItemId { get; private set; }
    [Reactive] public string ItemName { get; private set; } = IdleTitle;
    [Reactive] public Color ShopColor { get; private set; }
    [Reactive] public string TotalText { get; private set; } = "";
    [Reactive] public bool NoContributions { get; private set; }
    [Reactive] public IReadOnlyList<ContributionCard> Contributions { get; private set; } = [];
    [Reactive] public MatchDataCard? MatchData { get; private set; }
    [Reactive] public IReadOnlyList<DataPick> Picks { get; private set; } = [];
    [Reactive] public bool HasPicks { get; private set; }

    public void ShowItem(DataStore store, MatchState match, string? itemId, IReadOnlyCollection<string>? restrictTo,
        IReadOnlyList<ScoredItem> picks, double now)
    {
        if (itemId is null || !store.Items.TryGetValue(itemId, out var item))
        {
            ShowIdle(store, picks, now);
            return;
        }

        var contributions = ItemScoring.ExplainItem(store, match, itemId, restrictTo);
        var total = 0.0;
        foreach (var contribution in contributions)
            total += contribution.Amount;

        HasItem = true;
        ItemId = itemId;
        ItemName = item.ItemName;
        ShopColor = Palette.ShopColor(item.Category);
        TotalText = Format.Num(total);
        NoContributions = contributions.Count == 0;
        Contributions = contributions.Select(Card).ToList();
        var parts = ItemScoring.DataParts(store, match, itemId, restrictTo);
        MatchData = parts.Count > 0 ? DataCard(store, parts, now) : null;
        Picks = [];
        HasPicks = false;
    }

    private void ShowIdle(DataStore store, IReadOnlyList<ScoredItem> picks, double now)
    {
        HasItem = false;
        ItemId = null;
        ItemName = IdleTitle;
        TotalText = "";
        NoContributions = false;
        Contributions = [];
        MatchData = null;
        var tip = MatchStatsMath.DataNote(store.MatchMeta, now);
        Picks = picks.Select(pick => new DataPick(
            pick.ItemId, pick.ItemName, Palette.ShopColor(pick.ShopCategory), $"T{pick.Tier}",
            Palette.TierColor(pick.Tier), pick.Data, tip)).ToList();
        HasPicks = Picks.Count > 0;
    }

    private static ContributionCard Card(HeroContribution contribution) => new(
        contribution.HeroName,
        ExplainText.RelationWord(contribution.Relation).ToUpperInvariant(),
        Palette.RelationColor(contribution.Relation),
        Format.Signed(contribution.Amount),
        contribution.Parts.Select(part => new TraitLine(
            part.CategoryName,
            ExplainText.CoefficientSource(part),
            ExplainText.CoefficientSource(part) is null ? null : ExplainText.CoefficientTooltip(part),
            ExplainText.Arithmetic(part),
            ExplainText.Share(part))).ToList());

    /// <summary>One line per hero with match data for this item, then where the numbers come from.</summary>
    private static MatchDataCard DataCard(DataStore store, IReadOnlyList<MatchLift> parts, double now)
    {
        var totals = new List<DataTotal>();
        foreach (var relation in new[] { Relation.Against, Relation.As })
        {
            var key = relation.Key();
            if (!parts.Any(part => part.Relation == key))
                continue;
            var sum = 0.0;
            foreach (var part in parts.Where(part => part.Relation == key))
                sum += part.LiftShrunk;
            totals.Add(new DataTotal($"{ExplainText.DataWord(key)} {Format.SignedFixed(sum, 2)}", Palette.RelationColor(relation)));
        }

        var lines = parts.OrderBy(part => -part.LiftShrunk).Select(part =>
        {
            var relation = Relations.TryParse(part.Relation, out var parsed) ? parsed : Relation.As;
            var heroName = store.Heroes.TryGetValue(part.HeroId, out var hero) ? hero.HeroName : part.HeroId;
            return new DataLine(
                heroName,
                ExplainText.RelationWord(relation).ToUpperInvariant(),
                Palette.RelationColor(relation),
                $"raw {Format.SignedFixed(part.Lift, 2)} ± {Services.Formats.NumberFormat.Fixed(part.Se, 2)} · {Format.Compact(part.Matches)} matches",
                $"{Format.SignedFixed(part.LiftShrunk, 2)} pts");
        }).ToList();

        return new MatchDataCard(totals, lines, MatchStatsMath.DataNote(store.MatchMeta, now));
    }
}
