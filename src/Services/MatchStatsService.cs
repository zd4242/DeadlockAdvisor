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
/// <param name="Text">What's being fetched now: "Hero portraits: Haze".</param>
public readonly record struct FetchProgress(int Done, int Total, string Text);

/// <summary>A pause the API asked for, or a retry after its error.</summary>
/// <param name="Reason">"deadlock-api.com asked to slow down".</param>
public sealed record FetchWait(string Reason, TimeSpan Length);

/// <summary>Where a match data download is: overall, and within the phase it's on.</summary>
/// <param name="Phase">Index into the plan's phases.</param>
/// <param name="Done">Calls finished in the whole download, of <paramref name="Total"/>.</param>
/// <param name="Text">What was asked last: "09-29 · every match · Enemies: Haze".</param>
/// <param name="Bytes">Received so far, as it came over the wire.</param>
/// <param name="Wait">Set while the download waits out a rate limit or an error.</param>
public sealed record MatchFetchProgress(int Phase, int PhaseDone, int PhaseTotal, int Done, int Total, string Text, long Bytes, FetchWait? Wait = null)
{
    public FetchProgress Overall => new(Done, Total, Text);
}

public interface IMatchStatsService
{
    /// <summary>The patches, newest first: one cheap call. Throws <see cref="InvalidOperationException"/> if there are none.</summary>
    Task<IReadOnlyList<Patch>> PatchesAsync(CancellationToken cancellationToken = default);

    /// <summary>What a download would fetch now, given the patches.</summary>
    MatchFetchPlan Plan(DataStore store, IReadOnlyList<Patch> patches, bool includeRanks);

    /// <summary>
    /// Fetch <paramref name="plan"/> phase by phase, handing each finished phase's segment to
    /// <paramref name="finished"/> before the next starts, so a download stopped part-way keeps what it
    /// finished. Only reads the store. Throws <see cref="OperationCanceledException"/>, an HTTP / timeout
    /// error, or whatever <paramref name="finished"/> throws.
    /// </summary>
    Task FetchAsync(DataStore store, MatchFetchPlan plan, IProgress<MatchFetchProgress>? progress, Action<MatchSegment> finished,
        CancellationToken cancellationToken);

    /// <summary>
    /// Put a fetched segment into the store in place of that patch's, drop the patches not in
    /// <paramref name="keep"/>, work the lifts out again for the rank range the data was set to, and write it all out.
    /// </summary>
    FetchResult Apply(DataStore store, MatchSegment segment, IEnumerable<Patch> keep);

    /// <summary>
    /// Work the lifts out again from the stored counts for another rank range (null: every match), and
    /// write them out. No network. Throws <see cref="InvalidOperationException"/> without counts.
    /// </summary>
    FetchResult Reanalyse(DataStore store, RankRange? range);

    /// <summary>The latest patch if it's newer than the one the data was fetched under: one cheap call, the startup check.</summary>
    Task<Patch?> NewerPatchAsync(JsonObject meta, CancellationToken cancellationToken = default);
}

public static class MatchStatsServiceExtensions
{
    /// <summary>What a download would fetch now, from the patch list: one cheap call.</summary>
    public static async Task<MatchFetchPlan> PlanAsync(this IMatchStatsService service, DataStore store, bool includeRanks,
        CancellationToken cancellationToken = default) =>
        service.Plan(store, await service.PatchesAsync(cancellationToken), includeRanks);
}

/// <summary>
/// The network side of the match data (the maths is <see cref="MatchStatsMath"/>): /v1/patches for the
/// windows and /v1/assets/ranks for the rank names, then /v1/analytics/item-stats per patch, half-window
/// and rank group: every match once, every hero's own purchases at once, and what everyone facing each
/// hero bought. Paced well under the API's 200 requests a minute.
/// </summary>
public sealed class MatchStatsService : IMatchStatsService
{
    public const string Analytics = "https://api.deadlock-api.com/v1/analytics";
    public const string Patches = "https://api.deadlock-api.com/v1/patches";
    public const string Ranks = "https://api.deadlock-api.com/v1/assets/ranks";

    public static readonly TimeSpan RequestGap = TimeSpan.FromSeconds(0.4);
    public const int MaxInFlight = 2;
    public static readonly TimeSpan RateLimitWait = TimeSpan.FromSeconds(30);
    public const int RateLimitRetries = 3;
    public static readonly TimeSpan ServerErrorWait = TimeSpan.FromSeconds(5);
    public const int ServerErrorRetries = 1;

    private readonly IDeadlockApi _api;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

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

    private long Now => _utcNow().ToUnixTimeSeconds();

    /// <summary>The heroes a download asks about: the ones the game knows by an id.</summary>
    public static List<Hero> Heroes(DataStore store) => store.HeroesSorted().Where(hero => hero.GameId != 0).ToList();

    public async Task<IReadOnlyList<Patch>> PatchesAsync(CancellationToken cancellationToken = default)
    {
        var patches = await FetchPatchesAsync(cancellationToken);
        return patches.Count > 0 ? patches : throw new InvalidOperationException("Couldn't read any patch dates from /v1/patches.");
    }

