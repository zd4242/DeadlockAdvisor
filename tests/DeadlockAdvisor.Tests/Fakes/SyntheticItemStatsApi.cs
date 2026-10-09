using System.Globalization;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>
/// deadlock-api.com's item stats, made up but consistent: every answer adds up the same table of daily
/// (patch, rank, buyer's hero, item) counts, so date ranges, rank groups and hero buckets all add up the
/// way the real API's do; /hero-stats adds up each hero's matches alike. Every match also has buyers on a
/// hero the store doesn't know, and unranked matches that no rank group has (match_mode=ranked leaves them out). Heroes, enemies and patches each move win rates by a fixed pattern,
/// so the lifts aren't flat, and from Emissary up the heroes' pattern shifts, as if those ranks built
/// differently.
/// </summary>
public sealed class SyntheticItemStatsApi : IDeadlockApi
{
    /// <summary>A hero the API buckets that the store doesn't know.</summary>
    public const long UnknownHero = 999;

    public static readonly string[] PatchTitles = ["09-29-2026", "09-16-2026 Update", "08-22-2026 Update", "08-12-2026 Update"];

    /// <summary>Midday on 1 October 2026: a day and a half into the newest patch.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // Purchases a day by rank group: unranked first, then tiers 1 to 11, about as the real data has them.
    private static readonly int[] _groupWeights = [670, 25, 47, 41, 35, 33, 34, 34, 45, 30, 7, 2];

    private readonly List<long> _heroes;
    private readonly List<long> _items;
    private readonly List<long> _patchStarts;
    private readonly Dictionary<(int Patch, int Group), Day> _days = [];

    public SyntheticItemStatsApi(DataStore store)
    {
        _heroes = store.Heroes.Values.Where(hero => hero.GameId != 0).Select(hero => hero.GameId).Append(UnknownHero).ToList();
        _items = store.Items.Values.Where(item => item.GameId != 0).Select(item => item.GameId).ToList();
        _patchStarts = MatchStatsMath.ParsePatches(PatchTitles).Select(patch => patch.Start).ToList();
    }

    public List<string> Asked { get; } = [];

    /// <summary>Answers to other URLs, such as the game's heroes and shop for a sync.</summary>
    public Dictionary<string, Func<JsonNode>> Also { get; } = [];

    /// <summary>Throws what it returns for a URL instead of answering.</summary>
    public Func<string, Exception?>? FailWith { get; set; }

    /// <summary>Answers an empty list, as a half-working endpoint does, for the URLs this returns true for.</summary>
    public Func<string, bool>? AnswerEmpty { get; set; }

    public long BytesReceived { get; private set; }

