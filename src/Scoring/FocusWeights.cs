using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// Leans scores toward the enemies you've focused on: each focused enemy's whole term in an item's score
/// counts <see cref="Ratio"/> times an unfocused enemy's, and the factors are scaled so the enemies' still
/// add up to how many there are. Focus moves weight between the enemies rather than adding any, so the
/// enemy side weighs what it did against you and your allies, and an item that's as good against every
/// enemy gains nothing from it.
/// </summary>
public sealed class FocusWeights
{
    /// <summary>How many times an unfocused enemy a focused one counts: one focused in a full match is half the enemy side.</summary>
    public const double Ratio = 5;

    public static readonly FocusWeights None = new(new Dictionary<string, double>(), new HashSet<string>(), 1.0);

    private readonly IReadOnlyDictionary<string, double> _factors;
    private readonly IReadOnlySet<string> _focused;

    private FocusWeights(IReadOnlyDictionary<string, double> factors, IReadOnlySet<string> focused, double unfocused)
    {
        _factors = factors;
        _focused = focused;
        Unfocused = unfocused;
    }

    /// <summary>How many enemies are focused.</summary>
    public int Count => _focused.Count;

    /// <summary>The factor on an enemy who isn't focused; a focused one's is <see cref="Ratio"/> times it.</summary>
    public double Unfocused { get; }

    public bool IsEmpty => Count == 0;

    public bool IsFocused(string heroId) => _focused.Contains(heroId);

    /// <summary>The factor on a hero's share: 1 for allies, you, and every enemy when none is focused.</summary>
    public double Factor(string heroId) => _factors.GetValueOrDefault(heroId, 1.0);

    /// <summary>
    /// The factors for <paramref name="enemies"/> with those of <paramref name="focused"/> on the team focused.
    /// Focusing nobody, or everybody, changes nothing.
    /// </summary>
    public static FocusWeights For(IReadOnlyCollection<string> enemies, IEnumerable<string> focused)
    {
        var onTeam = focused.Where(enemies.Contains).ToHashSet();
        var (count, total) = (onTeam.Count, enemies.Count);
        if (count == 0 || count == total)
            return None;
        var unfocused = total / (Ratio * count + total - count);
        return new FocusWeights(enemies.ToDictionary(heroId => heroId, heroId => onTeam.Contains(heroId) ? Ratio * unfocused : unfocused), onTeam,
            unfocused);
    }

    /// <summary>
    /// What a typical team's <see cref="BestTargets"/> sum comes to on <paramref name="relation"/> with this
    /// focus: the same focused share of a team of <paramref name="count"/>, so a focused line-up is measured
    /// against focused ones. Only enemies are focused.
    /// </summary>
    /// <param name="typical">The typical sum for a team of this many with that many focused, counting ×<see cref="Ratio"/> against ×1.</param>
    public double Typical(Relation relation, IEnumerable<string> team, Func<int, double> typical) =>
        relation != Relation.Against || IsEmpty ? typical(0) : Unfocused * typical(team.Count(IsFocused));
}