    public MatchFetchPlan Plan(DataStore store, IReadOnlyList<Patch> patches, bool includeRanks) =>
        MatchFetchPlan.For(store.MatchSegments, patches, Now, includeRanks, Heroes(store).Count);

    public async Task FetchAsync(DataStore store, MatchFetchPlan plan, IProgress<MatchFetchProgress>? progress, Action<MatchSegment> finished,
        CancellationToken cancellationToken)
    {
        var heroes = Heroes(store);
        var ranks = plan.IncludesRanks ? MatchStatsMath.RankBuckets(await FetchRankNamesAsync(cancellationToken)) : [];
        var pacer = new RequestPacer(RequestGap, MaxInFlight, () => _clock.Elapsed, _delay);
        var run = new Run(plan, progress, _api);
        var fetched = new Dictionary<long, MatchSegment>();

        for (var index = 0; index < plan.Phases.Count; index++)
        {
            var phase = plan.Phases[index];
            run.Start(index);
            MatchSegment segment;
            if (phase.Part == FetchPart.EveryMatch)
            {
                var everyMatch = await FetchSliceAsync(pacer, run, phase, null, heroes, cancellationToken);
                segment = new MatchSegment(phase.Patch, phase.From, phase.Until, phase.Ended, plan.Now, everyMatch, [], []);
            }
            else
            {
                // The rank groups go with every match over the same window: fetched earlier in this
                // download, or, for a patch that's over, the one already stored.
                var basis = fetched.GetValueOrDefault(phase.Patch.Start)
                            ?? store.MatchSegments.FirstOrDefault(stored => stored.Patch.Start == phase.Patch.Start && stored.Until == phase.Until)
                            ?? throw new InvalidOperationException($"No every-match counts for patch {phase.Patch.Label} to add rank groups to.");
                var byRank = new List<SliceCounts>();
                foreach (var rank in ranks)
                    byRank.Add(await FetchSliceAsync(pacer, run, phase, rank, heroes, cancellationToken));
                segment = basis with { Ranks = ranks, ByRank = byRank };
            }
            fetched[phase.Patch.Start] = segment;
            finished(segment);
        }
    }

    /// <summary>A download's count of calls, shared by its requests as they finish.</summary>
    private sealed class Run(MatchFetchPlan plan, IProgress<MatchFetchProgress>? progress, IDeadlockApi api)
    {
        private readonly long _bytesAtStart = api.BytesReceived;
        private int _phase;
        private int _phaseDone;
        private int _done;

        public void Start(int phase)
        {
            _phase = phase;
            _phaseDone = 0;
        }

        public void Report(string text, bool finishedCall, FetchWait? wait = null)
        {
            if (finishedCall)
            {
                Interlocked.Increment(ref _phaseDone);
                Interlocked.Increment(ref _done);
            }
            progress?.Report(new MatchFetchProgress(_phase, _phaseDone, plan.Phases[_phase].Calls, _done, plan.Calls, text,
                api.BytesReceived - _bytesAtStart, wait));
        }
    }

    /// <summary>
    /// Every match, or one rank group, over a phase's window: both halves of every match, of every hero's
    /// own purchases, and of what the players facing each hero bought.
    /// </summary>
    private async Task<SliceCounts> FetchSliceAsync(RequestPacer pacer, Run run, FetchPhase phase, RankBucket? rank,
        IReadOnlyList<Hero> heroes, CancellationToken cancellationToken)
    {
        var group = rank?.Name ?? "every match";
        var subjects = new List<(string Text, OrderedDictionary<string, string> Parameters)>
        {
            ("all matches", MatchStatsMath.BaselineParams()),
            ("Your hero", MatchStatsMath.AsParams()),
        };
        subjects.AddRange(heroes.Select(hero => ($"Enemies: {hero.HeroName}", MatchStatsMath.AgainstParams(hero.GameId))));

        var mid = MatchStatsMath.Midpoint(phase.From, phase.Until);
        // Both bounds are inclusive, so the first half stops a second short: no match counts twice.
        (long From, long Until)[] halves = [(phase.From, mid - 1), (mid, phase.Until)];
        var requests = new List<Func<CancellationToken, Task<JsonArray>>>();
        foreach (var (text, parameters) in subjects)
        {
            var filtered = rank is null ? parameters : MatchStatsMath.RankParams(parameters, rank);
            foreach (var (from, until) in halves)
            {
                var url = ItemStatsUrl(filtered, from, until);
                var label = $"{phase.Patch.Label} · {group} · {text}";
                requests.Add(token => GetItemStatsAsync(pacer, run, url, label, token));
            }
        }
        var answers = await pacer.RunAsync(requests, cancellationToken);

        static Dictionary<long, WinTotals> Totals(JsonArray rows) =>
            MatchStatsMath.Totals(rows.Select(row => (JsonRecord.Int(row, "item_id"), JsonRecord.Int(row, "wins"), JsonRecord.Int(row, "matches"))));
        Halves HalvesAt(int subject) => new(Totals(answers[2 * subject]), Totals(answers[2 * subject + 1]));

        // Bucketed by the buyer's hero; a hero the store doesn't know is only in every match.
        var asFirst = BucketTotals(answers[2]);
        var asSecond = BucketTotals(answers[3]);
        var mine = new OrderedDictionary<string, Halves>();
        foreach (var hero in heroes)
            mine[hero.HeroId] = new Halves(asFirst.GetValueOrDefault(hero.GameId) ?? [], asSecond.GetValueOrDefault(hero.GameId) ?? []);
        var against = new OrderedDictionary<string, Halves>();
        for (var i = 0; i < heroes.Count; i++)
            against[heroes[i].HeroId] = HalvesAt(i + 2);
        return new SliceCounts(HalvesAt(0), mine, against);
    }

