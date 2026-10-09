using System.Globalization;
using System.Text.Json.Nodes;

namespace DeadlockAdvisor.Services.GameApi;

/// <summary>
/// The durability trait, measured from how much damage each hero takes per match in /v1/analytics/hero-stats
/// rather than rated by hand. Damage taken is after resists, barriers and healing, so it covers what the trait's
/// description lists and base health alone doesn't, and it also records how much a hero's role puts it in the line
/// of fire. <see cref="GameApiService"/> fetches the rows, <see cref="GameSync.Apply"/> writes the scores.
/// </summary>
public static class HeroDurability
{
    public const string Trait = "durability";

    /// <summary>How far back the matches reach: long enough to smooth out a weekend, short enough to follow a patch.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>The average badge the matches start from, Phantom (tier 9, division 1): where the damage traits are measured too.</summary>
    public const int MinBadge = 91;

    /// <summary>A hero with fewer matches than this in the window is left alone: a new hero's average is mostly noise.</summary>
    public const long MinMatches = 1000;

    /// <summary>How far from the roster's median the ends of the trait's scale sit: a hero taking 55% more damage than the median scores 100.</summary>
    public const double Spread = 0.55;

    /// <summary>The URL for the last <see cref="Window"/> up to <paramref name="now"/>: one call, every hero in the answer.</summary>
    public static string Url(DateTimeOffset now) =>
        MatchStatsService.HeroStatsUrl(
            new() { ["min_average_badge"] = MinBadge.ToString(CultureInfo.InvariantCulture) },
            (now - Window).ToUnixTimeSeconds(),
            now.ToUnixTimeSeconds());

    /// <summary>
    /// Hero id → its durability score: damage taken per match against the median of every hero the answer lists,
    /// linear, with the scale's middle at the median and its ends at ± <see cref="Spread"/>.
    /// </summary>
    public static Dictionary<string, double> Measure(DataStore store, IReadOnlyList<JsonNode> rows)
    {
        if (!store.Categories.TryGetValue(Trait, out var category))
            return [];
        var byGameId = store.Heroes.Values.Where(hero => hero.GameId != 0).ToDictionary(hero => hero.GameId, hero => hero.HeroId);
        var taken = rows
            .Where(row => JsonRecord.Int(row, "matches") >= MinMatches && JsonRecord.Int(row, "total_player_damage_taken") > 0)
            .Select(row => (GameId: JsonRecord.Int(row, "hero_id"),
                            PerMatch: (double)JsonRecord.Int(row, "total_player_damage_taken") / JsonRecord.Int(row, "matches")))
            .ToList();
        if (taken.Count == 0)
            return [];
        var median = GameSync.Median(taken.Select(entry => entry.PerMatch));
        var middle = (category.ScaleMin + category.ScaleMax) / 2;
        var half = (category.ScaleMax - category.ScaleMin) / 2;
        return taken
            .Where(entry => byGameId.ContainsKey(entry.GameId))
            .ToDictionary(
                entry => byGameId[entry.GameId],
                entry => Math.Round(
                    Math.Clamp(middle + (entry.PerMatch / median - 1) / Spread * half, category.ScaleMin, category.ScaleMax),
                    MidpointRounding.AwayFromZero));
    }
}
