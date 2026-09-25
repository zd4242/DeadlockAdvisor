using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Services.GameApi;

/// <summary>
/// Reading API records the way the Python sync does: <c>record.get(key) or default</c> with
/// Python truthiness, <c>str()</c> and <c>int()</c> of whatever JSON scalar turns up.
/// </summary>
internal static class PyJson
{
    /// <summary><c>record.get(key)</c>: null for a missing key, a JSON null, or a record that isn't an object.</summary>
    public static JsonNode? Get(JsonNode? record, string key) =>
        record is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

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

    /// <summary><c>record.get(key) or ""</c> for a text field.</summary>
    public static string Text(JsonNode? record, string key) =>
        Get(record, key) is { } node && Truthy(node) ? Str(node) : "";

    /// <summary>Python's <c>str()</c> of a JSON scalar: 16 stays "16", 16.0 is "16.0", true is "True".</summary>
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
                    : NumberFormat.Repr(double.Parse(raw, CultureInfo.InvariantCulture));
            default:
                return "None";
        }
    }

    /// <summary><c>int(record.get(key) or 0)</c>.</summary>
    public static long Int(JsonNode? record, string key)
    {
        var node = Get(record, key);
        if (!Truthy(node))
            return 0;
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
            return (long)Math.Truncate(Double(value));
        return long.Parse(PyText.Strip(Str(node)), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    /// <summary>Iterating <c>record.get(key) or ()</c>: a list's items; anything else gives nothing.</summary>
    public static IEnumerable<JsonNode?> Items(JsonNode? record, string key) =>
        Get(record, key) as JsonArray ?? [];

    /// <summary><c>needle in record.get(key) or []</c>: membership in a list, or a substring of a string.</summary>
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
