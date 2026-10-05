using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>
/// Downloads from the synthetic API (<see cref="SyntheticItemStatsApi"/>), a day and a half into patch
/// 09-29. The files every match gives are regression snapshots, rewritten with DEADLOCK_UPDATE_GOLDENS=1.
/// </summary>
public class MatchStatsServiceTests
{
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

    private static readonly int _heroes = MatchStatsService.Heroes(LoadStore()).Count;

    private static IEnumerable<string> ItemStats(IEnumerable<string> asked) => asked.Where(url => url.Contains("/item-stats?"));

    private static IEnumerable<string> HeroStats(IEnumerable<string> asked) => asked.Where(url => url.Contains("/hero-stats?"));

    private static IEnumerable<string> Analytics(IEnumerable<string> asked) => asked.Where(url => url.StartsWith(MatchStatsService.Analytics));

    [Fact]
    public async Task APlanKeepsTheLastTwoPatchesAndFetchesEveryMatchBeforeTheRankGroups()
    {
        var store = LoadStore();
        var api = new SyntheticItemStatsApi(store);

        var plan = await api.Service().PlanAsync(store, includeRanks: true);

        Assert.Equal([MatchStatsService.Patches], api.Asked);
        Assert.Equal(["09-29", "09-16"], plan.Keep.Select(patch => patch.Label));
        Assert.Equal(
            [
                (FetchPart.EveryMatch, "09-29", FetchReason.New),
                (FetchPart.EveryMatch, "09-16", FetchReason.New),
                (FetchPart.Ranks, "09-29", FetchReason.New),
                (FetchPart.Ranks, "09-16", FetchReason.New),
            ],
            plan.Phases.Select(phase => (phase.Part, phase.Patch.Label, phase.Reason)));
        // The current patch runs until now; the one before ends the second before it.
        Assert.Equal(SyntheticItemStatsApi.Now.ToUnixTimeSeconds(), plan.Phases[0].Until);
        Assert.Equal(plan.Keep[0].Start - 1, plan.Phases[1].Until);
        Assert.Equal((false, true), (plan.Phases[0].Ended, plan.Phases[1].Ended));
        // A rank group needs no ranked calls: it holds only ranked matches.
        Assert.Equal(2 * (2 * (2 + _heroes) + 3 + 5 * (2 * (2 + _heroes) + 1)), plan.Calls);
    }

    [Fact]
    public async Task EveryMatchAndEveryHerosOwnPurchasesTakeOneCallPerHalfAndEachEnemyItsOwn()
    {
        var store = LoadStore();
        var api = new SyntheticItemStatsApi(store);
        var service = api.Service();
        var plan = await service.PlanAsync(store, includeRanks: false);
        var progress = new Collect<MatchFetchProgress>();
        var finished = new List<MatchSegment>();
        var bytesBefore = api.BytesReceived;

        await service.FetchAsync(store, plan, progress, finished.Add, CancellationToken.None);

        // No rank groups, so no rank names either.
        Assert.DoesNotContain(MatchStatsService.Ranks, api.Asked);
        Assert.Equal(plan.Calls, Analytics(api.Asked).Count());
        var asked = ItemStats(api.Asked).Select(SyntheticItemStatsApi.Query).ToList();
        Assert.Equal(2 * (2 * (2 + _heroes) + 1), asked.Count);
        Assert.All(asked, query => Assert.Equal("1", query["min_matches"]));
        Assert.Equal(4, asked.Count(query => query.Count == 3));
        Assert.Equal(6, asked.Count(query => query.GetValueOrDefault("bucket") == "hero"));
        Assert.Equal(2, asked.Count(query => query.GetValueOrDefault("match_mode") == "ranked"));
        Assert.DoesNotContain(asked, query => query.ContainsKey("hero_id") || query.ContainsKey("min_average_badge"));
        // Each hero's matches, and its ranked ones, once per patch over its whole window.
        var heroStats = HeroStats(api.Asked).Select(SyntheticItemStatsApi.Query).ToList();
        Assert.Equal(4, heroStats.Count);
        Assert.Equal(2, heroStats.Count(query => query.GetValueOrDefault("match_mode") == "ranked"));
        Assert.All(heroStats.Take(2), query => Assert.Equal(
            (plan.Phases[0].From, plan.Phases[0].Until), (long.Parse(query["min_unix_timestamp"]), long.Parse(query["max_unix_timestamp"]))));
        // Both halves of a window, with no second in both.
        var current = asked.Take(2).Select(query => (long.Parse(query["min_unix_timestamp"]), long.Parse(query["max_unix_timestamp"]))).ToList();
        Assert.Equal((plan.Phases[0].From, plan.Phases[0].Until), (current[0].Item1, current[1].Item2));
        Assert.Equal(current[0].Item2 + 1, current[1].Item1);

        Assert.Equal(["09-29", "09-16"], finished.Select(segment => segment.Patch.Label));
        Assert.All(finished, segment => Assert.False(segment.HasRanks));
        Assert.Equal(_heroes, finished[0].EveryMatch.As.Count);
        Assert.Equal(_heroes, finished[0].EveryMatch.Against.Count);
        Assert.Equal(_heroes, finished[0].EveryMatch.HeroMatches.Count);
        Assert.Equal(_heroes, finished[0].EveryMatch.Ranked.Count);
        Assert.Equal(SyntheticItemStatsApi.Now.ToUnixTimeSeconds(), finished[0].FetchedAt);

        Assert.Equal(Enumerable.Range(1, plan.Calls), progress.Seen.Select(step => step.Done));
        Assert.Equal([0, 1], progress.Seen.Select(step => step.Phase).Distinct());
        Assert.Equal("09-29 · every match · all matches", progress.Seen[0].Text);
        Assert.Equal("09-29 · every match · Enemies: Abrams", progress.Seen[4].Text);
        Assert.Equal(plan.Phases[0].Calls, progress.Seen.Last(step => step.Phase == 0).PhaseDone);
        Assert.Equal(api.BytesReceived - bytesBefore, progress.Seen[^1].Bytes);
    }

