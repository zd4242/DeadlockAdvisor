using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeadlockAdvisor.Services.Formats;

/// <summary>
/// Writes the data files' JSON layout: one-space indents, "key": value, empty containers as [] / {},
/// floats in their shortest round-trip form (<see cref="NumberFormat.RoundTrip"/>), every line ending
/// \r\n and a newline at the end. System.Text.Json can't produce this shape.
/// </summary>
public static class DataJson
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const string _newline = "\r\n";

    public static byte[] ToFileBytes(JsonNode? node, bool ensureAscii)
    {
        var builder = new StringBuilder();
        Write(builder, node, 0, ensureAscii);
        builder.Append(_newline);
        return _utf8NoBom.GetBytes(builder.ToString());
    }

    /// <summary>Parses JSON keeping key order and whether each number was written as an int or a float.</summary>
    public static JsonNode? Parse(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false });

    private static void Write(StringBuilder builder, JsonNode? node, int depth, bool ensureAscii)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                WriteObject(builder, obj, depth, ensureAscii);
                break;
            case JsonArray array:
                WriteArray(builder, array, depth, ensureAscii);
                break;
            case JsonValue value:
                WriteValue(builder, value, ensureAscii);
                break;
        }
    }

    private static void WriteObject(StringBuilder builder, JsonObject obj, int depth, bool ensureAscii)
    {
        if (obj.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        builder.Append('{');
        var first = true;
        foreach (var (key, value) in obj)
        {
            if (!first)
                builder.Append(',');
            first = false;
            NewLine(builder, depth + 1);
            WriteString(builder, key, ensureAscii);
            builder.Append(": ");
            Write(builder, value, depth + 1, ensureAscii);
        }
        NewLine(builder, depth);
        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, JsonArray array, int depth, bool ensureAscii)
    {
        if (array.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[');
        for (var i = 0; i < array.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            NewLine(builder, depth + 1);
            Write(builder, array[i], depth + 1, ensureAscii);
        }
        NewLine(builder, depth);
        builder.Append(']');
    }

    private static void NewLine(StringBuilder builder, int depth)
    {
        builder.Append(_newline);
        builder.Append(' ', depth);
    }

    private static void WriteValue(StringBuilder builder, JsonValue value, bool ensureAscii)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            WriteElement(builder, element, ensureAscii);
            return;
        }

        if (value.TryGetValue<string>(out var text))
            WriteString(builder, text, ensureAscii);
        else if (value.TryGetValue<bool>(out var flag))
            builder.Append(flag ? "true" : "false");
        else if (value.TryGetValue<double>(out var real) && value.GetValueKind() == JsonValueKind.Number && IsFloat(value))
            builder.Append(FloatText(real));
        else if (value.TryGetValue<long>(out var integer))
            builder.Append(integer.ToString(CultureInfo.InvariantCulture));
        else if (value.TryGetValue<int>(out var small))
            builder.Append(small.ToString(CultureInfo.InvariantCulture));
        else
            throw new NotSupportedException($"Can't write JSON value {value}");
    }

    private static bool IsFloat(JsonValue value) =>
        value.TryGetValue<double>(out _) && !value.TryGetValue<long>(out _) && !value.TryGetValue<int>(out _);

    private static void WriteElement(StringBuilder builder, JsonElement element, bool ensureAscii)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                WriteString(builder, element.GetString()!, ensureAscii);
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                // A number with a fraction or exponent is a float, written in its shortest round-trip
                // form; anything else is an integer and comes back out as written.
                if (raw.AsSpan().IndexOfAny(".eE") >= 0)
                    builder.Append(FloatText(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)));
                else
                    builder.Append(raw == "-0" ? "0" : raw);
                break;
            default:
                throw new NotSupportedException($"Unexpected JSON element {element.ValueKind}");
        }
    }

    private static string FloatText(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsInfinity(value))
            return value > 0 ? "Infinity" : "-Infinity";
        return NumberFormat.RoundTrip(value);
    }

    private static void WriteString(StringBuilder builder, string text, bool ensureAscii)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < 0x20 || (ensureAscii && c > 0x7E))
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
