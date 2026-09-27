using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// The fetch replayed against the conversation the Python app had with a synthetic API
/// (export_golden.py's match_stats_fetch), which predates rank groups: every match gets the recorded
/// answer, and each rank group a fixed share of it. Every match must still give the files Python wrote.
/// </summary>
public class MatchStatsServiceTests
{
    /// <summary>
    /// Answers only the recorded URLs, in order, and remembers what was asked. A rank group's query
    /// gets a share of the last answer to the same query over every match, as the fetch asks it next.
    /// </summary>
    private sealed class ReplayApi : IDeadlockApi
    {
        private static readonly string[] _added = ["min_matches", "min_average_badge", "max_average_badge"];

        private readonly Dictionary<string, Queue<JsonNode>> _answers = [];
        private readonly Dictionary<string, JsonArray> _lastAnswer = [];

        public ReplayApi(JsonNode recording)
        {
            foreach (var request in Items(recording["requests"]))
            {
                var url = Text(request["url"]);
                JsonNode answer = request["patches"] is { } patches
                    ? patches.DeepClone()
                    : new JsonArray(Items(request["rows"]).Select(row => (JsonNode)new JsonObject
                    {
                        ["item_id"] = row[0]!.GetValue<long>(),
                        ["wins"] = row[1]!.GetValue<long>(),
                        ["matches"] = row[2]!.GetValue<long>(),
                    }).ToArray());
                if (!_answers.TryGetValue(url, out var queue))
                    _answers[url] = queue = new Queue<JsonNode>();
                queue.Enqueue(answer);
            }
        }

        public List<string> Asked { get; } = [];
        public Func<string, Exception?>? FailWith { get; set; }

        public Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default)
        {
            Asked.Add(url);
            if (FailWith?.Invoke(url) is { } failure)
                throw failure;
            if (url == MatchStatsService.Ranks)
                return Task.FromResult<JsonNode?>(RankAssets());

            var recorded = Recorded(url);
            if (RankTier(url) is { } tier)
                return Task.FromResult<JsonNode?>(Share(_lastAnswer[recorded], tier));
            if (!_answers.TryGetValue(recorded, out var queue) || queue.Count == 0)
                throw new InvalidOperationException($"Python never asked for {recorded}");
            var answer = queue.Dequeue();
            if (answer is JsonArray rows)
                _lastAnswer[recorded] = rows;
            return Task.FromResult<JsonNode?>(answer.DeepClone());
        }

        public Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static JsonArray RankAssets() =>
            new(new[] { "Obscurus", "Initiate", "Seeker", "Acolyte", "Sentinel", "Mystic", "Ritualist", "Emissary", "Oracle", "Phantom", "Ascendant", "Eternus" }
                .Select((name, tier) => (JsonNode)new JsonObject { ["tier"] = tier, ["name"] = name })
                .ToArray());

        /// <summary>The URL as the Python app asked it, without the parameters rank groups added.</summary>
        public static string Recorded(string url)
        {
            var queryStart = url.IndexOf('?', StringComparison.Ordinal);
            if (queryStart < 0)
                return url;
            var kept = url[(queryStart + 1)..].Split('&').Where(pair => !_added.Contains(pair.Split('=')[0]));
            return url[..queryStart] + "?" + string.Join("&", kept);
        }

        public static int? RankTier(string url)
        {
            var min = url.Split('?', '&').FirstOrDefault(pair => pair.StartsWith("min_average_badge=", StringComparison.Ordinal));
            return min is null ? null : Math.Max(1, int.Parse(min.Split('=')[1]) / 10);
        }