    [Fact]
    public async Task YourHeroCountsComeFromTheHeroBucketsAndEveryMatchStillHasTheHeroesTheStoreDoesntKnow()
    {
        var store = LoadStore();
        var api = new SyntheticItemStatsApi(store);
        var service = api.Service();
        var plan = await service.PlanAsync(store, includeRanks: false);
        var finished = new List<MatchSegment>();

        await service.FetchAsync(store, plan, null, finished.Add, CancellationToken.None);

        var everyMatch = finished[0].EveryMatch;
        var item = everyMatch.Baseline.First.Keys.First();
        var heroesTotal = everyMatch.As.Values.Sum(halves => halves.First.GetValueOrDefault(item).Matches);
        Assert.True(everyMatch.Baseline.First[item].Matches > heroesTotal);
    }

    [Fact]
    public async Task ADownloadWritesEachPatchAndTheSnapshotLifts()
    {
        using var data = CopyData();
        var store = DataStore.Load(data.Path);

        var result = await SyntheticItemStatsApi.DownloadAsync(store);

        if (Updating)
        {
            WriteJson("match_fetch/result.json", new JsonObject
            {
                ["lines"] = new JsonArray(result.Lines().Select(line => (JsonNode)line).ToArray()),
                ["lift_count"] = result.Lifts.Count,
            });
            CopyFile(data.File(DataStore.MatchLiftFile), "match_fetch", "match_item_lift.csv");
            CopyFile(data.File(DataStore.MatchMetaFile), "match_fetch", "match_item_lift.meta.json");
        }
        var expected = Json("match_fetch/result.json");
        Assert.Equal(expected["lines"]!.AsArray().Select(Text), result.Lines());
        Assert.Equal(expected["lift_count"]!.GetValue<int>(), result.Lifts.Count);
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.csv"), data.File(DataStore.MatchLiftFile));
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.meta.json"), data.File(DataStore.MatchMetaFile));

        var saved = DataStore.Load(data.Path);
        Assert.Equal(["09-29", "09-16"], saved.MatchSegments.Select(segment => segment.Patch.Label));
        Assert.All(saved.MatchSegments, segment => Assert.True(segment.HasRanks));
        Assert.Equal(store.MatchSegments.Select(segment => segment.ToJsonBytes()), saved.MatchSegments.Select(segment => segment.ToJsonBytes()));
        Assert.True(File.Exists(Path.Combine(data.Path, DataStore.MatchCountsDir, "2026-09-29.json")));
    }

