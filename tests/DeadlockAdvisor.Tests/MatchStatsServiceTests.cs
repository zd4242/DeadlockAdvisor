using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// The fetch replayed against the conversation the Python app had with a synthetic API
/// (export_golden.py's match_stats_fetch): same requests in the same order, same files out.
/// </summary>
public class MatchStatsServiceTests
{
    /// <summary>Answers only the recorded URLs, in order, and remembers what was asked.</summary>
    private sealed class ReplayApi : IDeadlockApi
    {
        private readonly Dictionary<string, Queue<JsonNode>> _answers = [];

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
            if (!_answers.TryGetValue(url, out var queue) || queue.Count == 0)
                throw new InvalidOperationException($"Python never asked for {url}");
            return Task.FromResult<JsonNode?>(queue.Dequeue());
        }

        public Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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

    [Fact]
    public async Task FetchAsksWhatPythonAskedAndWritesWhatItWrote()
    {
        var (service, api, _) = Replay();
        var progress = new Collect<FetchProgress>();
        var store = LoadStore();

        var result = await service.FetchAsync(store, progress, CancellationToken.None);

        var recording = Json("match_fetch/requests.json");
        Assert.Equal(Items(recording["requests"]).Select(request => Text(request["url"])), api.Asked);
        var expected = Json("match_fetch/result.json");
        Assert.Equal(expected["lines"]!.AsArray().Select(Text), result.Lines());
        Assert.Equal(expected["lift_count"]!.GetValue<int>(), result.Lifts.Count);
        Assert.Equal(
            Items(expected["progress"]).Select(step => (step[0]!.GetValue<int>(), step[1]!.GetValue<int>(), Text(step[2]))),
            progress.Seen.Select(step => (step.Done, step.Total, step.Text)));

        using var data = CopyData();
        var target = DataStore.Load(data.Path);
        service.Apply(target, result);
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.csv"), data.File(DataStore.MatchLiftFile));
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.meta.json"), data.File(DataStore.MatchMetaFile));
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
        Assert.Equal(1 + 4 * 2, api.Asked.Count);
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
