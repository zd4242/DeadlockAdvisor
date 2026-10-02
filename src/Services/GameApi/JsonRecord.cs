using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Services.GameApi;

/// <summary>
/// Lenient reading of API records: a missing key, a JSON null, and an empty or zero value all count
/// as absent, and whatever JSON scalar turns up can be read as text or as an integer.
/// </summary>
internal static class JsonRecord
{
    /// <summary>The value at <paramref name="key"/>: null for a missing key, a JSON null, or a record that isn't an object.</summary>
    public static JsonNode? Get(JsonNode? record, string key) =>
        record is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary>Whether a value counts as present: not null, false, zero, an empty string, list or object.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => Double(value) != 0,
            _ => false,
        },
        _ => false,
    };

    /// <summary>A JSON number as a double, whether it was parsed or built in code from an integer.</summary>
    private static double Double(JsonValue value) => double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);

    /// <summary>A text field, "" when it's absent.</summary>
    public static string Text(JsonNode? record, string key) =>
        Get(record, key) is { } node && Truthy(node) ? Str(node) : "";

    /// <summary>A JSON scalar as text: 16 stays "16", 16.0 is "16.0", true is "True", null is "None".</summary>
    public static string Str(JsonNode? node)
    {
        if (node is not JsonValue value)
            return node is null ? "None" : node.ToJsonString();
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return value.GetValue<string>();
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
            case JsonValueKind.Number:
                var raw = value.ToJsonString();
                return raw.IndexOfAny(['.', 'e', 'E']) < 0
                    ? BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                    : NumberFormat.RoundTrip(double.Parse(raw, CultureInfo.InvariantCulture));
            default:
                return "None";
        }
    }

    /// <summary>A field as an integer, 0 when it's absent; a fraction is truncated toward zero.</summary>
    public static long Int(JsonNode? record, string key)
    {
        var node = Get(record, key);
        if (!Truthy(node))
            return 0;
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
            return (long)Math.Truncate(Double(value));
        return long.Parse(SyncText.Strip(Str(node)), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    /// <summary>A list field's items; anything else gives nothing.</summary>
    public static IEnumerable<JsonNode?> Items(JsonNode? record, string key) =>
        Get(record, key) as JsonArray ?? [];

    /// <summary>Whether a list field holds <paramref name="needle"/>, or a text field contains it.</summary>
    public static bool Contains(JsonNode? record, string key, string needle) => Get(record, key) switch
    {
        JsonArray array => array.Any(entry => entry is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == needle),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Contains(needle, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>The string entries of a list, e.g. property keys.</summary>
    public static IEnumerable<string> Strings(JsonNode? record, string key) =>
        Items(record, key).OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>());
}
