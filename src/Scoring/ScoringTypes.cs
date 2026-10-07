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
/// <param name="BuildRatio">How often your hero builds it next to the average player; see <see cref="ItemScoring.BuildRatio"/>.</param>
/// <param name="Cost">In souls, as the shop prices it; 0 when unknown.</param>
public sealed record ScoredItem(
    string ItemId,
    string ItemName,
    int Tier,
    double Score,
    string ShopCategory,
    OrderedDictionary<string, double> Data,
    double? BuildRatio = null,
    int Cost = 0)
{
    /// <summary>See <see cref="ItemScoring.DataStrength"/>.</summary>
    public double DataStrength { get; } = ItemScoring.DataStrength(Data);

    /// <summary>Your hero builds it so rarely that its enemy lifts count for less (<see cref="ItemScoring.Relevance"/>).</summary>
    public bool RarelyBuilt => ItemScoring.Relevance(BuildRatio) < 1;
}

/// <summary>One trait's share of one hero's contribution to an item's score.</summary>
/// <param name="Coefficient">The hand-typed part only.</param>
/// <param name="Baseline">The roster's average on the trait: only how far the hero sits from it counts.</param>
/// <param name="Rank">On a best-target line, its hero's <see cref="BestTargets"/> rank on the team; null when the line sums.</param>
public sealed record TraitPart(
    string CategoryId,
    string CategoryName,
    double HeroScore,
    double Coefficient,
    IReadOnlyList<StatPart> StatParts,
    double Weight = 1.0,
    double Baseline = 0.0,
    int? Rank = null)
{
    public double FromStats => DataStore.SumAmounts(StatParts);
    public double EffectiveCoefficient => Weight * (Coefficient + FromStats);
    public double Deviation => HeroScore - Baseline;
    public double Amount => Deviation * EffectiveCoefficient;
    public double RankFactor => Rank is { } rank ? BestTargets.RankFactor(rank) : 1.0;

    /// <summary>What the line adds to its hero's share: <see cref="Amount"/> at its rank.</summary>
    public double Share => Amount * RankFactor;
}

/// <summary>One hero's share of an item's score, trait by trait.</summary>
/// <param name="Amount">The traits' <see cref="TraitPart.Share"/>s summed, times the hero's <see cref="Factor"/>.</param>
/// <param name="NetWorth">Where the hero stood, when their net worth weighted the score.</param>
/// <param name="Rank">When some of its lines count best targets: 1 for the best target on this relation, 2 for the next…; null when every line sums.</param>
/// <param name="TypicalOf">
/// Not a hero: what <see cref="BestTargets"/> gives a typical team of this many, taken off as one line
/// (<see cref="Amount"/> is its negative, <see cref="Parts"/> empty).
/// </param>
/// <param name="Focus">The enemy's <see cref="FocusWeights"/> factor; 1 when no enemy is focused.</param>
/// <param name="IsFocused">An enemy you focused on, rather than one counting for less because another is.</param>
public sealed record HeroContribution(
    string HeroId,
    string HeroName,
    Relation Relation,
    double Amount,
    IReadOnlyList<TraitPart> Parts,
    NetWorthStanding? NetWorth = null,
    int? Rank = null,
    int? TypicalOf = null,
    double Focus = 1.0,
    bool IsFocused = false)
{
    /// <summary>What the hero's summed lines are multiplied by: their net worth factor times their focus factor.</summary>
    public double Factor => (NetWorth?.Factor ?? 1.0) * Focus;

    /// <summary>The part of <see cref="Amount"/> from best-target lines, at the hero's rank.</summary>
    public double RankedAmount => Factor * Parts.Where(part => part.Rank is not null).Sum(part => part.Share);

    /// <summary>Some lines count at the hero's rank and others sum.</summary>
    public bool PartlyRanked => Rank is not null && Parts.Any(part => part.Rank is null);
}

/// <summary>
/// How many enemies and allies a line-up has, whether you're in it, and how many of the enemies are focused:
/// what the spread of its scores depends on.
/// </summary>
public readonly record struct LineUpShape(int Enemies, int Allies, bool Self, int Focused = 0)
{
    /// <summary>You, five allies and six enemies.</summary>
    public static readonly LineUpShape FullMatch = new(6, 5, true);

    public int Size => Enemies + Allies + (Self ? 1 : 0);

    public static LineUpShape Of(LineUp lineUp) => new(lineUp.Enemies.Count, lineUp.Allies.Count, lineUp.Self is not null, lineUp.Focus.Count);

    /// <summary>
    /// A random line-up of this shape with no net worth, from distinct heroes of <paramref name="pool"/>, which it
    /// shuffles in part (a partial Fisher-Yates): you first, then the allies, then the enemies, the first of whom are focused.
    /// </summary>
    public LineUp Draw(Random random, string[] pool)
    {
        for (var i = 0; i < Size; i++)
        {
            var j = random.Next(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var first = Self ? 1 : 0;
        var enemies = pool[(first + Allies)..Size];
        return new LineUp(pool[first..(first + Allies)], enemies, Self ? pool[0] : null, NetWorthWeights.None,
            FocusWeights.For(enemies, enemies.Take(Focused)));
    }
}

/// <summary>The heroes one score is summed over, each with the relation they're counted on and the factor their share is weighted by.</summary>
public sealed record LineUp(IReadOnlyList<string> Allies, IReadOnlyList<string> Enemies, string? Self, NetWorthWeights NetWorth, FocusWeights Focus)
{
    /// <summary>What a hero's share is multiplied by: their net worth factor times their focus factor.</summary>
    public double Factor(string heroId) => NetWorth.Factor(heroId) * Focus.Factor(heroId);

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
