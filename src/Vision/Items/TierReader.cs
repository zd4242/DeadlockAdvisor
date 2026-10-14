namespace DeadlockAdvisor.Vision.Items;

/// <summary>
/// The tier numeral (I to IV) printed in a tag, in dark strokes on the tag's colour. An item's tier gives its cost exactly (every
/// item's is 800 · 2^(tier-1)) and tells apart items drawn nearly alike at different tiers (Superior and Compress Cooldown).
/// <para>
/// The numerals are read against references cut from the same board: the cells the icons identified confidently carry a known
/// tier, so each board brings the references it needs at its own resolution and HUD scale, and a cell is the tier of the references
/// it looks most like.
/// </para>
/// </summary>
internal sealed class TierReader
{
    private const int _size = 12;

    /// <summary>The numeral's window: a square this share of a cell's side, centred this far in from the cell's top right corner along both edges (inside the tag's triangle).</summary>
    private const double _window = 0.20;
    private const double _inset = 0.135;

    /// <summary>A numeral has to look this much like a tier's references, and this much more than any other tier's, to be read as it.</summary>
    public const double MinSimilarity = 0.80;
    public const double MinLead = 0.10;

    private readonly List<(int Tier, Numeral Numeral, int Slot, int Cell)> _references = [];

    /// <summary>
    /// A numeral as read off a cell: its shape as a vector comparable by dot product, how much ink it is (in thousandths of the cell's
    /// area, the share of the tag's brightness that the strokes take away), how bright its tag is, and the side of its cell in pixels. The
    /// strokes are a pixel wide at 1080p, where the shapes of I, II and III are all but the same and the ink is not.
    /// </summary>
    public readonly record struct Numeral(float[] Frame, double Mass, double Tag, double Side);

    /// <summary>The numeral of a cell as a vector comparable by dot product.</summary>
    public static float[] Frame(RgbImage band, ItemPlace place, int slot, int cell) => Read(band, place, slot, cell).Frame;

