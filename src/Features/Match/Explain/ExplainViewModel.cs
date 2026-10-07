using System.Reactive;
using System.Reactive.Subjects;
using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.HeroTraits;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Explain;

/// <param name="IsSole">The hero's only line, so without the math the card's amount stands for it.</param>
/// <param name="Cell">The Hero Traits cell the line was worked from; null for a line that isn't a hero's, such as the typical team.</param>
public sealed record TraitLine(string TraitName, string? Source, string? SourceTip, string Arithmetic, DisplayAmount Share, bool IsSole = false,
    TraitCell? Cell = null)
{
    /// <summary>The arithmetic and where its coefficient came from, for hovering the line when the math is hidden.</summary>
    public string Working => $"{Arithmetic} = {Format.SignedFixed(Share.Value, 1)}" + (SourceTip is null ? "" : $"\n\n{SourceTip}");
}

/// <param name="HeroId">The portrait beside the name; null for a line that isn't a hero, such as the typical team.</param>
/// <param name="Note">Beside the name: the hero's best-target rank and net worth standing, when they scale its share.</param>
/// <param name="Info">Behind an info badge: a plain explanation of a line that isn't a hero, such as the typical team.</param>
public sealed record ContributionCard(
    string? HeroId,
    string HeroName,
    string RelationText,
    Color RelationColor,
    DisplayAmount Amount,
    IReadOnlyList<TraitLine> Traits,
    string? Note = null,
    string? NoteTip = null,
    string? Info = null)
{
    /// <summary>The note and what it means, for hovering the card's title when the math is hidden.</summary>
    public string? NoteWithTip => Note is null ? null : NoteTip is null ? Note : $"{Note}\n\n{NoteTip}";
}

public sealed record DataLine(string HeroId, string HeroName, string RelationText, Color RelationColor, string Detail, DisplayAmount Share);

/// <summary>"enemies ▲1.3": one relation's summed lift.</summary>
public sealed record DataTotal(string Word, Color Color, DisplayAmount Value)
{
    public IBrush Brush => new SolidColorBrush(Color);
}

/// <summary>"Formula ▲1.6 · data ▲0.7": each opinion's part in the formula-and-data ranking, and their sum.</summary>
/// <param name="Formula">Null when no rule of the item's applies to the line-up.</param>
/// <param name="Data">Null when the match data has nothing on the item for these heroes.</param>
public sealed record BlendVerdict(double? Formula, double? Data, double Total);