    private static Dictionary<long, Dictionary<long, WinTotals>> BucketTotals(JsonArray rows) =>
        MatchStatsMath.BucketTotals(rows.Select(row =>
            (JsonRecord.Int(row, "bucket"), JsonRecord.Int(row, "item_id"), JsonRecord.Int(row, "wins"), JsonRecord.Int(row, "matches"))));

    /// <summary>
    /// The API leaves out an item with under 20 matches unless told otherwise, and the rank groups are
    /// added up and taken away from every match, so every match has to count. Never with a time bucket:
    /// over a two-patch window its day-bucketed answers came back silently empty or cut short.
    /// </summary>
    public static string ItemStatsUrl(OrderedDictionary<string, string> parameters, long from, long until)
    {
        var query = new OrderedDictionary<string, string>(parameters)
        {
            ["min_matches"] = "1",
            ["min_unix_timestamp"] = from.ToString(CultureInfo.InvariantCulture),
            ["max_unix_timestamp"] = until.ToString(CultureInfo.InvariantCulture),
        };
        return $"{Analytics}/item-stats?" + string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    /// <summary>One /item-stats call in its turn. Waits out a rate limit and retries a server error once; anything else throws.</summary>
    private async Task<JsonArray> GetItemStatsAsync(RequestPacer pacer, Run run, string url, string text, CancellationToken cancellationToken)
    {
        var rateLimited = 0;
        var serverErrors = 0;
        while (true)
        {
            FetchWait wait;
            try
            {
                var rows = await _api.GetJsonAsync(url, cancellationToken) as JsonArray ?? [];
                run.Report(text, finishedCall: true);
                return rows;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && rateLimited < RateLimitRetries)
            {
                rateLimited++;
                wait = new FetchWait("deadlock-api.com asked to slow down", RateLimitWait);
                pacer.Pause(RateLimitWait);
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode >= 500 && serverErrors < ServerErrorRetries)
            {
                serverErrors++;
                wait = new FetchWait($"deadlock-api.com had an error ({(int)ex.StatusCode!}), trying again", ServerErrorWait);
            }
            run.Report(text, finishedCall: false, wait);
            await _delay(wait.Length, cancellationToken);
        }
    }

    public FetchResult Apply(DataStore store, MatchSegment segment, IEnumerable<Patch> keep)
    {
        store.PutMatchSegment(segment);
        store.SaveMatchSegment(segment);
        store.PruneMatchSegments(keep.Select(patch => patch.Start).ToHashSet());
        return Reanalyse(store, MatchStatsMath.RankOf(store.MatchMeta));
    }

    public FetchResult Reanalyse(DataStore store, RankRange? range)
    {
        if (store.MatchSegments.Count == 0)
            throw new InvalidOperationException("There are no downloaded match counts to work the lifts out from.");
        var result = MatchStatsMath.Analyse(store.MatchSegments, range, store.Items.Values);
        store.MatchLift = new OrderedDictionary<MatchLiftKey, MatchLift>(result.Lifts);
        store.MatchMeta = result.Meta();
        store.SaveMatchLift();
        return result;
    }

    public async Task<Patch?> NewerPatchAsync(JsonObject meta, CancellationToken cancellationToken = default)
    {
        // Without data from a known patch there's nothing to be out of date against, and no call to make.
        if (!JsonRecord.Truthy(JsonRecord.Get(JsonRecord.Get(meta, "latest_patch"), "start")))
            return null;
        return MatchStatsMath.NewerPatch(meta, await FetchPatchesAsync(cancellationToken));
    }

    private async Task<List<Patch>> FetchPatchesAsync(CancellationToken cancellationToken)
    {
        var records = await _api.GetJsonAsync(Patches, cancellationToken) as JsonArray ?? [];
        return MatchStatsMath.ParsePatches(records.Select(record => JsonRecord.Get(record, "title") is JsonValue title
                                                                   && title.TryGetValue<string>(out var text) ? text : null));
    }

    /// <summary>Tier → name, as the game names its ranks now.</summary>
    private async Task<Dictionary<int, string>> FetchRankNamesAsync(CancellationToken cancellationToken)
    {
        var names = new Dictionary<int, string>();
        foreach (var rank in await _api.GetJsonAsync(Ranks, cancellationToken) as JsonArray ?? [])
        {
            if (JsonRecord.Get(rank, "name") is JsonValue name && name.TryGetValue<string>(out var text))
                names[(int)JsonRecord.Int(rank, "tier")] = text;
        }
        return names;
    }
}
