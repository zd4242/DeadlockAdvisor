using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// The units the "Formula + data" ranking adds the two opinions in: how far a formula score and a data
/// strength each typically stray from 0 over random line-ups shaped like the one being scored. One unit
/// of either is then as unusual as one of the other, and an opinion with nothing to say adds 0.
/// </summary>
public readonly record struct BlendScale(double Formula, double Data)
{
    public static readonly BlendScale One = new(1, 1);

    public double FormulaUnits(ScoredItem item) => item.Score / Formula;
    public double DataUnits(ScoredItem item) => item.DataStrength / Data;
    public double Blend(ScoredItem item) => FormulaUnits(item) + DataUnits(item);

    /// <summary>Both have a clear opinion, at least a unit each, and they point opposite ways.</summary>
    public bool Disagree(ScoredItem item)
    {
        var (formula, data) = (FormulaUnits(item), DataUnits(item));
        return Math.Abs(formula) >= 1 && Math.Abs(data) >= 1 && Math.Sign(formula) != Math.Sign(data);
    }
}

/// <summary>
/// Measures a <see cref="BlendScale"/> once per line-up shape and view, for one version of the data:
/// the root mean square of every nonzero formula score and data strength over
/// <see cref="LineUps"/> seeded random line-ups of that shape. Made anew with each weight matrix.
/// </summary>
public sealed class ScoreScales(DataStore store, WeightMatrix matrix)
{
    public const int LineUps = 200;

    private readonly Dictionary<(LineUpShape Shape, bool Lane), BlendScale> _measured = [];

    /// <param name="lane">The Lane Phase view: its tiers and its match-data scope.</param>
    public BlendScale For(LineUpShape shape, bool lane)
    {
        if (!_measured.TryGetValue((shape, lane), out var scale))
        {
            scale = Measure(shape, lane);
            _measured[(shape, lane)] = scale;
        }
        return scale;
    }

    private BlendScale Measure(LineUpShape shape, bool lane)
    {
        var pool = store.Heroes.Keys.ToArray();
        if (shape.Size == 0 || shape.Size > pool.Length)
            return BlendScale.One;

        var tiers = lane ? ItemScoring.LaneTiers : ItemScoring.FullTiers;
        var items = store.Items.Values.Where(item => tiers.Contains(item.Tier)).Select(item => item.ItemId).ToList();
        var scope = lane ? "lane" : "full";
        var random = new Random(1);
        var (formula, data) = (new Spread(), new Spread());
        for (var i = 0; i < LineUps; i++)
        {
            var lineUp = shape.Draw(random, pool);
            foreach (var itemId in items)
            {
                formula.Add(ItemScoring.Total(matrix, itemId, lineUp));
                data.Add(ItemScoring.DataStrength(ItemScoring.DataScores(store, lineUp, itemId, scope)));
            }
        }
        return new BlendScale(formula.Rms, data.Rms);
    }

    /// <summary>The root mean square of the nonzero values added; 1 when there are none.</summary>
    private sealed class Spread
    {
        private double _squares;
        private int _count;

        public void Add(double value)
        {
            if (value == 0)
                return;
            _squares += value * value;
            _count++;
        }

        public double Rms => _count > 0 ? Math.Sqrt(_squares / _count) : 1.0;
    }
}
