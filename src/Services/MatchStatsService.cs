using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.GameApi;

namespace DeadlockAdvisor.Services;

/// <param name="Done">Steps finished, of <paramref name="Total"/>.</param>
/// <param name="Text">What's being fetched now: "as/full: Haze".</param>
public readonly record struct FetchProgress(int Done, int Total, string Text);

public interface IMatchStatsService
{
    /// <summary>
    /// Download every family, split by rank: about 2,600 calls, a quarter of an hour. Only reads the
    /// store; nothing is written until <see cref="Apply"/>, so a cancelled or failed run leaves nothing
    /// half-done. Throws <see cref="OperationCanceledException"/>, an HTTP / timeout error, or
    /// <see cref="InvalidOperationException"/> if no patch date can be read.
    /// </summary>
    Task<MatchCounts> FetchAsync(DataStore store, IProgress<FetchProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Put a finished download into the store, worked out for the rank range the data was set to, and write it all out.</summary>
    FetchResult Apply(DataStore store, MatchCounts counts);

    /// <summary>
    /// Work the lifts out again from the downloaded counts for another rank range (null: every match),
    /// and write them out. No network. Throws <see cref="InvalidOperationException"/> without counts.
    /// </summary>
    FetchResult Refilter(DataStore store, RankRange? range);

    /// <summary>The latest patch if it's newer than the one the data was fetched under: one cheap call, the startup check.</summary>
    Task<Patch?> NewerPatchAsync(JsonObject meta, CancellationToken cancellationToken = default);
}

/// <summary>
/// The network side of the match data (the maths is <see cref="MatchStatsMath"/>): /v1/patches for the
/// windows and /v1/assets/ranks for the rank names, then /v1/analytics/item-stats per family, hero,
/// half-window and rank group, paced well under the API's 200 requests a minute.
/// </summary>
public sealed class MatchStatsService : IMatchStatsService
{
    public const string Analytics = "https://api.deadlock-api.com/v1/analytics";
    public const string Patches = "https://api.deadlock-api.com/v1/patches";
    public const string Ranks = "https://api.deadlock-api.com/v1/assets/ranks";

    public static readonly TimeSpan RequestGap = TimeSpan.FromSeconds(0.4);
    public static readonly TimeSpan RateLimitWait = TimeSpan.FromSeconds(30);
    public const int RateLimitRetries = 3;
    public static readonly TimeSpan ServerErrorWait = TimeSpan.FromSeconds(5);
    public const int ServerErrorRetries = 1;

    private readonly IDeadlockApi _api;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _lastRequest;

    public MatchStatsService(IDeadlockApi api)
        : this(api, () => DateTimeOffset.UtcNow, Task.Delay)
    {
    }

    internal MatchStatsService(IDeadlockApi api, Func<DateTimeOffset> utcNow, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _api = api;
        _utcNow = utcNow;
        _delay = delay;
    }

    private double Now => _utcNow().ToUnixTimeMilliseconds() / 1000.0;

    public async Task<MatchCounts> FetchAsync(DataStore store, IProgress<FetchProgress>? progress, CancellationToken cancellationToken)
    {
        var patches = await FetchPatchesAsync(cancellationToken);
        if (patches.Count == 0)
            throw new InvalidOperationException("Couldn't read any patch dates from /v1/patches.");
        var ranks = MatchStatsMath.RankBuckets(await FetchRankNamesAsync(cancellationToken));
        var now = Now;
        var heroes = store.HeroesSorted().Where(hero => hero.GameId != 0).ToList();
        var total = MatchStatsMath.Families.Count * (heroes.Count + 1) * (ranks.Count + 1);
        var done = 0;

        void Step(string text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new FetchProgress(done, total, text));
            done++;
        }

        var families = new List<FamilyCounts>();
        foreach (var family in MatchStatsMath.Families)
        {
            var since = MatchStatsMath.WindowStart(patches, family, now);
            var mid = MatchStatsMath.Midpoint(since.Start, now);

            // Every match, then each rank group on its own: unranked matches have no rank, so the
            // groups don't add up to every match.
            async Task<RankedHalves> FetchRankedAsync(string subject, long? heroGameId)
            {
                var parameters = MatchStatsMath.QueryParams(family.Relation, family.Scope, heroGameId);
                Step($"{family.Key}: {subject}");
                var all = await FetchHalvesAsync(parameters, since.Start, mid, cancellationToken);
                var byRank = new List<Halves>();
                foreach (var rank in ranks)
                {
                    Step($"{family.Key}: {subject} · {rank.Name}");
                    byRank.Add(await FetchHalvesAsync(MatchStatsMath.RankParams(parameters, rank), since.Start, mid, cancellationToken));
                }
                return new RankedHalves(
                    new RankedTotals(all.First, byRank.Select(halves => halves.First).ToList()),
                    new RankedTotals(all.Second, byRank.Select(halves => halves.Second).ToList()));
            }

            var baseline = await FetchRankedAsync("all matches", null);
            var heroCounts = new OrderedDictionary<string, RankedHalves>();
            foreach (var hero in heroes)
                heroCounts[hero.HeroId] = await FetchRankedAsync(hero.HeroName, hero.GameId);
            families.Add(new FamilyCounts(family, since, baseline, heroCounts));
        }
        progress?.Report(new FetchProgress(total, total, "done"));
        return new MatchCounts((long)Math.Truncate(Now), patches[0], ranks, families);
    }

