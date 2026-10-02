using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.GameApi;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>The shared match data: its files, and the scheduled job that publishes them.</summary>
public sealed class MatchSnapshotTests : IDisposable
{
    private static readonly long _now = SyntheticItemStatsApi.Now.ToUnixTimeSeconds();
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static IEnumerable<string> ItemStats(IEnumerable<string> asked) => asked.Where(url => url.Contains("/item-stats?"));

    /// <summary>The seed's heroes and items, as the job starts from them, answered by the synthetic API with the game sync's snapshot.</summary>
    private static SyntheticItemStatsApi Api()
    {
        using var seed = new TempDirectory();
        DataService.SeedIfEmpty(seed.Path);
        var api = new SyntheticItemStatsApi(DataStore.Load(seed.Path));
        api.Also[$"{GameSync.Api}/heroes?only_active=true"] = () => Json("game_api/heroes.json");
        api.Also[$"{GameSync.Api}/items/by-type/upgrade"] = () => Json("game_api/shop_items.json");
        return api;
    }

    private Task<MatchSnapshot> RunAsync(SyntheticItemStatsApi api, string run, string? restore, long now) =>
        new MatchSnapshotJob(api, TextWriter.Null, () => DateTimeOffset.FromUnixTimeSeconds(now), (_, _) => Task.CompletedTask)
            .RunAsync(Path.Combine(_temp.Path, run, "work"), restore, Path.Combine(_temp.Path, run, "out"));

    private string Out(string run) => Path.Combine(_temp.Path, run, "out");

    [Fact]
    public void AManifestReadsBackAsWritten()
    {
        var snapshot = new MatchSnapshot(1_790_900_000,
        [
            new SnapshotPatch("2026-09-29-0123abcd.json.gz", "09-29-2026", 1_790_726_400, 1_790_899_000, false, 1_790_899_000, true, 312_345, new string('a', 64)),
            new SnapshotPatch("2026-09-16-89abcdef.json.gz", "09-16-2026 Update", 1_789_603_200, 1_790_726_399, true, 1_790_899_000, true, 301_000, new string('b', 64)),
        ]);

        var read = MatchSnapshot.Parse(snapshot.ToJsonBytes());

        Assert.Equal(snapshot.CheckedAt, read.CheckedAt);
        Assert.Equal(snapshot.Patches, read.Patches);
        Assert.True(read.Patches[1].Complete);
        Assert.False(read.IsStale(snapshot.CheckedAt + (long)MatchSnapshot.StaleAfter.TotalSeconds - 1));
        Assert.True(read.IsStale(snapshot.CheckedAt + (long)MatchSnapshot.StaleAfter.TotalSeconds));
    }