        /// <summary>Between 1% and 12% of each item's matches and wins, varying by rank and item, so the groups never add up to every match.</summary>
        private static JsonArray Share(JsonArray rows, int tier) =>
            new(rows.Select(row =>
            {
                var item = row!["item_id"]!.GetValue<long>();
                var percent = tier + item % 3;
                return (JsonNode)new JsonObject
                {
                    ["item_id"] = item,
                    ["wins"] = row["wins"]!.GetValue<long>() * percent / 100,
                    ["matches"] = row["matches"]!.GetValue<long>() * percent / 100,
                };
            }).ToArray());
    }

    private sealed class Collect<T> : IProgress<T>
    {
        public List<T> Seen { get; } = [];
        public Action<T>? OnReport { get; set; }

        public void Report(T value)
        {
            Seen.Add(value);
            OnReport?.Invoke(value);
        }
    }

    private static (MatchStatsService Service, ReplayApi Api, List<TimeSpan> Waits) Replay()
    {
        var recording = Json("match_fetch/requests.json");
        var now = DateTimeOffset.FromUnixTimeMilliseconds((long)(recording["now"]!.GetValue<double>() * 1000));
        var api = new ReplayApi(recording);
        var waits = new List<TimeSpan>();
        var service = new MatchStatsService(api, () => now, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });
        return (service, api, waits);
    }

    /// <summary>A replayed download, as the other tests' data.</summary>
    internal static Task<MatchCounts> ReplayedCountsAsync() => Replay().Service.FetchAsync(LoadStore(), null, CancellationToken.None);

    [Fact]
    public async Task FetchAsksWhatPythonAskedPlusEachRankGroupAndEveryMatchWritesWhatItWrote()
    {
        var (service, api, _) = Replay();
        var progress = new Collect<FetchProgress>();

        var counts = await service.FetchAsync(LoadStore(), progress, CancellationToken.None);

        var recorded = Items(Json("match_fetch/requests.json")["requests"]).Select(request => Text(request["url"])).ToList();
        Assert.Equal([recorded[0], MatchStatsService.Ranks, .. recorded[1..]],
            api.Asked.Where(url => ReplayApi.RankTier(url) is null).Select(ReplayApi.Recorded));
        Assert.All(api.Asked.Where(url => url.Contains("item-stats")), url => Assert.Contains("min_matches=1", url));
        Assert.Equal(10 * (recorded.Count - 1), api.Asked.Count(url => ReplayApi.RankTier(url) is not null));
        Assert.Equal(["Initiate", "Seeker", "Acolyte", "Sentinel", "Mystic", "Ritualist", "Emissary", "Oracle", "Phantom", "Ascendant"],
            counts.Ranks.Select(rank => rank.Name));
        Assert.Equal((0, 116), (counts.Ranks[0].MinBadge, counts.Ranks[^1].MaxBadge));

        const int total = 3 * 39 * 11;
        Assert.Equal(Enumerable.Range(0, total + 1), progress.Seen.Select(step => step.Done));
        Assert.All(progress.Seen, step => Assert.Equal(total, step.Total));
        Assert.Equal(
            ["against/full: all matches", "against/full: all matches · Initiate", "against/full: all matches · Ascendant", "against/full: Abrams"],
            new[] { 0, 1, 10, 11 }.Select(i => progress.Seen[i].Text));
        Assert.Equal("done", progress.Seen[^1].Text);

        using var data = CopyData();
        var target = DataStore.Load(data.Path);
        var result = service.Apply(target, counts);
        var expected = Json("match_fetch/result.json");
        Assert.Equal(expected["lines"]!.AsArray().Select(Text), result.Lines());
        Assert.Equal(expected["lift_count"]!.GetValue<int>(), result.Lifts.Count);
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.csv"), data.File(DataStore.MatchLiftFile));
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.meta.json"), data.File(DataStore.MatchMetaFile));
        Assert.True(File.Exists(data.File(DataStore.MatchCountsFile)));
    }

    [Fact]
    public async Task ARankRangeIsWorkedOutFromTheSavedCountsWithoutTheNetwork()
    {
        var (service, api, _) = Replay();
        var counts = await service.FetchAsync(LoadStore(), null, CancellationToken.None);
        using var data = CopyData();
        var every = service.Apply(DataStore.Load(data.Path), counts);
        var asked = api.Asked.Count;

        // Straight from disk, as after a restart.
        var store = DataStore.Load(data.Path);
        Assert.Equal(counts.ToJsonBytes(), store.MatchCounts!.ToJsonBytes());
        var mystic = service.Refilter(store, new RankRange(5, 10));

        Assert.Equal(asked, api.Asked.Count);
        Assert.Equal("Mystic+", mystic.RankLabel);
        Assert.Equal("Ranked matches only: Mystic+.", mystic.Lines()[0]);
        Assert.NotEqual(every.Lifts.Values.Select(lift => lift.Lift), mystic.Lifts.Values.Select(lift => lift.Lift));
        var saved = DataStore.Load(data.Path);
        Assert.Equal(new RankRange(5, 10), MatchStatsMath.RankOf(saved.MatchMeta));
        Assert.Equal("Mystic+", MatchStatsMath.RankLabel(saved.MatchMeta));
        Assert.Equal(mystic.Lifts.Count, saved.MatchLift.Count);

        // A fresh download keeps the range the data was set to.
        Assert.Equal(new RankRange(5, 10), service.Apply(saved, counts).Rank);

        service.Refilter(saved, null);
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.csv"), data.File(DataStore.MatchLiftFile));
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.meta.json"), data.File(DataStore.MatchMetaFile));
    }

    [Fact]
    public void RefilteringWithoutCountsThrows()
    {
        var (service, _, _) = Replay();
        var store = LoadStore();

        Assert.Null(store.MatchCounts);
        Assert.Throws<InvalidOperationException>(() => service.Refilter(store, new RankRange(5, 10)));
    }

    [Fact]
    public async Task ARateLimitIsWaitedOutAndRetried()
    {
        var (service, api, waits) = Replay();
        var refused = 0;
        api.FailWith = url => url.Contains("item-stats") && refused++ < 2
            ? new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests)
            : null;

        await service.FetchAsync(LoadStore(), null, CancellationToken.None);

        Assert.Equal(2, waits.Count(wait => wait == MatchStatsService.RateLimitWait));
    }

    [Fact]
    public async Task AClientErrorIsNotRetried()
    {
        var (service, api, _) = Replay();
        api.FailWith = url => url.Contains("item-stats") ? new HttpRequestException("nope", null, HttpStatusCode.BadRequest) : null;

        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync(LoadStore(), null, CancellationToken.None));
        Assert.Single(api.Asked, url => url.Contains("item-stats"));
    }

    [Fact]
    public async Task CancellingStopsBeforeTheNextStepAndTouchesNothing()
    {
        var (service, api, _) = Replay();
        using var cancel = new CancellationTokenSource();
        var progress = new Collect<FetchProgress> { OnReport = step => { if (step.Done == 3) cancel.Cancel(); } };
        var store = LoadStore();
        var before = store.MatchLift.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync(store, progress, cancel.Token));

        Assert.Equal(before, store.MatchLift.Count);
        Assert.Null(store.MatchCounts);
        // /v1/patches, /v1/assets/ranks, then both halves of steps 0 to 3.
        Assert.Equal(2 + 4 * 2, api.Asked.Count);
    }

    [Fact]
    public async Task ThePatchCheckOnlyCallsWithDataAndReportsANewerPatch()
    {
        var (service, api, _) = Replay();
        Assert.Null(await service.NewerPatchAsync([]));
        Assert.Empty(api.Asked);

        var meta = JsonNode.Parse("""{"fetched_at": 1, "latest_patch": {"start": 1787443200}}""")!.AsObject();
        var newer = await service.NewerPatchAsync(meta);
        Assert.Equal("09-16", newer!.Label);

        var (upToDate, _, _) = Replay();
        meta["latest_patch"]!["start"] = newer.Start;
        Assert.Null(await upToDate.NewerPatchAsync(meta));
    }
}
