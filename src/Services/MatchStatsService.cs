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
    /// Fetch and analyse every family: about 230 calls, a few minutes. Only reads the store; nothing
    /// is written until <see cref="Apply"/>, so a cancelled or failed run leaves nothing half-done.
    /// Throws <see cref="OperationCanceledException"/>, an HTTP / timeout error, or
    /// <see cref="InvalidOperationException"/> if no patch date can be read.
    /// </summary>
    Task<FetchResult> FetchAsync(DataStore store, IProgress<FetchProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Put a finished fetch into the store and write it out.</summary>
    void Apply(DataStore store, FetchResult result);

    /// <summary>The latest patch if it's newer than the one the data was fetched under: one cheap call, the startup check.</summary>
    Task<Patch?> NewerPatchAsync(JsonObject meta, CancellationToken cancellationToken = default);
}

/// <summary>
/// The network side of the match data (the maths is <see cref="MatchStatsMath"/>): /v1/patches for the
/// windows, then /v1/analytics/item-stats per family, hero and half-window, paced well under the
/// API's 200 requests a minute.
/// </summary>
public sealed class MatchStatsService : IMatchStatsService
{
    public const string Analytics = "https://api.deadlock-api.com/v1/analytics";
    public const string Patches = "https://api.deadlock-api.com/v1/patches";

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

    public async Task<FetchResult> FetchAsync(DataStore store, IProgress<FetchProgress>? progress, CancellationToken cancellationToken)
    {
        var patches = await FetchPatchesAsync(cancellationToken);
        if (patches.Count == 0)
            throw new InvalidOperationException("Couldn't read any patch dates from /v1/patches.");
        var now = Now;
        var heroes = store.HeroesSorted().Where(hero => hero.GameId != 0).ToList();
        var byGameId = new Dictionary<long, Item>();
        foreach (var item in store.Items.Values.Where(item => item.GameId != 0))
            byGameId[item.GameId] = item;
        var total = MatchStatsMath.Families.Count * (heroes.Count + 1);
        var done = 0;

        void Step(string text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new FetchProgress(done, total, text));
            done++;
        }

        var lifts = new OrderedDictionary<MatchLiftKey, MatchLift>();
        var reports = new List<FamilyReport>();
        foreach (var family in MatchStatsMath.Families)
        {
            var since = MatchStatsMath.WindowStart(patches, family, now);
            var scopeTiers = MatchStatsMath.ScopeTiers(family.Scope);
            var tiers = byGameId.Where(pair => scopeTiers.Contains(pair.Value.Tier)).ToDictionary(pair => pair.Key, pair => pair.Value.Tier);
            var mid = MatchStatsMath.Midpoint(since.Start, now);

            Step($"{family.Key}: all matches");
            var baseline = await FetchHalvesAsync(MatchStatsMath.QueryParams(family.Relation, family.Scope), since.Start, mid, cancellationToken);
            var heroHalves = new OrderedDictionary<string, Halves>();
            foreach (var hero in heroes)
            {
                Step($"{family.Key}: {hero.HeroName}");
                heroHalves[hero.HeroId] = await FetchHalvesAsync(
                    MatchStatsMath.QueryParams(family.Relation, family.Scope, hero.GameId), since.Start, mid, cancellationToken);
            }

            var report = new FamilyReport(family, since, MatchStatsMath.AnalyseFamily(baseline, heroHalves, tiers));
            reports.Add(report);
            if (!report.Kept)
                continue;
            foreach (var (key, lift) in report.Stats.Full)
            {
                var itemId = byGameId[key.GameItemId].ItemId;
                lifts[new MatchLiftKey(itemId, key.HeroId, family.Relation, family.Scope)] = new MatchLift(
                    itemId, key.HeroId, family.Relation, family.Scope, lift.Matches, lift.Lift, lift.Se,
                    MatchStatsMath.Shrink(lift, report.Stats.Tau2));
            }
        }
        progress?.Report(new FetchProgress(total, total, "done"));
        return new FetchResult(lifts, reports, patches[0], (long)Math.Truncate(Now));
    }

    public void Apply(DataStore store, FetchResult result)
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
        var query = new OrderedDictionary<string, string>(parameters)
        {
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
