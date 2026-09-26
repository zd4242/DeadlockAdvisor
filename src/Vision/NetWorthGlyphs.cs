using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeadlockAdvisor.Vision;

/// <summary>
/// The reference digits the net-worth reader matches against: glyphs framed the way
/// <see cref="NetWorthReader"/> frames them, cut from the labelled captures in the tests' vision
/// fixtures and embedded as net_worth_glyphs.json. A glyph takes the digit whose best reference it
/// matches most closely, by dot product.
/// </summary>
public sealed class NetWorthGlyphs
{
    public const string ResourceName = "Vision/net_worth_glyphs.json";

    private static readonly Lazy<NetWorthGlyphs> _bundled = new(LoadBundled);

    private readonly List<(char Digit, float[] Vector)> _references = [];

    /// <param name="frames">Raw frames (ink 0 to 1, <see cref="NetWorthReader.FrameWidth"/> × <see cref="NetWorthReader.FrameHeight"/>) per digit.</param>
    public NetWorthGlyphs(IEnumerable<(char Digit, float[] Frame)> frames)
    {
        foreach (var (digit, frame) in frames)
        {
            if (Normalize(frame) is { } vector)
                _references.Add((digit, vector));
        }
    }

    public static NetWorthGlyphs Bundled => _bundled.Value;

    public int Count => _references.Count;

    /// <summary>The best digit for a raw frame, its score, and how far it leads the runner-up digit.</summary>
    public (char Digit, double Score, double Margin) Classify(float[] frame)
    {
        if (Normalize(frame) is not { } vector)
            return ('?', 0, 0);
        var best = new Dictionary<char, double>();
        foreach (var (digit, reference) in _references)
        {
            var score = Dot(vector, reference);
            if (!best.TryGetValue(digit, out var current) || score > current)
                best[digit] = score;
        }
        var ranked = best.OrderByDescending(pair => pair.Value).ToList();
        if (ranked.Count == 0)
            return ('?', 0, 0);
        var margin = ranked.Count > 1 ? ranked[0].Value - ranked[1].Value : ranked[0].Value;
        return (ranked[0].Key, ranked[0].Value, margin);
    }

    /// <summary>Mean-subtracted and unit length, so a dot product is a correlation; null for a blank frame.</summary>
    private static float[]? Normalize(float[] frame)
    {
        var mean = frame.Average();
        var vector = frame.Select(value => value - mean).ToArray();
        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (norm < 1e-6)
            return null;
        return vector.Select(value => (float)(value / norm)).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        var total = 0.0;
        for (var i = 0; i < a.Length; i++)
            total += a[i] * b[i];
        return total;
    }

    // -- the embedded file -------------------------------------------------------

    private static NetWorthGlyphs LoadBundled()
    {
        using var stream = typeof(NetWorthGlyphs).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new FileNotFoundException("The embedded net-worth glyphs are missing.");
        return FromJson(JsonNode.Parse(stream)!);
    }

    /// <summary>Digit → frames, each frame's ink quantised to a byte and base64-encoded.</summary>
    public static NetWorthGlyphs FromJson(JsonNode json)
    {
        var frames = new List<(char, float[])>();
        foreach (var (digit, list) in json["digits"]!.AsObject())
        {
            foreach (var encoded in list!.AsArray())
                frames.Add((digit[0], Convert.FromBase64String((string)encoded!).Select(value => value / 255f).ToArray()));
        }
        return new NetWorthGlyphs(frames);
    }

    public static JsonObject ToJson(IEnumerable<(char Digit, float[] Frame)> frames)
    {
        var digits = new JsonObject();
        foreach (var group in frames.GroupBy(frame => frame.Digit).OrderBy(group => group.Key))
        {
            var list = new JsonArray();
            foreach (var (_, frame) in group)
                list.Add(Convert.ToBase64String(frame.Select(value => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255)).ToArray()));
            digits[group.Key.ToString()] = list;
        }
        return new JsonObject
        {
            ["width"] = NetWorthReader.FrameWidth,
            ["height"] = NetWorthReader.FrameHeight,
            ["digits"] = digits,
        };
    }
}