    [Fact]
    public void AManifestOfAnotherVersionOrWithAPathForAFileIsRefused()
    {
        var bytes = new MatchSnapshot(1, [new SnapshotPatch("a.json.gz", "09-29-2026", 1, 2, false, 2, true, 3, "")]).ToJsonBytes();
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Throws<FormatException>(() => MatchSnapshot.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("\"version\": 1", "\"version\": 2"))));
        Assert.Throws<FormatException>(() => MatchSnapshot.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("a.json.gz", "../a.json.gz"))));
    }

    [Fact]
    public async Task APackedSegmentUnpacksAsItWasAndADamagedOneIsRefused()
    {
        using var data = CopyData();
        var store = DataStore.Load(data.Path);
        await SyntheticItemStatsApi.DownloadAsync(store);
        var segment = store.MatchSegments[0];

        var (entry, gzipped) = MatchSnapshot.Pack(segment);

        Assert.StartsWith(segment.Patch.Date + "-", entry.File);
        Assert.Equal(segment.ToJsonBytes(), MatchSnapshot.Unpack(entry, gzipped).ToJsonBytes());
        Assert.True(gzipped.Length < segment.ToJsonBytes().Length / 3, "gzip should shrink the counts several times over");
        gzipped[^5] ^= 1;
        Assert.Throws<InvalidDataException>(() => MatchSnapshot.Unpack(entry, gzipped));
        Assert.Throws<InvalidDataException>(() => MatchSnapshot.Unpack(entry with { Start = entry.Start + 1 }, MatchSnapshot.Pack(segment).Gzipped));
    }

    [Fact]
    public async Task APlanTakesWhatTheSnapshotHasNewerAndNeverDropsANewerPatch()
    {
        using var data = CopyData();
        var store = DataStore.Load(data.Path);
        await SyntheticItemStatsApi.DownloadAsync(store);
        var (current, previous) = (store.MatchSegments[0], store.MatchSegments[1]);
        var snapshot = new MatchSnapshot(_now, [MatchSnapshot.Pack(current).Entry, MatchSnapshot.Pack(previous).Entry]);
        IReadOnlyList<(string, FetchReason)> Steps(params MatchSegment[] stored) =>
            SnapshotPlan.For(snapshot, stored, _now)!.Steps.Select(step => (step.Entry.Patch.Label, step.Reason)).ToList();
        var earlier = current with { Until = current.Until - 86400, FetchedAt = current.FetchedAt - 86400 };

        Assert.Equal([("09-29", FetchReason.New), ("09-16", FetchReason.New)], Steps());
        Assert.Equal([("09-29", FetchReason.Refresh)], Steps(earlier, previous));
        Assert.Empty(Steps(current, previous));
        // Complete counts never change, but rank groups they lack come along.
        Assert.Equal([("09-16", FetchReason.AddRanks)], Steps(current, previous with { Ranks = [], ByRank = [] }));
        // Fetched before the patch ended: its last matches.
        Assert.Equal([("09-16", FetchReason.Finish)],
            Steps(current, previous with { Until = previous.Until - 86400, Ended = false, FetchedAt = previous.Until - 86400 }));
        // Counts newer than the snapshot's stay.
        Assert.Empty(Steps(current with { FetchedAt = current.FetchedAt + 3600, Until = current.Until + 3600 }, previous));
        // A patch the snapshot doesn't have yet: it's behind, and would drop it.
        var newer = current with { Patch = new Patch("10-05-2026", current.Patch.Start + 6 * 86400) };
        Assert.Null(SnapshotPlan.For(snapshot, [newer, current, previous], _now));
    }

    [Fact]
    public async Task TheFirstRunPublishesTheKeptPatchesWithRankGroupsExactlyAsTheAppWouldDownloadThem()
    {
        var snapshot = await RunAsync(Api(), "first", restore: null, _now);

        Assert.Equal(_now, snapshot.CheckedAt);
        Assert.Equal(["09-29", "09-16"], snapshot.Patches.Select(patch => patch.Patch.Label));
        Assert.All(snapshot.Patches, patch => Assert.True(patch.Ranks));
        Assert.Equal(snapshot.ToJsonBytes(), File.ReadAllBytes(Path.Combine(Out("first"), MatchSnapshot.ManifestFile)));
        Assert.Equal(snapshot.Patches.Select(patch => patch.File).Append(MatchSnapshot.ManifestFile).Order(),
            Directory.GetFiles(Out("first")).Select(Path.GetFileName).Order());

        // The app's own download over the same synced heroes brings back the very same counts.
        using var direct = new TempDirectory();
        DataService.SeedIfEmpty(direct.Path);
        var store = DataStore.Load(direct.Path);
        var api = Api();
        await new GameApiService(api).SyncAsync(store);
        var service = api.Service();
        var plan = await service.PlanAsync(store, includeRanks: true);
        var downloaded = new List<MatchSegment>();
        await service.FetchAsync(store, plan, null, segment => downloaded.Add(segment), CancellationToken.None);
        foreach (var entry in snapshot.Patches)
        {
            var published = MatchSnapshot.Unpack(entry, File.ReadAllBytes(Path.Combine(Out("first"), entry.File)));
            Assert.Equal(downloaded.Last(segment => segment.Patch.Start == entry.Start).ToJsonBytes(), published.ToJsonBytes());
        }
    }

    [Fact]
    public async Task ALaterRunOnlyAsksForThePatchListUntilTheCurrentPatchIsDueAndThenFetchesJustThat()
    {
        var first = await RunAsync(Api(), "first", restore: null, _now);

        var quiet = Api();
        var unchanged = await RunAsync(quiet, "quiet", Out("first"), _now + 3600);

        Assert.Equal([MatchStatsService.Patches], quiet.Asked);
        Assert.Equal(_now + 3600, unchanged.CheckedAt);
        Assert.Equal(first.Patches, unchanged.Patches);

        var due = Api();
        var dueAt = _now + (long)MatchSnapshot.RefreshAfter.TotalSeconds;
        var refreshed = await RunAsync(due, "due", Out("quiet"), dueAt);

        // The patch before is over and settled: only the current one is fetched again, with its rank groups.
        var currentStart = first.Patches[0].Start;
        Assert.NotEmpty(ItemStats(due.Asked));
        Assert.All(ItemStats(due.Asked), url => Assert.True(long.Parse(SyntheticItemStatsApi.Query(url)["min_unix_timestamp"]) >= currentStart));
        Assert.Equal(dueAt, refreshed.Patches[0].FetchedAt);
        Assert.NotEqual(first.Patches[0].File, refreshed.Patches[0].File);
        Assert.Equal(first.Patches[1], refreshed.Patches[1]);
    }

    [Fact]
    public async Task ADamagedFileFromTheLastRunIsFetchedAgain()
    {
        var first = await RunAsync(Api(), "first", restore: null, _now);
        File.WriteAllBytes(Path.Combine(Out("first"), first.Patches[1].File), [1, 2, 3]);

        var api = Api();
        var again = await RunAsync(api, "again", Out("first"), _now + 3600);

        Assert.Contains(ItemStats(api.Asked), url => long.Parse(SyntheticItemStatsApi.Query(url)["min_unix_timestamp"]) == first.Patches[1].Start);
        Assert.Equal(2, again.Patches.Count);
    }
}
