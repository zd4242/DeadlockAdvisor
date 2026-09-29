using System.Diagnostics.CodeAnalysis;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// weight(item, hero, relation) for every combination a nonzero coefficient reaches
/// (<see cref="ItemScoring.BuildWeightMatrix"/>), plus what <see cref="BestTargets"/> scoring needs: the
/// single-target items, the profiled heroes, and each such item's typical best-target sum per team size,
/// worked out up front so scoring a line-up is only lookups.
/// </summary>
public sealed class WeightMatrix : IReadOnlyDictionary<MatrixKey, double>
{
    public static readonly WeightMatrix Empty = new([], [], new Dictionary<string, Relation>());

    private readonly Dictionary<MatrixKey, double> _weights;
    private readonly HashSet<string> _profiled;
    private readonly IReadOnlyDictionary<string, Relation> _castOn;
    private readonly Dictionary<string, double[]> _typical = [];

    /// <param name="castOn">Each single-target item's relation (<see cref="Models.Item.CastOn"/>).</param>
    public WeightMatrix(Dictionary<MatrixKey, double> weights, IReadOnlyCollection<string> profiled, IReadOnlyDictionary<string, Relation> castOn)
    {
        _weights = weights;
        _profiled = profiled.ToHashSet();
        _castOn = castOn;
        foreach (var (itemId, relation) in castOn)
        {
            var roster = profiled.Select(heroId => weights.GetValueOrDefault(new MatrixKey(itemId, heroId, relation))).ToList();
            // One hero's typical value is the roster's average weight, which is 0 by construction.
            var typical = new double[Math.Min(BestTargets.MaxTeam, roster.Count) + 1];
            for (var count = 2; count < typical.Length; count++)
                typical[count] = BestTargets.Expected(roster, count);
            _typical[itemId] = typical;
        }
    }

    public bool IsProfiled(string heroId) => _profiled.Contains(heroId);

    /// <summary>Whether this item's weights on this relation go through <see cref="BestTargets"/> rather than a plain sum.</summary>
    public bool OnBestTargets(string itemId, Relation relation) => _castOn.TryGetValue(itemId, out var castOn) && castOn == relation;

    /// <summary><see cref="BestTargets.Sum"/> on the item's cast-on relation for a team of this many profiled heroes on average; 0 for no heroes.</summary>
    public double Typical(string itemId, int count) =>
        _typical.TryGetValue(itemId, out var typical) && count > 0 ? typical[Math.Min(count, typical.Length - 1)] : 0.0;

    public double this[MatrixKey key] => _weights[key];
    public IEnumerable<MatrixKey> Keys => _weights.Keys;
    public IEnumerable<double> Values => _weights.Values;
    public int Count => _weights.Count;
    public bool ContainsKey(MatrixKey key) => _weights.ContainsKey(key);
    public bool TryGetValue(MatrixKey key, [MaybeNullWhen(false)] out double value) => _weights.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<MatrixKey, double>> GetEnumerator() => _weights.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
