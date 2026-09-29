namespace DeadlockAdvisor.Core;

/// <summary>
/// Type-to-find matching: the query's letters in order anywhere in the text, ignoring case and spaces, scored so
/// that runs of letters and the starts of words rank first ("gt" finds Grey Talon, "ven" Venator before Seven).
/// </summary>
public static class FuzzyMatch
{
    private const int Letter = 16;
    private const int WordStart = 8;
    private const int Run = 8;
    private const int TextStart = 4;

    /// <summary>Null when the text doesn't have the query's letters in order; otherwise higher is better.</summary>
    public static int? Score(string query, string text)
    {
        var needle = query.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray();
        if (needle.Length == 0)
            return 0;
        if (needle.Length > text.Length)
            return null;

        // For each letter of the query in turn, best[j] is the best score with that letter matched at text[j]:
        // a letter just after the previous one's match earns the run bonus, and one further on loses a point
        // per letter skipped, as the first match does for the letters before it.
        var best = new int?[text.Length];
        for (var j = 0; j < text.Length; j++)
            best[j] = Matches(needle[0], text, j) ? Bonus(text, j) - j : null;

        for (var i = 1; i < needle.Length; i++)
        {
            var next = new int?[text.Length];
            // The best of best[p] + p over p < j - 1, so a gapped step to j costs best[p] - (j - p - 1).
            int? gapped = null;
            for (var j = 1; j < text.Length; j++)
            {
                if (j >= 2 && best[j - 2] is { } earlier)
                    gapped = Math.Max(gapped ?? int.MinValue, earlier + j - 2);
                if (!Matches(needle[i], text, j))
                    continue;
                int? from = best[j - 1] is { } adjacent ? adjacent + Run : null;
                if (gapped is { } far)
                    from = Math.Max(from ?? int.MinValue, far - j + 1);
                next[j] = from + Bonus(text, j);
            }
            best = next;
        }
        return best.Max();
    }

    /// <summary>The entries that match, best first; ties, and every entry for an empty query, keep their order.</summary>
    public static IReadOnlyList<T> Filter<T>(IEnumerable<T> entries, string? query, Func<T, string> text)
    {
        if (string.IsNullOrWhiteSpace(query))
            return entries.ToList();
        return entries
            .Select(entry => (Entry: entry, Score: Score(query, text(entry))))
            .Where(scored => scored.Score is not null)
            .OrderByDescending(scored => scored.Score)
            .Select(scored => scored.Entry)
            .ToList();
    }

    private static bool Matches(char letter, string text, int at) => char.ToLowerInvariant(text[at]) == letter;

    private static int Bonus(string text, int at)
    {
        if (at == 0)
            return Letter + WordStart + TextStart;
        var before = text[at - 1];
        var startsWord = !char.IsLetterOrDigit(before) || (char.IsLower(before) && char.IsUpper(text[at]));
        return Letter + (startsWord ? WordStart : 0);
    }
}
