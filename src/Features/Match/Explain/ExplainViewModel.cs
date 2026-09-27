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

/// <param name="ShareText">The share without its sign: a ▲/▼ beside it carries that.</param>
public sealed record TraitLine(string TraitName, string? Source, string? SourceTip, string Arithmetic, double Share, string ShareText);

public sealed record ContributionCard(
    string HeroName,
    string RelationText,
    Color RelationColor,
    double Amount,
    string AmountText,
    IReadOnlyList<TraitLine> Traits,
    string? NetWorthText = null,
    string? NetWorthTip = null);

public sealed record DataLine(string HeroName, string RelationText, Color RelationColor, string Detail, double Share, string ShareText);

/// <summary>"enemies ▲1.25": one relation's summed lift.</summary>
public sealed record DataTotal(string Word, Color Color, double Value, string ValueText)
{
    public IBrush Brush => new SolidColorBrush(Color);
}

public sealed record MatchDataCard(IReadOnlyList<DataTotal> Totals, IReadOnlyList<DataLine> Lines, string Note);

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
    [Reactive] public double Total { get; private set; }
    [Reactive] public string TotalText { get; private set; } = "";
    [Reactive] public bool NoContributions { get; private set; }
    [Reactive] public IReadOnlyList<ContributionCard> Contributions { get; private set; } = [];
    [Reactive] public MatchDataCard? MatchData { get; private set; }

    public void ShowItem(DataStore store, MatchState match, string? itemId, IReadOnlyCollection<string>? restrictTo,
        double now, NetWorthWeights? netWorth = null)
    {
        if (itemId is null || !store.Items.TryGetValue(itemId, out var item))
        {
            ShowIdle();
            return;
        }

        var contributions = ItemScoring.ExplainItem(store, match, itemId, restrictTo, netWorth);
        var total = 0.0;
        foreach (var contribution in contributions)
            total += contribution.Amount;

        HasItem = true;
        ItemId = itemId;
        ItemName = item.ItemName;
        ShopColor = Palette.ShopColor(item.Category);
        Total = total;
        TotalText = Format.Num(Math.Abs(total));
        NoContributions = contributions.Count == 0;
        Contributions = contributions.Select(Card).ToList();
        var parts = ItemScoring.DataParts(store, match, itemId, restrictTo);
        MatchData = parts.Count > 0 ? DataCard(store, parts, now) : null;
    }

    private void ShowIdle()
    {
        HasItem = false;
        ItemId = null;
        ItemName = IdleTitle;
        Total = 0;
        TotalText = "";
        NoContributions = false;
        Contributions = [];
        MatchData = null;
    }

    private static ContributionCard Card(HeroContribution contribution) => new(
        contribution.HeroName,
        ExplainText.RelationWord(contribution.Relation).ToUpperInvariant(),
        Palette.RelationColor(contribution.Relation),
        NumberFormat.Round(contribution.Amount, 2),
        ExplainText.Magnitude(contribution.Amount),
        contribution.Parts.Select(part => new TraitLine(
            part.CategoryName,
            ExplainText.CoefficientSource(part),
            ExplainText.CoefficientSource(part) is null ? null : ExplainText.CoefficientTooltip(part),
            ExplainText.Arithmetic(part),
            NumberFormat.Round(part.Amount, 2),
            ExplainText.Magnitude(part.Amount))).ToList(),
        ExplainText.NetWorth(contribution.NetWorth),
        contribution.NetWorth is { Factor: not 1.0 } standing ? ExplainText.NetWorthTooltip(standing) : null);

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
            totals.Add(new DataTotal(ExplainText.DataWord(key), Palette.RelationColor(relation), NumberFormat.Round(sum, 2), NumberFormat.Fixed(Math.Abs(sum), 2)));
        }

        var lines = parts.OrderBy(part => -part.LiftShrunk).Select(part =>
        {
            var relation = Relations.TryParse(part.Relation, out var parsed) ? parsed : Relation.As;
            var heroName = store.Heroes.TryGetValue(part.HeroId, out var hero) ? hero.HeroName : part.HeroId;
            return new DataLine(
                heroName,
                ExplainText.RelationWord(relation).ToUpperInvariant(),
                Palette.RelationColor(relation),
                $"raw {Format.SignedFixed(part.Lift, 2)} ± {NumberFormat.Fixed(part.Se, 2)} · {Format.Compact(part.Matches)} matches",
                NumberFormat.Round(part.LiftShrunk, 2),
                $"{NumberFormat.Fixed(Math.Abs(part.LiftShrunk), 2)} pts");
        }).ToList();

        return new MatchDataCard(totals, lines, MatchStatsMath.DataNote(store.MatchMeta, now));
    }
}
