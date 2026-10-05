using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Scoring;

/// <summary>One item in a hero's item table: how often the hero's players bought it, and how they did.</summary>
/// <param name="Usage">The share of the hero's matches it was bought in, 0 to 1.</param>
/// <param name="WinRateChange">Since the patch before, as a fraction; null when that patch has no win rate for it.</param>
/// <param name="UsageChange">Since the patch before, as a fraction; null without the patch before.</param>
public sealed record HeroItemRow(Item Item, long Wins, long Matches, double Usage, double? WinRateChange, double? UsageChange)
{
    public long Losses => Matches - Wins;

    public double WinRate => (double)Wins / Matches;
}

/// <summary>
/// One hero's items over one patch, as sites like tracklock.gg show them: every item its players bought,
/// with its win rate and usage, and how both moved since the patch before.
/// </summary>
/// <param name="Matches">The hero's matches, the usage's denominator.</param>
/// <param name="Rows">Most used first.</param>
public sealed record HeroItemTable(WinTotals Matches, IReadOnlyList<HeroItemRow> Rows)
{
    /// <summary>
    /// The table for <paramref name="heroId"/> in <paramref name="patch"/>'s counts, compared with the patch
    /// before it in <paramref name="segments"/> (newest first). Null when <paramref name="range"/> needs
    /// rank groups the patch was fetched without. Items the store doesn't know are left out.
    /// </summary>
    public static HeroItemTable? Build(IReadOnlyList<MatchSegment> segments, MatchSegment patch, string heroId, MatchMode mode,
        RankRange? range, IEnumerable<Item> items)
    {
        if (patch.HeroItems(mode, range) is not { } heroes)
            return null;
        var counts = heroes.GetValueOrDefault(heroId) ?? HeroCounts.Empty;
        var before = segments.SkipWhile(segment => segment.Patch.Start >= patch.Patch.Start).FirstOrDefault()
            ?.HeroItems(mode, range)?.GetValueOrDefault(heroId) is { Matches.Matches: > 0 } earlier
            ? earlier
            : null;

        var byGameId = items.Where(item => item.GameId != 0).ToDictionary(item => item.GameId);
        var rows = new List<HeroItemRow>();
        foreach (var (gameId, (wins, matches)) in counts.Items)
        {
            if (matches == 0 || !byGameId.TryGetValue(gameId, out var item))
                continue;
            var row = new HeroItemRow(item, wins, matches, Share(matches, counts.Matches), null, null);
            if (before is not null)
            {
                var (earlierWins, earlierMatches) = before.Items.GetValueOrDefault(gameId);
                row = row with
                {
                    WinRateChange = earlierMatches > 0 ? row.WinRate - (double)earlierWins / earlierMatches : null,
                    UsageChange = row.Usage - Share(earlierMatches, before.Matches),
                };
            }
            rows.Add(row);
        }
        return new HeroItemTable(counts.Matches, rows.OrderByDescending(row => row.Usage).ThenBy(row => row.Item.ItemName).ToList());
    }

    private static double Share(long matches, WinTotals hero) => hero.Matches > 0 ? Math.Min(1, (double)matches / hero.Matches) : 0;
}
