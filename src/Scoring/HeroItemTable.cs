using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Scoring;

/// <summary>One item in a hero's item table: how often the hero's players bought it, and how they did.</summary>
/// <param name="Usage">The share of the hero's matches it was bought in, 0 to 1.</param>
/// <param name="WinRateChange">Since the patch before, as a fraction; null when that patch has no win rate for it.</param>
/// <param name="UsageChange">Since the patch before, as a fraction; null without the patch before.</param>
/// <param name="Fit">How much more the hero wins with it than everyone who builds it; null when it was bought in too few matches to measure.</param>
public sealed record HeroItemRow(
    Item Item, long Wins, long Matches, double Usage, double? WinRateChange, double? UsageChange, HeroFit? Fit = null)
{
    public long Losses => Matches - Wins;

    public double WinRate => (double)Wins / Matches;
}

/// <summary>
/// One hero's items over one or more patches, as sites like tracklock.gg show them: every item its players
/// bought, with its win rate and usage, and how both moved since the patch before.
/// </summary>
/// <param name="Matches">The hero's matches, the usage's denominator.</param>
/// <param name="Rows">Most used first.</param>
/// <param name="PatchCount">How many patches' counts these are, which can be fewer than were asked for.</param>
public sealed record HeroItemTable(WinTotals Matches, IReadOnlyList<HeroItemRow> Rows, int PatchCount)
{
    /// <summary>
    /// The table for <paramref name="heroId"/> over <paramref name="patches"/>' counts added up, compared with
    /// the patch before the oldest of them in <paramref name="segments"/> (newest first). A patch without the
    /// rank groups <paramref name="range"/> needs is left out; null when that's all of them. Items the store
    /// doesn't know are left out.
    /// </summary>
    public static HeroItemTable? Build(IReadOnlyList<MatchSegment> segments, IReadOnlyList<MatchSegment> patches, string heroId,
        MatchMode mode, RankRange? range, IEnumerable<Item> items)
    {
        var everyHero = new Dictionary<string, HeroCounts>();
        var oldest = long.MaxValue;
        var patchCount = 0;
        foreach (var patch in patches)
        {
            if (patch.HeroItems(mode, range) is not { } heroes)
                continue;
            foreach (var (id, heroCounts) in heroes)
                everyHero[id] = everyHero.TryGetValue(id, out var sofar) ? sofar.Plus(heroCounts) : heroCounts;
            oldest = Math.Min(oldest, patch.Patch.Start);
            patchCount++;
        }
        if (patchCount == 0)
            return null;
        var counts = everyHero.GetValueOrDefault(heroId) ?? HeroCounts.Empty;
        var before = segments.SkipWhile(segment => segment.Patch.Start >= oldest).FirstOrDefault()
            ?.HeroItems(mode, range)?.GetValueOrDefault(heroId) is { Matches.Matches: > 0 } earlier
            ? earlier
            : null;

        var byGameId = items.Where(item => item.GameId != 0).ToDictionary(item => item.GameId);
        var tiers = MatchStatsMath.Tiers(byGameId.Values);
        var fits = HeroFits.Of(heroId, everyHero, tiers, HeroFits.NoiseScale(patches, tiers));
        var rows = new List<HeroItemRow>();
        foreach (var (gameId, (wins, matches)) in counts.Items)
        {
            if (matches == 0 || !byGameId.TryGetValue(gameId, out var item))
                continue;
            var row = new HeroItemRow(item, wins, matches, Share(matches, counts.Matches), null, null, fits.GetValueOrDefault(gameId));
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
        return new HeroItemTable(counts.Matches, rows.OrderByDescending(row => row.Usage).ThenBy(row => row.Item.ItemName).ToList(), patchCount);
    }

    private static double Share(long matches, WinTotals hero) => hero.Matches > 0 ? Math.Min(1, (double)matches / hero.Matches) : 0;
}
