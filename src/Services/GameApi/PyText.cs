using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockAdvisor.Services.GameApi;

/// <summary>
/// The few Python string helpers the game sync's text handling leans on, reproduced exactly:
/// <c>html.unescape</c> (with the full HTML5 entity table, generated from Python into
/// html5_entities.json), <c>html.escape(quote=False)</c> and <c>str.isspace</c>.
/// </summary>
public static partial class PyText
{
    /// <summary>Python's <c>\s</c> in a str pattern: .NET's <c>\s</c> plus the four separators U+001C-U+001F.</summary>
    public const string Space = @"[\s\x1c-\x1f]";

    private static readonly Lazy<EntityTables> _tables = new(LoadTables);

    private sealed record EntityTables(
        Dictionary<string, string> Named,
        Dictionary<int, string> InvalidCharrefs,
        HashSet<int> InvalidCodepoints);

    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>Python's <c>str.strip()</c>.</summary>
    public static string Strip(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
            start++;
        while (end > start && IsSpace(text[end - 1]))
            end--;
        return text[start..end];
    }

    /// <summary>Python's <c>str.split()</c> with no arguments: runs of whitespace, no empty parts.</summary>
    public static string[] Split(string text) =>
        SpaceRun().Split(text).Where(part => part.Length > 0).ToArray();

    /// <summary><c>html.escape(text, quote=False)</c>.</summary>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary><c>html.unescape</c>: numeric and named character references, as HTML5 defines them.</summary>
    public static string Unescape(string text)
    {
        if (!text.Contains('&'))
            return text;
        return Charref().Replace(text, match => ReplaceCharref(match.Groups[1].Value));
    }

    private static string ReplaceCharref(string reference)
    {
        var tables = _tables.Value;
        if (reference[0] == '#')
        {
            var digits = reference.TrimEnd(';');
            // Huge values overflow to "invalid" either way, so clamp rather than fail.
            var parsed = reference[1] is 'x' or 'X'
                ? System.Numerics.BigInteger.Parse("0" + digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : System.Numerics.BigInteger.Parse(digits[1..], CultureInfo.InvariantCulture);
            var number = parsed > 0x110000 ? 0x110000 : (int)parsed;
            if (tables.InvalidCharrefs.TryGetValue(number, out var replacement))
                return replacement;
            if (number is >= 0xD800 and <= 0xDFFF || number > 0x10FFFF)
                return "�";
            if (tables.InvalidCodepoints.Contains(number))
                return "";
            return char.ConvertFromUtf32(number);
        }

        if (tables.Named.TryGetValue(reference, out var named))
            return named;
        // The longest name that's a known reference on its own, e.g. "&notit;" is "¬it;".
        for (var length = reference.Length - 1; length > 1; length--)
        {
            if (tables.Named.TryGetValue(reference[..length], out var prefix))
                return prefix + reference[length..];
        }
        return "&" + reference;
    }

    private static EntityTables LoadTables()
    {
        using var stream = typeof(PyText).Assembly.GetManifestResourceStream("GameApi/html5_entities.json")
                           ?? throw new FileNotFoundException("The embedded HTML entity table is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var named = root.GetProperty("named").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var invalid = root.GetProperty("invalid_charrefs").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetString()!);
        var codepoints = root.GetProperty("invalid_codepoints").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
        return new EntityTables(named, invalid, codepoints);
    }

    [GeneratedRegex(@"&(#[0-9]+;?|#[xX][0-9a-fA-F]+;?|[^\t\n\f <&#;]{1,32};?)")]
    private static partial Regex Charref();

    [GeneratedRegex(Space + "+")]
    private static partial Regex SpaceRun();
}
