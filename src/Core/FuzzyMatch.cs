namespace DeadlockAdvisor.Core;

/// <summary>
/// Type-to-find matching, ignoring case and spaces: the query's letters in order, where each letter after the first
/// either follows the one before or starts a word ("gt" finds Grey Talon, "mk" Mo &amp; Krill). Letters scattered
/// through the middle of words don't count, so "slow" doesn't find Compress Cooldown. Starts of words and longer
/// runs rank first ("ven" finds Venator before Seven).
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
        // a letter just after the previous one's match earns the run bonus, and one further on, which must
        // start a word, loses a point per letter skipped, as the first match does for the letters before it.
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
                if (gapped is { } far && StartsWord(text, j))
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

    private static int Bonus(string text, int at) =>
        Letter + (StartsWord(text, at) ? WordStart : 0) + (at == 0 ? TextStart : 0);

    /// <summary>The first letter of the text or of a word in it, including the capital of "McGinnis".</summary>
    private static bool StartsWord(string text, int at)
    {
        if (at == 0)
            return true;
        var before = text[at - 1];
        return !char.IsLetterOrDigit(before) || (char.IsLower(before) && char.IsUpper(text[at]));
    }
}