    [Fact]
    public async Task ARefreshFetchesOnlyThePatchesStillCollectingMatches()
    {
        var store = LoadStore();
        var oneDayLater = SyntheticItemStatsApi.Now.AddDays(1).ToUnixTimeSeconds();
        var segment = new MatchSegment(new Patch("09-16-2026 Update", 1789603200), 1789603200, 1790726399, true, oneDayLater, SliceCounts.Empty, [], []);
        var current = segment with { Patch = new Patch("09-29-2026", 1790726400), From = 1790726400, Until = 1790800000, Ended = false };
        var patches = MatchStatsMath.ParsePatches(SyntheticItemStatsApi.PatchTitles);

        var plan = MatchFetchPlan.For([current, segment], patches, oneDayLater, includeRanks: true, heroCount: 38);

        // 09-16 is over and settled, but has no rank groups: only those. 09-29 is still going.
        Assert.True(segment.Complete);
        Assert.Equal(
            [
                (FetchPart.EveryMatch, "09-29", FetchReason.Refresh),
                (FetchPart.Ranks, "09-29", FetchReason.Refresh),
                (FetchPart.Ranks, "09-16", FetchReason.AddRanks),
            ],
            plan.Phases.Select(phase => (phase.Part, phase.Patch.Label, phase.Reason)));
        Assert.Equal((segment.From, segment.Until), (plan.Phases[2].From, plan.Phases[2].Until));

        // Fetched within a day of the next patch, it might still be missing matches.
        var unsettled = segment with { FetchedAt = segment.Until + 3600 };
        Assert.False(unsettled.Complete);
        Assert.Equal(FetchReason.Finish, MatchFetchPlan.For([unsettled], patches, oneDayLater, false, 38).Phases[1].Reason);
    }

    [Fact]
    public void TwoYoungPatchesKeepTheOneBeforeThemToo()
    {
        var patches = MatchStatsMath.ParsePatches(["10-01-2026", "09-29-2026", "09-26-2026 Update", "09-16-2026 Update", "08-22-2026 Update"]);

        var plan = MatchFetchPlan.For([], patches, SyntheticItemStatsApi.Now.ToUnixTimeSeconds(), includeRanks: false, heroCount: 38);

        // 10-01 starts at midnight tonight. 09-29 and the 09-26 hotfix have under two weeks between them.
        Assert.Equal(["09-29", "09-26", "09-16"], plan.Keep.Select(patch => patch.Label));
        Assert.Equal([false, true, true], plan.Phases.Select(phase => phase.Ended));
        Assert.Equal(patches[2].Start - 1, plan.Phases[2].Until);
    }

    [Fact]
    public async Task ApplyingKeepsOnlyThePlansPatchesAndDropsTheOldCountsFile()
    {
        using var data = CopyData();
        var store = DataStore.Load(data.Path);
        var old = new MatchSegment(new Patch("08-12-2026 Update", 1786924800), 1786924800, 1787443199, true, 1790000000, SliceCounts.Empty, [], []);
        store.PutMatchSegment(old);
        store.SaveMatchSegment(old);
        File.WriteAllText(data.File(DataStore.LegacyMatchCountsFile), "{}");

        await SyntheticItemStatsApi.DownloadAsync(store, includeRanks: false);

        Assert.Equal(["09-29", "09-16"], store.MatchSegments.Select(segment => segment.Patch.Label));
        Assert.False(File.Exists(Path.Combine(data.Path, DataStore.MatchCountsDir, old.FileName)));
        Assert.False(File.Exists(data.File(DataStore.LegacyMatchCountsFile)));
    }

    [Fact]
    public async Task ARankRangeIsWorkedOutFromTheSavedCountsWithoutTheNetwork()
    {
        using var data = CopyData();
        var every = await SyntheticItemStatsApi.DownloadAsync(DataStore.Load(data.Path));
        var store = DataStore.Load(data.Path);
        var service = new MatchStatsService(new FakeDeadlockApi());

        var mystic = service.Reanalyse(store, new RankRange(5, 11));

        Assert.Equal("Mystic+", mystic.RankLabel);
        Assert.Equal("Leaning toward Mystic+.", mystic.Lines()[0]);
        Assert.Equal(every.Lifts.Values.Select(lift => lift.Lift), mystic.Lifts.Values.Select(lift => lift.Lift));
        Assert.NotEqual(every.Lifts.Values.Select(lift => lift.LiftShrunk), mystic.Lifts.Values.Select(lift => lift.LiftShrunk));
        Assert.Contains(mystic.Lifts.Values, lift => lift.RankShift != 0);
        var saved = DataStore.Load(data.Path);
        Assert.Equal(new RankRange(5, 11), MatchStatsMath.RankOf(saved.MatchMeta));
        Assert.Equal("Mystic+", MatchStatsMath.RankLabel(saved.MatchMeta));
        Assert.Equal(mystic.Lifts.Count, saved.MatchLift.Count);

        service.Reanalyse(saved, null);
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.csv"), data.File(DataStore.MatchLiftFile));
        AssertEx.BytesEqual(PathOf("match_fetch", "match_item_lift.meta.json"), data.File(DataStore.MatchMetaFile));
    }

