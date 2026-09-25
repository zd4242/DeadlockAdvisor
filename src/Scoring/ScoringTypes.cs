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
    OrderedDictionary<string, double> Data);

/// <summary>One trait's share of one hero's contribution to an item's score.</summary>
/// <param name="Coefficient">The hand-typed part only.</param>
public sealed record TraitPart(
    string CategoryId,
    string CategoryName,
    double HeroScore,
    double Coefficient,
    IReadOnlyList<StatPart> StatParts,
    double Weight = 1.0)
{
    public double FromStats => DataStore.SumAmounts(StatParts);
    public double EffectiveCoefficient => Weight * (Coefficient + FromStats);
    public double Amount => HeroScore * EffectiveCoefficient;
}

/// <summary>One hero's share of an item's score, trait by trait.</summary>
public sealed record HeroContribution(
    string HeroId,
    string HeroName,
    Relation Relation,
    double Amount,
    IReadOnlyList<TraitPart> Parts);