    public Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default)
    {
        Asked.Add(url);
        if (FailWith?.Invoke(url) is { } failure)
            throw failure;
        JsonNode answer = url switch
        {
            _ when AnswerEmpty?.Invoke(url) == true => new JsonArray(),
            _ when Also.TryGetValue(url, out var also) => also(),
            MatchStatsService.Patches => new JsonArray(PatchTitles.Select(title => (JsonNode)new JsonObject { ["title"] = title }).ToArray()),
            MatchStatsService.Ranks => new JsonArray(
                new[] { "Obscurus", "Initiate", "Seeker", "Acolyte", "Sentinel", "Mystic", "Ritualist", "Emissary", "Oracle", "Phantom", "Ascendant", "Eternus" }
                    .Select((name, tier) => (JsonNode)new JsonObject { ["tier"] = tier, ["name"] = name })
                    .ToArray()),
            _ => Analytics(url),
        };
        BytesReceived += answer.ToJsonString().Length;
        return Task.FromResult<JsonNode?>(answer);
    }

    public Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ChangedFile> GetBytesIfChangedAsync(string url, string userAgent, string? etag, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DownloadAsync(string url, string userAgent, Stream destination, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>The parameters an /item-stats URL was asked with.</summary>
    public static Dictionary<string, string> Query(string url) =>
        url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&')
            .Select(pair => pair.Split('='))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private JsonArray Analytics(string url)
    {
        var heroStats = url.StartsWith($"{MatchStatsService.Analytics}/hero-stats?", StringComparison.Ordinal);
        if (!heroStats && !url.StartsWith($"{MatchStatsService.Analytics}/item-stats?", StringComparison.Ordinal))
            throw new InvalidOperationException($"Not an API this fake answers: {url}");
        var query = Query(url);
        long? Get(string key) => query.TryGetValue(key, out var value) ? long.Parse(value, CultureInfo.InvariantCulture) : null;
        var (from, until) = (Get("min_unix_timestamp") ?? 0, Get("max_unix_timestamp") ?? long.MaxValue);
        var (minBadge, maxBadge) = (Get("min_average_badge"), Get("max_average_badge"));
        var enemy = Get("enemy_hero_ids");
        var ranked = query.GetValueOrDefault("match_mode") == "ranked";
        var byHero = query.GetValueOrDefault("bucket") == "hero";

        // Every day's matches happen at midday; a day counts if that's inside the window.
        var daysByPatch = new Dictionary<int, long>();
        for (var day = from / 86400; day <= Math.Min(until, Now.ToUnixTimeSeconds()) / 86400; day++)
        {
            var midday = day * 86400 + 43200;
            var patch = _patchStarts.FindIndex(start => start <= midday);
            if (midday >= from && midday <= until && patch >= 0)
                daysByPatch[patch] = daysByPatch.GetValueOrDefault(patch) + 1;
        }
        // A rank group's matches all sit at its middle badge; unranked matches have none, so any rank filter
        // leaves them out, and ranked matches are the ones with a badge.
        var groups = Enumerable.Range(0, _groupWeights.Length)
            .Where(group => minBadge is null && maxBadge is null && !ranked
                            || group > 0 && group * 10 + 5 >= (minBadge ?? 0) && group * 10 + 5 <= (maxBadge ?? long.MaxValue))
            .ToList();

        if (heroStats)
            return HeroStats(daysByPatch, groups);

        var totals = new Dictionary<(long Bucket, long Item), (long Wins, long Matches)>();
        foreach (var (patch, days) in daysByPatch)
        {
            foreach (var group in groups)
            {
                var day = DayOf(patch, group);
                var subjects = enemy is { } id
                    ? new[] { (Hero: 0L, Cells: day.Against.GetValueOrDefault(id) ?? []) }
                    : day.ByHero.Select(pair => (Hero: pair.Key, Cells: pair.Value));
                foreach (var (hero, cells) in subjects)
                {
                    foreach (var (item, (wins, matches)) in cells)
                    {
                        var key = (byHero ? hero : 0, item);
                        var current = totals.GetValueOrDefault(key);
                        totals[key] = (current.Wins + days * wins, current.Matches + days * matches);
                    }
                }
            }
        }
        return new JsonArray(totals.Select(pair =>
        {
            var row = new JsonObject { ["item_id"] = pair.Key.Item };
            if (byHero)
                row["bucket"] = pair.Key.Bucket;
            row["wins"] = pair.Value.Wins;
            row["matches"] = pair.Value.Matches;
            return (JsonNode)row;
        }).ToArray());
    }

    /// <summary>Each hero's matches over the days asked about, every hero in one answer, as /hero-stats gives them.</summary>
    private JsonArray HeroStats(Dictionary<int, long> daysByPatch, List<int> groups)
    {
        var totals = new Dictionary<long, (long Wins, long Matches)>();
        foreach (var (patch, days) in daysByPatch)
        {
            foreach (var group in groups)
            {
                var day = DayOf(patch, group);
                foreach (var (hero, (wins, matches)) in day.Heroes)
                {
                    var current = totals.GetValueOrDefault(hero);
                    totals[hero] = (current.Wins + days * wins, current.Matches + days * matches);
                }
            }
        }
        return new JsonArray(totals.Select(pair => (JsonNode)new JsonObject
        {
            ["hero_id"] = pair.Key,
            ["wins"] = pair.Value.Wins,
            ["losses"] = pair.Value.Matches - pair.Value.Wins,
            ["matches"] = pair.Value.Matches,
        }).ToArray());
    }

    /// <summary>One day's matches in one patch and rank group: purchases by the buyer's hero and by an enemy they faced, and each hero's matches.</summary>
    private sealed record Day(
        Dictionary<long, Dictionary<long, (long Wins, long Matches)>> ByHero,
        Dictionary<long, Dictionary<long, (long Wins, long Matches)>> Against,
        Dictionary<long, (long Wins, long Matches)> Heroes);

    private Day DayOf(int patch, int group)
    {
        if (_days.TryGetValue((patch, group), out var cached))
            return cached;

        var byHero = new Dictionary<long, Dictionary<long, (long, long)>>();
        var heroes = new Dictionary<long, (long, long)>();
        foreach (var hero in _heroes)
        {
            var cells = new Dictionary<long, (long, long)>();
            foreach (var item in _items)
            {
                var matches = _groupWeights[group] * (1 + hero % 5) * (1 + item % 13);
                var rate = Rate(patch, group, hero, item);
                cells[item] = ((long)(matches * rate), matches);
            }
            byHero[hero] = cells;
            // Every buyer of the most-bought item, and a few who bought none of these.
            var heroMatches = _groupWeights[group] * (1 + hero % 5) * 16;
            heroes[hero] = ((long)(heroMatches * (0.5 + 0.01 * Math.Sin(hero + group))), heroMatches);
        }

        // About a sixth of every other hero's buyers face a given enemy, and the enemy moves their win rates.
        var against = new Dictionary<long, Dictionary<long, (long, long)>>();
        foreach (var enemy in _heroes)
        {
            var cells = new Dictionary<long, (long, long)>();
            foreach (var hero in _heroes.Where(hero => hero != enemy))
            {
                foreach (var item in _items)
                {
                    var matches = _groupWeights[group] * (1 + hero % 5) * (1 + item % 13) / 6;
                    var rate = Rate(patch, group, hero, item) + 0.01 * Math.Sin(enemy * 0.9 + item * 0.4);
                    var (wins, total) = cells.GetValueOrDefault(item);
                    cells[item] = (wins + (long)(matches * rate), total + matches);
                }
            }
            against[enemy] = cells;
        }
        return _days[(patch, group)] = new Day(byHero, against, heroes);
    }

    /// <summary>A buyer's chance to win with an item, before any enemy: Emissary (tier 7) and up build to a shifted pattern.</summary>
    private static double Rate(int patch, int group, long hero, long item) =>
        0.5 + 0.03 * Math.Sin(hero * 0.7 + item * 0.3 + (group >= 7 ? 1.5 : 0)) + 0.01 * Math.Sin(item * 1.1 + patch);

    /// <summary>A service on this API's clock that waits for nothing, recording the waits it asked for.</summary>
    public MatchStatsService Service(List<TimeSpan>? waits = null) =>
        new(this, () => Now, (wait, _) =>
        {
            waits?.Add(wait);
            return Task.CompletedTask;
        });

    /// <summary>A whole download into <paramref name="store"/>, as the Data menu runs one; the last phase's result.</summary>
    public static async Task<FetchResult> DownloadAsync(DataStore store, bool includeRanks = true)
    {
        var service = new SyntheticItemStatsApi(store).Service();
        var plan = await service.PlanAsync(store, includeRanks);
        FetchResult? result = null;
        await service.FetchAsync(store, plan, null, segment => result = service.Apply(store, segment, plan.Keep), CancellationToken.None);
        return result!;
    }
}
