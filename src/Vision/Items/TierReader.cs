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

    private readonly List<(int Tier, float[] Frame, int Slot, int Cell)> _references = [];

    /// <summary>The numeral of a cell as a vector comparable by dot product.</summary>
    public static float[] Frame(RgbImage band, ItemPlace place, int slot, int cell)
    {
        var (x, y) = place.Cell(slot, cell);
        var offset = (0.5 - _inset) * place.Side;
        var sample = ItemFrame.Sample(band, x + offset, y - offset, _window * place.Side, _size);
        var vector = new float[_size * _size];
        var kept = new bool[vector.Length];
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
                count++;
            }
        }
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
        return vector;
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

    public void Add(int tier, float[] frame, int slot, int cell) => _references.Add((tier, frame, slot, cell));

    /// <summary>A numeral that looks this much more like another tier's references than like its item's contradicts the item.</summary>
    public const double VetoLead = 0.05;

    /// <summary>...and has to match them this well: where the contradiction was real the numeral matched the other tier at 0.95 or better, where it wasn't (a tier few cells on the board have) at 0.77 or less.</summary>
    public const double VetoSimilarity = 0.90;

    /// <summary>
    /// The tier a numeral looks clearly more like than it does <paramref name="claimed"/>, the tier its cell's item is, or null when
    /// it agrees or there is nothing to say. Softer than <see cref="Classify"/>, as taking a read back is the cautious thing to do.
    /// </summary>
    public int? Contradicts(float[] frame, int claimed, int slot, int cell)
    {
        var best = new Dictionary<int, float>();
        foreach (var (tier, reference, referenceSlot, referenceCell) in _references)
        {
            if (referenceSlot == slot && referenceCell == cell)
                continue;
            var similarity = TemplateBank.Dot(frame, reference);
            if (!best.TryGetValue(tier, out var kept) || similarity > kept)
                best[tier] = similarity;
        }
        if (!best.TryGetValue(claimed, out var own))
            return null;
        var other = best.Where(pair => pair.Key != claimed).OrderByDescending(pair => pair.Value).Cast<KeyValuePair<int, float>?>().FirstOrDefault();
        return other is { } found && found.Value >= VetoSimilarity && found.Value > own + VetoLead ? found.Key : null;
    }

    /// <summary>The tier a numeral is, or null when it isn't clearly any. A cell's own reference, if it has one, isn't counted for it.</summary>
    public int? Classify(float[] frame, int slot = -1, int cell = -1)
    {
        var best = new Dictionary<int, float>();
        foreach (var (tier, reference, referenceSlot, referenceCell) in _references)
        {
            if (referenceSlot == slot && referenceCell == cell)
                continue;
            var similarity = TemplateBank.Dot(frame, reference);
            if (!best.TryGetValue(tier, out var kept) || similarity > kept)
                best[tier] = similarity;
        }
        var ranked = best.OrderByDescending(pair => pair.Value).ToList();
        if (ranked.Count == 0 || ranked[0].Value < MinSimilarity)
            return null;
        return ranked.Count == 1 || ranked[0].Value - ranked[1].Value >= MinLead ? ranked[0].Key : null;
    }
}