    public static Numeral Read(RgbImage band, ItemPlace place, int slot, int cell)
    {
        var (x, y) = place.Cell(slot, cell);
        var offset = (0.5 - _inset) * place.Side;
        var sample = ItemFrame.Sample(band, x + offset, y - offset, _window * place.Side, _size);
        var vector = new float[_size * _size];
        var kept = new bool[vector.Length];
        var luminance = new List<float>();
        double total = 0;
        var count = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            // How far in from the cell's top right corner along each edge, in shares of its side: the tag is the triangle under
            // _triangle, and the pixels past it are the icon's.
            double along = _inset + _window * (0.5 - ((i % _size) + 0.5) / _size), down = _inset + _window * (((i / _size) + 0.5) / _size - 0.5);
            kept[i] = along + down <= _triangle;
            vector[i] = (float)(0.299 * sample.Pixels[i * 3] + 0.587 * sample.Pixels[i * 3 + 1] + 0.114 * sample.Pixels[i * 3 + 2]);
            if (kept[i])
            {
                total += vector[i];
                luminance.Add(vector[i]);
                count++;
            }
        }
        var tag = luminance.Count == 0 ? 0 : luminance.Order().ElementAt(luminance.Count * 3 / 4);
        var ink = tag <= 0 ? 0 : luminance.Sum(value => Math.Max(0, (tag - value) / tag)) / (_size * _size) * _window * _window * 1000;
        var mean = (float)(total / Math.Max(1, count));
        double squares = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = kept[i] ? vector[i] - mean : 0;
            squares += vector[i] * (double)vector[i];
        }
        var scale = (float)(1.0 / (Math.Sqrt(squares) + 1e-6));
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= scale;
        return new Numeral(vector, ink, tag, place.Side);
    }

    /// <summary>The pixels of a numeral's window that are on the tag lie within this far of the corner (the tag's own edge is at 0.47), as a share of a cell's side, along the two edges together.</summary>
    private const double _triangle = 0.42;

    /// <summary>
    /// How dark the numeral's window gets against the tag's own colour, within the tag (the tag's brightness less the darkest few,
    /// over the tag's brightness), 0 to 1. A tag has a numeral and any glare or stray colour hasn't.
    /// </summary>
    public static double InkShare(RgbImage band, ItemPlace place, int slot, int cell)
    {
        var (x, y) = place.Cell(slot, cell);
        var offset = (0.5 - _inset) * place.Side;
        var sample = ItemFrame.Sample(band, x + offset, y - offset, _window * place.Side, _size);
        var luminance = new List<double>();
        for (var i = 0; i < _size * _size; i++)
        {
            double along = _inset + _window * (0.5 - ((i % _size) + 0.5) / _size), down = _inset + _window * (((i / _size) + 0.5) / _size - 0.5);
            if (along + down <= _triangle)
                luminance.Add(0.299 * sample.Pixels[i * 3] + 0.587 * sample.Pixels[i * 3 + 1] + 0.114 * sample.Pixels[i * 3 + 2]);
        }
        var sorted = luminance.Order().ToList();
        var tag = sorted[sorted.Count * 3 / 4];
        return tag <= 0 ? 0 : (tag - sorted[sorted.Count / 20]) / tag;
    }

    public int ReferenceCount => _references.Count;

    public void Add(int tier, Numeral numeral, int slot, int cell) => _references.Add((tier, numeral, slot, cell));

    /// <summary>A numeral that looks this much more like another tier's references than like its item's contradicts the item.</summary>
    public const double VetoLead = 0.05;

    /// <summary>...and has to match them this well: where the contradiction was real the numeral matched the other tier at 0.95 or better, where it wasn't (a tier few cells on the board have) at 0.77 or less.</summary>
    public const double VetoSimilarity = 0.90;

    /// <summary>
    /// The tier a numeral looks clearly more like than it does <paramref name="claimed"/>, the tier its cell's item is, or null when
    /// it agrees or there is nothing to say. Softer than <see cref="Classify"/>, as taking a read back is the cautious thing to do.
    /// The shapes decide where they can; where the strokes are too small for that, the ink does (<see cref="ByInk"/>).
    /// </summary>
    public int? Contradicts(Numeral numeral, int claimed, int slot, int cell)
    {
        var best = new Dictionary<int, float>();
        foreach (var (tier, reference, referenceSlot, referenceCell) in _references)
        {
            if (referenceSlot == slot && referenceCell == cell)
                continue;
            var similarity = TemplateBank.Dot(numeral.Frame, reference.Frame);
            if (!best.TryGetValue(tier, out var kept) || similarity > kept)
                best[tier] = similarity;
        }
        if (best.TryGetValue(claimed, out var own))
        {
            var other = best.Where(pair => pair.Key != claimed).OrderByDescending(pair => pair.Value).Cast<KeyValuePair<int, float>?>().FirstOrDefault();
            if (other is { } found && found.Value >= VetoSimilarity && found.Value > own + VetoLead)
                return found.Key;
        }
        if (!InkDecides(numeral) || Cluster(claimed, slot, cell) is not { } claimedCluster
            || Math.Abs(numeral.Mass - claimedCluster.Median) < _farSpreads * claimedCluster.Spread)
            return null;
        return ByInk(numeral, slot, cell);
    }

    /// <summary>The tier a numeral is, or null when it isn't clearly any. A cell's own reference, if it has one, isn't counted for it.</summary>
    public int? Classify(Numeral numeral, int slot = -1, int cell = -1)
    {
        var best = new Dictionary<int, float>();
        foreach (var (tier, reference, referenceSlot, referenceCell) in _references)
        {
            if (referenceSlot == slot && referenceCell == cell)
                continue;
            var similarity = TemplateBank.Dot(numeral.Frame, reference.Frame);
            if (!best.TryGetValue(tier, out var kept) || similarity > kept)
                best[tier] = similarity;
        }
        var ranked = best.OrderByDescending(pair => pair.Value).ToList();
        if (ranked.Count > 0 && ranked[0].Value >= MinSimilarity && (ranked.Count == 1 || ranked[0].Value - ranked[1].Value >= MinLead))
            return ranked[0].Key;
        return ByInk(numeral, slot, cell);
    }

    // -- the ink ----------------------------------------------------------------------

    /// <summary>
    /// Between these cell sides, in pixels, the shapes can't say which numeral it is and the ink can (1080p is 26). Above, at 1440p's 35, the
    /// shapes read every tier and the ink doesn't (the strokes of IV fall on the pixels differently from one tag to the next, 6.0 to 8.5
    /// beside III's 8.9). Below, at 900p's 22, the strokes blur until I and II overlap (3.2 to 3.5, 3.6 to 4.8) and nothing says which.
    /// </summary>
    private const double _inkFromSide = 24;
    private const double _inkBelowSide = 31;

    private static bool InkDecides(Numeral numeral) => numeral.Side is >= _inkFromSide and < _inkBelowSide;

    /// <summary>The fewest references a tier needs on the board for its ink to be a measure.</summary>
    private const int _minCluster = 3;

    /// <summary>A tier's references vary by their spread, at least this share of their median (the strokes are drawn the same way every time).</summary>
    private const double _minSpread = 0.02;

    /// <summary>A numeral's ink is a tier's when it is within this many spreads of it and the next tier is at least this many and twice as far.</summary>
    private const double _fitSpreads = 3;
    private const double _apartSpreads = 3.5;

    /// <summary>The ink of a numeral that isn't its claimed tier's is this many spreads from it.</summary>
    private const double _farSpreads = 5;

    /// <summary>A tag brighter than this many times the board's usual is lit (a freshly bought item flashing): its numeral is not drawn as the others are.</summary>
    private const double _litTag = 1.5;

    /// <summary>
    /// The tier whose references' ink a numeral's is clearly nearest, or null. At 1080p the strokes are a pixel wide and the shapes of I,
    /// II and III correlate at 0.96 or better, but the ink of a numeral is the same for a tier on every tag (I 4.0, II 5.4, III 5.9, IV 5.0
    /// there), and the board's own references say where each tier's is.
    /// </summary>
    private int? ByInk(Numeral numeral, int slot, int cell)
    {
        if (!InkDecides(numeral) || IsLit(numeral))
            return null;
        var distances = _references.Select(reference => reference.Tier).Distinct()
            .Select(tier => (Tier: tier, Cluster: Cluster(tier, slot, cell)))
            .Where(pair => pair.Cluster is not null)
            .Select(pair => (pair.Tier, Spreads: Math.Abs(numeral.Mass - pair.Cluster!.Value.Median) / pair.Cluster.Value.Spread))
            .OrderBy(pair => pair.Spreads)
            .ToList();
        if (distances.Count == 0 || distances[0].Spreads > _fitSpreads)
            return null;
        return distances.Count == 1 || distances[1].Spreads >= Math.Max(_apartSpreads, 2 * distances[0].Spreads) ? distances[0].Tier : null;
    }

    private bool IsLit(Numeral numeral)
    {
        var tags = _references.Select(reference => reference.Numeral.Tag).Order().ToList();
        return tags.Count > 0 && numeral.Tag > _litTag * tags[tags.Count / 2];
    }

    /// <summary>The middle and spread (robust, from the median's own deviation) of the ink of a tier's references, not counting the cell itself; null with too few.</summary>
    private (double Median, double Spread)? Cluster(int tier, int slot, int cell)
    {
        var masses = _references.Where(reference => reference.Tier == tier && !(reference.Slot == slot && reference.Cell == cell))
            .Select(reference => reference.Numeral.Mass).Order().ToList();
        if (masses.Count < _minCluster)
            return null;
        var median = Median(masses);
        var spread = 1.4826 * Median(masses.Select(mass => Math.Abs(mass - median)).Order().ToList());
        return (median, Math.Max(spread, _minSpread * median));
    }

    private static double Median(List<double> sorted) => sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}
