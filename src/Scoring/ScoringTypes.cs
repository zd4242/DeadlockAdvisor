using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Scoring;

public readonly record struct MatrixKey(string ItemId, string HeroId, Relation Relation);

/// <summary>One recommendation: an item and its summed hand-model score.</summary>
/// <param name="Data">
/// Relation key ("against" / "as") → summed match-data lift; see <see cref="ItemScoring.DataScores"/>.
/// Shown beside the score, never folded into it.
/// </param>
public sealed record ScoredItem(
    string ItemId,
    string ItemName,
    int Tier,
    double Score,
    string ShopCategory,
    OrderedDictionary<string, double> Data)
{
    /// <summary>See <see cref="ItemScoring.DataStrength"/>.</summary>
    public double DataStrength { get; } = ItemScoring.DataStrength(Data);
}

/// <summary>One trait's share of one hero's contribution to an item's score.</summary>
/// <param name="Coefficient">The hand-typed part only.</param>
/// <param name="Baseline">The roster's average on the trait: only how far the hero sits from it counts.</param>
public sealed record TraitPart(
    string CategoryId,
    string CategoryName,
    double HeroScore,
    double Coefficient,
    IReadOnlyList<StatPart> StatParts,
    double Weight = 1.0,
    double Baseline = 0.0)
{
    public double FromStats => DataStore.SumAmounts(StatParts);
    public double EffectiveCoefficient => Weight * (Coefficient + FromStats);
    public double Deviation => HeroScore - Baseline;
    public double Amount => Deviation * EffectiveCoefficient;
}

/// <summary>One hero's share of an item's score, trait by trait.</summary>
/// <param name="Amount">The traits' sum times the hero's net worth factor.</param>
/// <param name="NetWorth">Where the hero stood, when their net worth weighted the score.</param>
public sealed record HeroContribution(
    string HeroId,
    string HeroName,
    Relation Relation,
    double Amount,
    IReadOnlyList<TraitPart> Parts,
    NetWorthStanding? NetWorth = null)
{
    public double Factor => NetWorth?.Factor ?? 1.0;
}

/// <summary>The heroes one score is summed over, each with the relation they're counted on and their net worth factor.</summary>
public sealed record LineUp(IReadOnlyList<string> Allies, IReadOnlyList<string> Enemies, string? Self, NetWorthWeights NetWorth)
{
    /// <summary>Enemies, then allies, then you.</summary>
    public IEnumerable<(string HeroId, Relation Relation)> Members()
    {
        foreach (var heroId in Enemies)
            yield return (heroId, Relation.Against);
        foreach (var heroId in Allies)
            yield return (heroId, Relation.With);
        if (Self is not null)
            yield return (Self, Relation.As);
    }
}
