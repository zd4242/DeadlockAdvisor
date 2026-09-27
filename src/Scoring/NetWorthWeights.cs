using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Scoring;

/// <summary>Where a hero stood at their latest net worth reading, and the factor their term in a score gets for it.</summary>
/// <param name="Average">The match's average over everyone in the same reading.</param>
public sealed record NetWorthStanding(int Souls, double Average, double Factor);

/// <summary>
/// Leans scores toward whoever is ahead: each hero's whole term in an item's score is multiplied by
/// <c>clamp(1 + Strength × (souls / average − 1), 1 − MaxShift, 1 + MaxShift)</c>. Multiplying rather
/// than adding keeps it a reweighting of the heroes, never a bonus of its own: a counter to a fed
/// enemy gains, and an item that's poor against them loses by the same proportion.
/// </summary>
public sealed class NetWorthWeights
{
    public const double Strength = 0.5;
    public const double MaxShift = 0.3;

    /// <summary>Below this average everyone is still on their starting souls, and the gaps are noise.</summary>
    public const double MinAverage = 500;

    public static readonly NetWorthWeights None = new(new Dictionary<string, NetWorthStanding>());

    private readonly IReadOnlyDictionary<string, NetWorthStanding> _standings;

    private NetWorthWeights(IReadOnlyDictionary<string, NetWorthStanding> standings) => _standings = standings;

    public NetWorthStanding? StandingOf(string heroId) => _standings.GetValueOrDefault(heroId);

    /// <summary>1 for a hero without a reading.</summary>
    public double Factor(string heroId) => _standings.TryGetValue(heroId, out var standing) ? standing.Factor : 1.0;

    public static double FactorFor(double souls, double average) =>
        average < MinAverage ? 1.0 : Math.Clamp(1 + Strength * (souls / average - 1), 1 - MaxShift, 1 + MaxShift);

    /// <summary>
    /// Each hero in the match against the average of their own latest reading. A reading can miss
    /// heroes whose pills were hidden or a side whose pills didn't add up, and comparing one hero's
    /// older value with another's newer one would make whoever was read last look ahead.
    /// </summary>
    public static NetWorthWeights For(MatchState match)
    {
        var inMatch = match.RoleMap.Where(entry => entry.Value != Role.None).Select(entry => entry.Key).ToHashSet();
        var standings = new Dictionary<string, NetWorthStanding>();
        foreach (var snapshot in match.NetWorth.Snapshots.Reverse())
        {
            var read = snapshot.Souls.Where(entry => inMatch.Contains(entry.Key) && !standings.ContainsKey(entry.Key)).ToList();
            if (read.Count == 0)
                continue;
            var average = snapshot.Souls.Where(entry => inMatch.Contains(entry.Key)).Average(entry => (double)entry.Value);
            foreach (var (heroId, souls) in read)
                standings[heroId] = new NetWorthStanding(souls, average, FactorFor(souls, average));
        }
        return standings.Count == 0 ? None : new NetWorthWeights(standings);
    }
}