    [Fact]
    public void WorkingTheLiftsOutWithoutCountsThrows()
    {
        var store = LoadStore();

        Assert.Empty(store.MatchSegments);
        Assert.Throws<InvalidOperationException>(() => new MatchStatsService(new FakeDeadlockApi()).Reanalyse(store, new RankRange(5, 11)));
    }

    [Fact]
    public async Task ARateLimitPausesTheDownloadAndIsRetried()
    {
        var store = LoadStore();
        var api = new SyntheticItemStatsApi(store);
        var waits = new List<TimeSpan>();
        var service = api.Service(waits);
        var plan = await service.PlanAsync(store, includeRanks: false);
        var progress = new Collect<MatchFetchProgress>();
        var refused = 0;
        api.FailWith = url => url.Contains("item-stats") && refused++ < 2
            ? new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests)
            : null;

        await service.FetchAsync(store, plan, progress, _ => { }, CancellationToken.None);

        Assert.Equal(2, waits.Count(wait => wait == MatchStatsService.RateLimitWait));
        Assert.Equal(2, progress.Seen.Count(step => step.Wait?.Length == MatchStatsService.RateLimitWait));
        Assert.Null(progress.Seen[^1].Wait);
        Assert.Equal(plan.Calls, progress.Seen[^1].Done);
    }

    [Fact]
    public async Task AClientErrorIsNotRetried()
    {
        var store = LoadStore();
        var api = new SyntheticItemStatsApi(store);
        var service = api.Service();
        var plan = await service.PlanAsync(store, includeRanks: false);
        api.FailWith = url => url.Contains("item-stats") ? new HttpRequestException("nope", null, HttpStatusCode.BadRequest) : null;

        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync(store, plan, null, _ => { }, CancellationToken.None));
        Assert.Single(ItemStats(api.Asked));
    }

    [Fact]
    public async Task CancellingKeepsTheFinishedPhasesAndStopsBeforeTheNextCall()
    {
        var store = LoadStore();
        var api = new SyntheticItemStatsApi(store);
        var service = api.Service();
        var plan = await service.PlanAsync(store, includeRanks: true);
        using var cancel = new CancellationTokenSource();
        var firstPhase = plan.Phases[0].Calls;
        var progress = new Collect<MatchFetchProgress> { OnReport = step => { if (step.Done == firstPhase + 3) cancel.Cancel(); } };
        var finished = new List<MatchSegment>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync(store, plan, progress, finished.Add, cancel.Token));

        Assert.Equal(["09-29"], finished.Select(segment => segment.Patch.Label));
        // The cancel came as the next phase's third call finished, with its fourth already asked.
        Assert.InRange(Analytics(api.Asked).Count(), firstPhase + 3, firstPhase + 3 + MatchStatsService.MaxInFlight);
    }

    [Fact]
    public async Task EachHerosMatchesAndRankedCountsAddUpByModeAndRank()
    {
        var store = LoadStore();
        var service = new SyntheticItemStatsApi(store).Service();
        var plan = await service.PlanAsync(store, includeRanks: true);
        var finished = new List<MatchSegment>();

        await service.FetchAsync(store, plan, null, finished.Add, CancellationToken.None);

        var segment = finished.Last(segment => segment.Patch.Label == "09-29" && segment.HasRanks);
        var heroId = segment.EveryMatch.HeroMatches.Keys.First();
        var all = segment.HeroItems(MatchMode.All, null)![heroId];
        var ranked = segment.HeroItems(MatchMode.Ranked, null)![heroId];
        var unranked = segment.HeroItems(MatchMode.Unranked, null)![heroId];
        // Every item's buyers are among the hero's matches, and the ranked ones among them.
        Assert.All(all.Items.Values, item => Assert.InRange(item.Matches, 1, all.Matches.Matches));
        Assert.InRange(ranked.Matches.Matches, 1, all.Matches.Matches - 1);
        Assert.Equal(all.Matches.Matches - ranked.Matches.Matches, unranked.Matches.Matches);
        var item = ranked.Items.Keys.First();
        Assert.Equal(all.Items[item].Matches - ranked.Items[item].Matches, unranked.Items[item].Matches);
        // The rank groups hold every ranked match and nothing else, so a range is the same in any mode but unranked.
        var everyRank = new RankRange(1, 11);
        Assert.Equal(ranked.Matches, segment.HeroItems(MatchMode.Ranked, everyRank)![heroId].Matches);
        Assert.Equal(ranked.Items[item], segment.HeroItems(MatchMode.All, everyRank)![heroId].Items[item]);
        Assert.Empty(segment.HeroItems(MatchMode.Unranked, everyRank)!);
        Assert.Null(finished.First(segment => !segment.HasRanks).HeroItems(MatchMode.All, everyRank));

        // And come back the same from the file.
        var parsed = MatchSegment.Parse(segment.ToJsonBytes());
        Assert.Equal(segment.ToJsonBytes(), parsed.ToJsonBytes());
        Assert.Equal(ranked.Matches, parsed.HeroItems(MatchMode.Ranked, null)![heroId].Matches);
        Assert.Equal(ranked.Items.OrderBy(pair => pair.Key), parsed.HeroItems(MatchMode.Ranked, null)![heroId].Items.OrderBy(pair => pair.Key));
        Assert.Equal(segment.HeroItems(MatchMode.All, new RankRange(5, 6))![heroId].Matches,
            parsed.HeroItems(MatchMode.All, new RankRange(5, 6))![heroId].Matches);
    }

    [Fact]
    public async Task ThePacerStartsInOrderNoMoreThanTwoAtOnceAndAFailureStopsTheRest()
    {
        var waits = new List<TimeSpan>();
        var clock = TimeSpan.Zero;
        var pacer = new RequestPacer(TimeSpan.FromSeconds(1), 2, () => clock, (wait, _) =>
        {
            waits.Add(wait);
            clock += wait;
            return Task.CompletedTask;
        });
        var started = new List<int>();
        var running = 0;
        var most = 0;
        var gates = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource<int>()).ToList();
        var requests = Enumerable.Range(0, 5).Select(i => (Func<CancellationToken, Task<int>>)(async token =>
        {
            started.Add(i);
            most = Math.Max(most, ++running);
            var answer = await gates[i].Task.WaitAsync(token);
            running--;
            return answer;
        })).ToList();

        var run = pacer.RunAsync(requests, CancellationToken.None);
        Assert.Equal([0, 1], started);
        gates[1].SetResult(10);
        gates[0].SetResult(0);
        gates[2].SetResult(20);
        gates[3].SetResult(30);
        gates[4].SetResult(40);

        var answers = await run;
        Assert.Equal([0, 10, 20, 30, 40], answers);
        Assert.Equal([0, 1, 2, 3, 4], started);
        Assert.Equal(2, most);
        Assert.All(waits, wait => Assert.Equal(TimeSpan.FromSeconds(1), wait));

        // A pause holds the next start back for its length, not the gap.
        pacer.Pause(TimeSpan.FromSeconds(30));
        waits.Clear();
        await pacer.RunAsync([_ => Task.FromResult(1)], CancellationToken.None);
        Assert.Equal([TimeSpan.FromSeconds(30)], waits);

        var failing = new List<Func<CancellationToken, Task<int>>>
        {
            _ => Task.FromException<int>(new HttpRequestException("nope")),
            token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => 0, TaskScheduler.Default),
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => pacer.RunAsync(failing, CancellationToken.None));
    }

    [Fact]
    public async Task ThePatchCheckOnlyCallsWithDataAndReportsANewerPatch()
    {
        var store = LoadStore();
        var api = new SyntheticItemStatsApi(store);
        var service = api.Service();
        Assert.Null(await service.NewerPatchAsync([]));
        Assert.Empty(api.Asked);

        var meta = JsonNode.Parse("""{"fetched_at": 1, "latest_patch": {"start": 1789603200}}""")!.AsObject();
        var newer = await service.NewerPatchAsync(meta);
        Assert.Equal("09-29", newer!.Label);

        meta["latest_patch"]!["start"] = newer.Start;
        Assert.Null(await service.NewerPatchAsync(meta));
    }
}
