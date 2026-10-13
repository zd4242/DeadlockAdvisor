using System.Collections.Concurrent;
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
    private readonly Dictionary<MatrixKey, double> _overlapSummed;
    private readonly Dictionary<MatrixKey, double> _overlapRanked;
    private readonly HashSet<string> _profiled;
    private readonly List<string> _profiledOrder;
    private readonly Dictionary<(string ItemId, Relation Relation), double[]> _typical = [];
    private readonly Dictionary<(string ItemId, Relation Relation), List<double>> _rosters = [];

    // Worked out when a focused line-up first asks, since most matches never focus.
    private readonly ConcurrentDictionary<(string ItemId, Relation Relation, int Count, int Focused), double> _focusedTypical = [];

    // Likewise for a team that already has some of the item's shared effect (TeamOverlap), whose typical sum is that of the weights left.
    private readonly ConcurrentDictionary<(string ItemId, Relation Relation, int Count, int Focused, double Coverage), double> _coveredTypical = [];

    /// <param name="summed">The weights from lines that sum over the team.</param>
    /// <param name="ranked">The weights from lines scored on their best targets (<see cref="Services.DataStore.OnBestTargets"/>).</param>
    /// <param name="overlapSummed">The part of <paramref name="summed"/> that is a shared effect (<see cref="OverlapWeights"/>), which the team's own copy discounts.</param>
    /// <param name="overlapRanked">The same for <paramref name="ranked"/>.</param>
    public WeightMatrix(Dictionary<MatrixKey, double> summed, Dictionary<MatrixKey, double> ranked, IReadOnlyCollection<string> profiled,
        Dictionary<MatrixKey, double>? overlapSummed = null, Dictionary<MatrixKey, double>? overlapRanked = null)
    {
        _summed = summed;
        _ranked = ranked;
        _overlapSummed = overlapSummed ?? [];
        _overlapRanked = overlapRanked ?? [];
        _weights = new Dictionary<MatrixKey, double>(summed);
        foreach (var (key, weight) in ranked)
            _weights[key] = _weights.GetValueOrDefault(key) + weight;
        _profiled = profiled.ToHashSet();
        _profiledOrder = [.. profiled];
        foreach (var (itemId, relation) in ranked.Keys.Select(key => (key.ItemId, key.Relation)).Distinct())
        {
            var roster = profiled.Select(heroId => ranked.GetValueOrDefault(new MatrixKey(itemId, heroId, relation))).ToList();
            // One hero's typical value is the roster's average weight, which is 0 by construction.
            var typical = new double[Math.Min(BestTargets.MaxTeam, roster.Count) + 1];
            for (var count = 2; count < typical.Length; count++)
                typical[count] = BestTargets.Expected(roster, count);
            _typical[(itemId, relation)] = typical;
            _rosters[(itemId, relation)] = roster;
        }
    }

    public bool IsProfiled(string heroId) => _profiled.Contains(heroId);

    /// <summary>Whether some of this item's weight on this relation goes through <see cref="BestTargets"/> rather than a plain sum.</summary>
    public bool OnBestTargets(string itemId, Relation relation) => _typical.ContainsKey((itemId, relation));

    /// <summary>The part of the weight that sums over the team.</summary>
    public double Summed(MatrixKey key) => _summed.GetValueOrDefault(key);

    /// <summary>The part of the weight that counts by <see cref="BestTargets"/>.</summary>
    public double Ranked(MatrixKey key) => _ranked.GetValueOrDefault(key);

    /// <summary>The part of <see cref="Summed"/> that is an effect other heroes carry.</summary>
    public double OverlapSummed(MatrixKey key) => _overlapSummed.GetValueOrDefault(key);

    /// <summary>The part of <see cref="Ranked"/> that is an effect other heroes carry.</summary>
    public double OverlapRanked(MatrixKey key) => _overlapRanked.GetValueOrDefault(key);

    /// <summary><see cref="BestTargets.Sum"/> of the best-target parts on one relation for a team of this many profiled heroes on average; 0 for no heroes.</summary>
    public double Typical(string itemId, Relation relation, int count) =>
        _typical.TryGetValue((itemId, relation), out var typical) && count > 0 ? typical[Math.Min(count, typical.Length - 1)] : 0.0;

    /// <summary>
    /// <see cref="Typical(string, Relation, int)"/> when <paramref name="focused"/> of the team count
    /// <see cref="FocusWeights.Ratio"/> times the rest, who count ×1.
    /// </summary>
    public double Typical(string itemId, Relation relation, int count, int focused)
    {
        if (focused <= 0 || !_typical.TryGetValue((itemId, relation), out var typical) || count <= 0)
            return Typical(itemId, relation, count);
        count = Math.Min(count, typical.Length - 1);
        focused = Math.Min(focused, count);
        return _focusedTypical.GetOrAdd((itemId, relation, count, focused),
            key => BestTargets.Expected(_rosters[(key.ItemId, key.Relation)], key.Count, key.Focused, FocusWeights.Ratio));
    }

    /// <summary>
    /// <see cref="Typical(string, Relation, int, int)"/> once the team has <paramref name="coverage"/> of the item's shared effect, which takes
    /// that share of <see cref="OverlapRanked"/> off every hero's best-target weight: the typical sum of what is left, so the item still
    /// averages 0 over random teams.
    /// </summary>
    public double Typical(string itemId, Relation relation, int count, int focused, double coverage)
    {
        if (coverage == 0 || !_typical.TryGetValue((itemId, relation), out var typical) || count <= 0)
            return Typical(itemId, relation, count, focused);
        count = Math.Min(count, typical.Length - 1);
        if (count < 2)
            return 0.0;
        focused = Math.Min(Math.Max(focused, 0), count);
        return _coveredTypical.GetOrAdd((itemId, relation, count, focused, coverage), key =>
        {
            var roster = _rosters[(key.ItemId, key.Relation)];
            var left = _profiledOrder.Select((heroId, index) =>
                roster[index] - key.Coverage * _overlapRanked.GetValueOrDefault(new MatrixKey(key.ItemId, heroId, key.Relation))).ToList();
            return key.Focused > 0 ? BestTargets.Expected(left, key.Count, key.Focused, FocusWeights.Ratio) : BestTargets.Expected(left, key.Count);
        });
    }

    public double this[MatrixKey key] => _weights[key];
    public IEnumerable<MatrixKey> Keys => _weights.Keys;
    public IEnumerable<double> Values => _weights.Values;
    public int Count => _weights.Count;
    public bool ContainsKey(MatrixKey key) => _weights.ContainsKey(key);
    public bool TryGetValue(MatrixKey key, [MaybeNullWhen(false)] out double value) => _weights.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<MatrixKey, double>> GetEnumerator() => _weights.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