/// <param name="Info">
/// Behind the title's info badge: what the numbers measure, how to read a line, and which matches the
/// data comes from and how old it is.
/// </param>
/// <param name="Relevance">Why the enemy lifts count for less, when your hero rarely builds the item.</param>
public sealed record MatchDataCard(
    IReadOnlyList<DataTotal> Totals,
    IReadOnlyList<DataLine> Lines,
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

    /// <summary>The number the list shows for the item: whatever it's ranked by.</summary>
    [Reactive] public DisplayAmount Headline { get; private set; }

    /// <summary>What the hero cards add up to, the formula's score.</summary>
    [Reactive] public DisplayAmount FormulaTotal { get; private set; }

    /// <summary>The headline isn't the formula's score, so the cards get a total of their own.</summary>
    [Reactive] public bool ShowsFormulaTotal { get; private set; }
    [Reactive] public string? FormulaTotalTip { get; private set; }

    /// <summary>
    /// Ranked by the formula and the data together, how the cards' total in points becomes the verdict's
    /// formula part, so the two numbers read as one; null otherwise.
    /// </summary>
    [Reactive] public string? FormulaConversion { get; private set; }

    [Reactive] public bool NoContributions { get; private set; }
    [Reactive] public IReadOnlyList<ContributionCard> Contributions { get; private set; } = [];
    [Reactive] public MatchDataCard? MatchData { get; private set; }

    /// <summary>Ranked by the match data alone, so its card leads and the formula's follow.</summary>
    [Reactive] public bool MatchDataFirst { get; private set; }

    /// <summary>Ranking by formula and data together: each one's part in the item's rank, and their sum.</summary>
    [Reactive] public BlendVerdict? Verdict { get; private set; }

    /// <summary>The header's context menu asked to open the item's rules on the Item Formulas page.</summary>
    public IObservable<string> FormulaRequested => _formulaRequested;
    private readonly Subject<string> _formulaRequested = new();

    public ReactiveCommand<string, Unit> OpenFormulaCommand { get; }

    /// <summary>A trait line's context menu asked to select the hero's trait on the Hero Traits page.</summary>
    public IObservable<TraitCell> TraitRequested => _traitRequested;
    private readonly Subject<TraitCell> _traitRequested = new();

    public ReactiveCommand<TraitCell, Unit> OpenTraitCommand { get; }

    /// <summary>The model editors are shown, so the header and the trait lines offer a way to their pages on right-click.</summary>
    [Reactive] public bool ShowsEditors { get; set; }

    /// <summary>
    /// Each line's arithmetic, where its coefficient came from, and what scaled a hero's share. Without
    /// it, the cards say who counts and how much, and the rest shows on hover.
    /// </summary>
    [Reactive] public bool ShowsMath { get; set; } = true;

    public ExplainViewModel()
    {
        OpenFormulaCommand = ReactiveCommand.Create<string>(_formulaRequested.OnNext, this.WhenAnyValue(vm => vm.ShowsEditors));
        OpenTraitCommand = ReactiveCommand.Create<TraitCell>(_traitRequested.OnNext, this.WhenAnyValue(vm => vm.ShowsEditors));
    }

    /// <param name="rankBy">What the list is ranked by, which the headline shows.</param>
    /// <param name="blend">The formula-and-data ranking's units, when the list is ranked that way.</param>
    public void ShowItem(DataStore store, MatchState match, string? itemId, double now, NetWorthWeights? netWorth = null,
        RankBy rankBy = RankBy.Formula, BlendScale? blend = null)
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
        NoContributions = contributions.Count == 0;
        Contributions = contributions.Select(contribution => Card(contribution, TypicalInfo(store, item, contribution, contributions))).ToList();
        var lineUp = ItemScoring.RelevantHeroes(match);
        var parts = ItemScoring.DataParts(store, lineUp, itemId);
        var self = match.SelfHero;
        MatchData = parts.Count > 0 ? DataCard(store, parts, now, self, ItemScoring.BuildRatio(store, itemId, self), lineUp.Focus) : null;
        MatchDataFirst = rankBy == RankBy.MatchData;

        var scored = new ScoredItem(itemId, item.ItemName, item.Tier, total, item.Category, ItemScoring.DataScores(store, match, itemId));
        Verdict = rankBy == RankBy.Both && blend is { } scale ? ExplainText.Verdict(scored, scale, NoContributions) : null;
        // Ranked by more than the formula, the headline is another number, so the cards get a total of their own.
        var (headline, formulaTip) = rankBy switch
        {
            RankBy.MatchData => (scored.DataStrength, ExplainText.FormulaTotalByDataTip),
            RankBy.Both when Verdict is { Formula: { } part } verdict => (verdict.Total, ExplainText.FormulaTotalInBlendTip(part)),
            RankBy.Both when Verdict is { } verdict => (verdict.Total, null),
            _ => (total, null),
        };
        Headline = new DisplayAmount(headline);
        FormulaTotal = new DisplayAmount(total);
        FormulaTotalTip = NoContributions ? null : formulaTip;
        ShowsFormulaTotal = FormulaTotalTip is not null;
        FormulaConversion = ShowsFormulaTotal && blend is { } units && Verdict is { Formula: { } inUnits }
            ? ExplainText.FormulaConversion(total, units.Formula, inUnits)
            : null;
    }

    private void ShowIdle()
    {
        HasItem = false;
        ItemId = null;
        ItemName = IdleTitle;
        Headline = default;
        FormulaTotal = default;
        ShowsFormulaTotal = false;
        FormulaTotalTip = null;
        FormulaConversion = null;
        NoContributions = false;
        Contributions = [];
        MatchData = null;
        MatchDataFirst = false;
        Verdict = null;
    }

    /// <summary>The typical team's explanation, against what the same relation's ranked heroes come to; null for a hero.</summary>
    private static string? TypicalInfo(DataStore store, Item item, HeroContribution contribution, IReadOnlyList<HeroContribution> contributions)
    {
        if (contribution.TypicalOf is not { } count)
            return null;
        var team = contributions.Where(other => other.Relation == contribution.Relation).ToList();
        var targets = team.Where(other => other.Rank is not null).Sum(other => other.RankedAmount);
        return ExplainText.TypicalInfo(item.ItemName, contribution.Relation, store.CastOnCovers(item.ItemId, contribution.Relation),
            count, -contribution.Amount, targets, team.Any(other => other.Focus != 1.0));
    }

    private static ContributionCard Card(HeroContribution contribution, string? info)
    {
        var notes = new[]
        {
            ExplainText.Rank(contribution.Rank),
            ExplainText.Focus(contribution.Focus, contribution.IsFocused),
            ExplainText.NetWorth(contribution.NetWorth),
        }.OfType<string>().ToList();
        if (contribution.TypicalOf is { } count)
            notes.Add(ExplainText.Typical(count));
        var tips = new List<string>();
        if (contribution.Rank is not null)
            tips.Add(contribution.PartlyRanked ? $"{ExplainText.BestTargetsTip}\n{ExplainText.PartlyRankedTip}" : ExplainText.BestTargetsTip);
        if (contribution.Focus != 1.0)
            tips.Add(ExplainText.FocusTooltip(contribution.HeroName, contribution.Focus, contribution.IsFocused));
        if (contribution.NetWorth is { Factor: not 1.0 } standing)
            tips.Add(ExplainText.NetWorthTooltip(standing));

        return new ContributionCard(
            contribution.TypicalOf is null ? contribution.HeroId : null,
            contribution.HeroName,
            ExplainText.RelationWord(contribution.Relation).ToUpperInvariant(),
            Palette.RelationColor(contribution.Relation),
            new DisplayAmount(contribution.Amount),
            contribution.Parts.Select(part => new TraitLine(
                part.CategoryName,
                ExplainText.CoefficientSource(part),
                ExplainText.CoefficientSource(part) is null ? null : ExplainText.CoefficientTooltip(part),
                ExplainText.Arithmetic(part),
                new DisplayAmount(part.Share),
                IsSole: contribution.Parts.Count == 1,
                Cell: contribution.TypicalOf is null ? new TraitCell(contribution.HeroId, part.CategoryId) : null)).ToList(),
            notes.Count > 0 ? string.Join(" · ", notes) : null,
            tips.Count > 0 ? string.Join("\n\n", tips) : null,
            info);
    }

    /// <summary>
    /// One line per hero with match data for this item, then where the numbers come from. The enemies
    /// total counts for less when your hero rarely builds the item, as it does in the list, and each
    /// enemy's line counts by their focus factor.
    /// </summary>
    private static MatchDataCard DataCard(DataStore store, IReadOnlyList<MatchLift> parts, double now, string? self, double? buildRatio,
        FocusWeights focus)
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
                sum += focus.Factor(part.HeroId) * part.LiftShrunk;
            if (relation == Relation.Against)
                sum *= relevance;
            totals.Add(new DataTotal(ExplainText.DataWord(key), Palette.RelationColor(relation), new DisplayAmount(sum)));
        }
        string? relevanceNote = null;
        if (buildRatio is { } ratio && relevance < 1 && parts.Any(part => part.Relation == Relation.Against.Key()))
            relevanceNote = ExplainText.RarelyBuilt(self is not null && store.Heroes.TryGetValue(self, out var selfHero) ? selfHero.HeroName : "Your hero", ratio);

        var lines = parts.OrderBy(part => -focus.Factor(part.HeroId) * part.LiftShrunk).Select(part =>
        {
            var relation = Relations.TryParse(part.Relation, out var parsed) ? parsed : Relation.As;
            var heroName = store.Heroes.TryGetValue(part.HeroId, out var hero) ? hero.HeroName : part.HeroId;
            var factor = focus.Factor(part.HeroId);
            return new DataLine(
                part.HeroId,
                heroName,
                ExplainText.RelationWord(relation).ToUpperInvariant(),
                Palette.RelationColor(relation),
                $"raw {Format.SignedFixed(part.Lift, 2)} ± {NumberFormat.Fixed(part.Se, 2)} · {Format.Compact(part.Matches)} matches"
                + (Math.Abs(part.RankShift) >= RankLean.Visible ? $" · ranks {Format.SignedFixed(part.RankShift, 2)}" : "")
                + (ExplainText.Focus(factor, focus.IsFocused(part.HeroId)) is { } note ? $" · {note}" : ""),
                new DisplayAmount(factor * part.LiftShrunk));
        }).ToList();

        var meaning = MatchStatsMath.DataMeaning(store.MatchMeta);
        return new MatchDataCard(
            totals,
            lines,
            meaning.Length > 0 ? $"{meaning}\n\n{ExplainText.DataLinesTip}\n\n{MatchStatsMath.DataSource(store.MatchMeta, now)}" : null,
            relevanceNote);
    }
}
