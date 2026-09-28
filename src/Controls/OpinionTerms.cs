using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// Colours the two opinions an item gets wherever a label names them: "formula" in the formula's gold,
/// "data" or "match data" in the data's teal, matching the bars they draw.
/// </summary>
public static partial class OpinionTerms
{
    public static readonly IValueConverter Converter =
        new FuncValueConverter<string?, IReadOnlyList<TextSpan>>(text => Spans(text));

    public static IReadOnlyList<TextSpan> Spans(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var spans = new List<TextSpan>();
        var at = 0;
        foreach (System.Text.RegularExpressions.Match term in Term().Matches(text))
        {
            if (term.Index > at)
                spans.Add(new TextSpan(text[at..term.Index]));
            var isFormula = term.Value.Equals("formula", StringComparison.OrdinalIgnoreCase);
            spans.Add(new TextSpan(term.Value, isFormula ? Palette.Formula : Palette.Data));
            at = term.Index + term.Length;
        }
        if (at < text.Length)
            spans.Add(new TextSpan(text[at..]));
        return spans;
    }

    [GeneratedRegex(@"\b(formula|(match )?data)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Term();
}
