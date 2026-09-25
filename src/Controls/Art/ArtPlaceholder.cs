using Avalonia.Media;

namespace DeadlockAdvisor.Controls.Art;

/// <summary>The tile drawn for a hero or item without art: a stable per-id colour and the name's initials.</summary>
public static class ArtPlaceholder
{
    private static readonly HashSet<string> _skippedWords = ["and", "of", "the", "&"];

    /// <summary>Stable per-id colour, so the same hero or item is always the same tile (Qt's HSV 120/175).</summary>
    public static Color ColorFor(string key)
    {
        uint hash = 0;
        foreach (var c in key)
            hash = unchecked(hash * 31 + c);
        return FromQtHsv((int)(hash % 360), 120, 175);
    }

    public static string Initials(string name)
    {
        var words = name.Replace('_', ' ').Replace('-', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var letters = words
            .Where(word => !_skippedWords.Contains(word.ToLowerInvariant()))
            .Select(word => char.ToUpperInvariant(word[0]))
            .ToList();
        if (letters.Count == 0)
            return name[..Math.Min(2, name.Length)].ToUpperInvariant();
        return new string(letters.Take(2).ToArray());
    }

    /// <summary>QColor.fromHsv(h, s, v).toRgb(), with its 16-bit intermediate steps, so tiles match the Python app's.</summary>
    internal static Color FromQtHsv(int hue, int saturation, int value)
    {
        const double max = ushort.MaxValue;
        var h = hue * 100 / 6000.0;
        var s = saturation * 0x101 / max;
        var v = value * 0x101 / max;
        var i = (int)h;
        var f = h - i;
        var p = v * (1 - s);

        double r, g, b;
        if ((i & 1) != 0)
        {
            var q = v * (1 - s * f);
            (r, g, b) = i switch
            {
                1 => (q, v, p),
                3 => (p, q, v),
                _ => (v, p, q),
            };
        }
        else
        {
            var t = v * (1 - s * (1 - f));
            (r, g, b) = i switch
            {
                0 => (v, t, p),
                2 => (p, v, t),
                _ => (t, p, v),
            };
        }

        static byte To8Bit(double channel)
        {
            var wide = (int)Math.Round(channel * max, MidpointRounding.AwayFromZero);
            return (byte)((wide - (wide >> 8) + 0x80) >> 8);
        }

        return Color.FromRgb(To8Bit(r), To8Bit(g), To8Bit(b));
    }
}