    public FetchResult Apply(DataStore store, MatchCounts counts)
    {
        var result = MatchStatsMath.Analyse(counts, MatchStatsMath.RankOf(store.MatchMeta), store.Items.Values);
        store.MatchCounts = counts;
        store.SaveMatchCounts();
        PutLifts(store, result);
        return result;
    }

    public FetchResult Refilter(DataStore store, RankRange? range)
    {
        var counts = store.MatchCounts ?? throw new InvalidOperationException("There are no downloaded match counts to filter by rank.");
        var result = MatchStatsMath.Analyse(counts, range, store.Items.Values);
        PutLifts(store, result);
        return result;
    }

    private static void PutLifts(DataStore store, FetchResult result)
    {
        store.MatchLift = new OrderedDictionary<MatchLiftKey, MatchLift>(result.Lifts);
        store.MatchMeta = result.Meta();
        store.SaveMatchLift();
    }

    public async Task<Patch?> NewerPatchAsync(JsonObject meta, CancellationToken cancellationToken = default)
    {
        // Without data from a known patch there's nothing to be out of date against, and no call to make.
        if (!PyJson.Truthy(PyJson.Get(PyJson.Get(meta, "latest_patch"), "start")))
            return null;
        return MatchStatsMath.NewerPatch(meta, await FetchPatchesAsync(cancellationToken));
    }

    private async Task<List<Patch>> FetchPatchesAsync(CancellationToken cancellationToken)
    {
        var records = await _api.GetJsonAsync(Patches, cancellationToken) as JsonArray ?? [];
        return MatchStatsMath.ParsePatches(records.Select(record => PyJson.Get(record, "title") is JsonValue title
                                                                   && title.TryGetValue<string>(out var text) ? text : null));
    }

    /// <summary>Tier → name, as the game names its ranks now.</summary>
    private async Task<Dictionary<int, string>> FetchRankNamesAsync(CancellationToken cancellationToken)
    {
        var names = new Dictionary<int, string>();
        foreach (var rank in await _api.GetJsonAsync(Ranks, cancellationToken) as JsonArray ?? [])
        {
            if (PyJson.Get(rank, "name") is JsonValue name && name.TryGetValue<string>(out var text))
                names[(int)PyJson.Int(rank, "tier")] = text;
        }
        return names;
    }

    /// <summary>Both halves of a window. Both bounds are inclusive, so the first half stops a second short: no match counts twice.</summary>
    private async Task<Halves> FetchHalvesAsync(OrderedDictionary<string, string> parameters, long since, long mid, CancellationToken cancellationToken)
    {
        var first = MatchStatsMath.Totals(await FetchItemStatsAsync(parameters, since, mid - 1, cancellationToken));
        var second = MatchStatsMath.Totals(await FetchItemStatsAsync(parameters, mid, null, cancellationToken));
        return new Halves(first, second);
    }

    /// <summary>
    /// One /item-stats call, spaced <see cref="RequestGap"/> from the last. Waits out a rate limit
    /// and retries a server error once; anything else throws. Never with the API's bucket option:
    /// over a two-patch window its day-bucketed answers came back silently empty or cut short.
    /// </summary>
    private async Task<List<(long, long, long)>> FetchItemStatsAsync(
        OrderedDictionary<string, string> parameters, long since, long? until, CancellationToken cancellationToken)
    {
        // The API leaves out an item with under 20 matches unless told otherwise, and the rank groups
        // are added up, so every match has to count.
        var query = new OrderedDictionary<string, string>(parameters)
        {
            ["min_matches"] = "1",
            ["min_unix_timestamp"] = since.ToString(CultureInfo.InvariantCulture),
        };
        if (until is { } end)
            query["max_unix_timestamp"] = end.ToString(CultureInfo.InvariantCulture);
        var url = $"{Analytics}/item-stats?" + string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        var rateLimited = 0;
        var serverErrors = 0;
        while (true)
        {
            if (_lastRequest is { } last && last + RequestGap - _clock.Elapsed is var wait && wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            _lastRequest = _clock.Elapsed;
            try
            {
                var rows = await _api.GetJsonAsync(url, cancellationToken) as JsonArray ?? [];
                return rows.Select(row => (PyJson.Int(row, "item_id"), PyJson.Int(row, "wins"), PyJson.Int(row, "matches"))).ToList();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && rateLimited < RateLimitRetries)
            {
                rateLimited++;
                await _delay(RateLimitWait, cancellationToken);
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode >= 500 && serverErrors < ServerErrorRetries)
            {
                serverErrors++;
                await _delay(ServerErrorWait, cancellationToken);
            }
        }
    }
}
