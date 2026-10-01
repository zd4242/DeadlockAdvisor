using System.Diagnostics.CodeAnalysis;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// weight(item, hero, relation) for every combination a nonzero coefficient reaches
/// (<see cref="ItemScoring.BuildWeightMatrix"/>), plus what <see cref="BestTargets"/> scoring needs: each
/// weight split into the part that sums over the team and the part from best-target lines, the profiled
/// heroes, and each best-target part's typical sum per team size, worked out up front so scoring a
/// line-up is only lookups.
/// </summary>
public sealed class WeightMatrix : IReadOnlyDictionary<MatrixKey, double>
{
    public static readonly WeightMatrix Empty = new([], [], []);

    private readonly Dictionary<MatrixKey, double> _weights;
    private readonly Dictionary<MatrixKey, double> _summed;
    private readonly Dictionary<MatrixKey, double> _ranked;
    private readonly HashSet<string> _profiled;
    private readonly Dictionary<(string ItemId, Relation Relation), double[]> _typical = [];

    /// <param name="summed">The weights from lines that sum over the team.</param>
    /// <param name="ranked">The weights from lines scored on their best targets (<see cref="Services.DataStore.OnBestTargets"/>).</param>
    public WeightMatrix(Dictionary<MatrixKey, double> summed, Dictionary<MatrixKey, double> ranked, IReadOnlyCollection<string> profiled)
    {
        _summed = summed;
        _ranked = ranked;
        _weights = new Dictionary<MatrixKey, double>(summed);
        foreach (var (key, weight) in ranked)
            _weights[key] = _weights.GetValueOrDefault(key) + weight;
        _profiled = profiled.ToHashSet();
        foreach (var (itemId, relation) in ranked.Keys.Select(key => (key.ItemId, key.Relation)).Distinct())
        {
            var roster = profiled.Select(heroId => ranked.GetValueOrDefault(new MatrixKey(itemId, heroId, relation))).ToList();
            // One hero's typical value is the roster's average weight, which is 0 by construction.
            var typical = new double[Math.Min(BestTargets.MaxTeam, roster.Count) + 1];
            for (var count = 2; count < typical.Length; count++)
                typical[count] = BestTargets.Expected(roster, count);
            _typical[(itemId, relation)] = typical;
        }
    }

    public bool IsProfiled(string heroId) => _profiled.Contains(heroId);

    /// <summary>Whether some of this item's weight on this relation goes through <see cref="BestTargets"/> rather than a plain sum.</summary>
    public bool OnBestTargets(string itemId, Relation relation) => _typical.ContainsKey((itemId, relation));

    /// <summary>The part of the weight that sums over the team.</summary>
    public double Summed(MatrixKey key) => _summed.GetValueOrDefault(key);

    /// <summary>The part of the weight that counts by <see cref="BestTargets"/>.</summary>
    public double Ranked(MatrixKey key) => _ranked.GetValueOrDefault(key);

    /// <summary><see cref="BestTargets.Sum"/> of the best-target parts on one relation for a team of this many profiled heroes on average; 0 for no heroes.</summary>
    public double Typical(string itemId, Relation relation, int count) =>
        _typical.TryGetValue((itemId, relation), out var typical) && count > 0 ? typical[Math.Min(count, typical.Length - 1)] : 0.0;

    public double this[MatrixKey key] => _weights[key];
    public IEnumerable<MatrixKey> Keys => _weights.Keys;
    public IEnumerable<double> Values => _weights.Values;
    public int Count => _weights.Count;
    public bool ContainsKey(MatrixKey key) => _weights.ContainsKey(key);
    public bool TryGetValue(MatrixKey key, [MaybeNullWhen(false)] out double value) => _weights.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<MatrixKey, double>> GetEnumerator() => _weights.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
