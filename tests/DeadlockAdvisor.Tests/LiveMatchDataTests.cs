using System.Diagnostics;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.GameApi;
using Xunit.Abstractions;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>A test that calls the real deadlock-api.com, so it only runs with DEADLOCK_LIVE_API=1.</summary>
public sealed class LiveApiFactAttribute : FactAttribute
{
    public LiveApiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DEADLOCK_LIVE_API") != "1")
            Skip = "Calls deadlock-api.com: set DEADLOCK_LIVE_API=1 to run.";
    }
}

/// <summary>
/// What a download costs against the real API, and what the analysis makes of real matches: run with
/// DEADLOCK_LIVE_API=1 and a detailed console logger to read the numbers. The counts are kept in
/// %TEMP%\DeadlockAdvisorLive, so later runs analyse them again without downloading; delete it for fresh ones.
/// </summary>
public class LiveMatchDataTests(ITestOutputHelper output)
{
    private static readonly string _keep = Path.Combine(Path.GetTempPath(), "DeadlockAdvisorLive");

    [LiveApiFact]
    public async Task YourHeroFromTheHeroBucketsMatchesAskingPerHero()
    {
        using var api = new DeadlockApi();
        var hero = MatchStatsService.Heroes(LoadStore())[0];
        var until = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var from = until - 6 * 3600;

        var bucketed = await api.GetJsonAsync(MatchStatsService.ItemStatsUrl(MatchStatsMath.AsParams(), from, until)) as JsonArray;
        var single = await api.GetJsonAsync(MatchStatsService.ItemStatsUrl(new() { ["hero_ids"] = hero.GameId.ToString() }, from, until)) as JsonArray;

        var fromBuckets = MatchStatsMath.BucketTotals(bucketed!.Select(row =>
            (JsonRecord.Int(row, "bucket"), JsonRecord.Int(row, "item_id"), JsonRecord.Int(row, "wins"), JsonRecord.Int(row, "matches"))))[hero.GameId];
        var asked = MatchStatsMath.Totals(single!.Select(row => (JsonRecord.Int(row, "item_id"), JsonRecord.Int(row, "wins"), JsonRecord.Int(row, "matches"))));
        Assert.NotEmpty(asked);
        Assert.Equal(asked.OrderBy(pair => pair.Key), fromBuckets.OrderBy(pair => pair.Key));
    }

    /// <summary>
    /// One hero's items over the current patch, as a per-hero items table would show them: for checking
    /// the API's volume against a site like tracklock.gg. DEADLOCK_LIVE_HERO picks the hero (Dynamo by default).
    /// </summary>
    [LiveApiFact]
    public async Task AHeroItemTableFromTheApi()
    {
        var store = LoadStore();
        var name = Environment.GetEnvironmentVariable("DEADLOCK_LIVE_HERO") is { Length: > 0 } wanted ? wanted : "Dynamo";
        var hero = MatchStatsService.Heroes(store).Single(hero => hero.HeroName.Equals(name, StringComparison.OrdinalIgnoreCase));
        var items = store.Items.Values.Where(item => item.GameId != 0).ToDictionary(item => item.GameId);
        using var api = new DeadlockApi();
        var patch = (await new MatchStatsService(api).PatchesAsync())[0];
        var until = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        output.WriteLine($"{hero.HeroName} ({hero.GameId}), patch {patch.Title}, {MatchStatsMath.PatchSpan(patch.Start, until, ended: false)}");

        foreach (var mode in new[] { "ranked,unranked", "ranked" })
        {
            var filters = new OrderedDictionary<string, string> { ["match_mode"] = mode };
            var heroRows = await api.GetJsonAsync($"{MatchStatsService.Analytics}/hero-stats?match_mode={Uri.EscapeDataString(mode)}"
                                                  + $"&min_unix_timestamp={patch.Start}&max_unix_timestamp={until}") as JsonArray ?? [];
            var heroRow = heroRows.FirstOrDefault(row => JsonRecord.Int(row, "hero_id") == hero.GameId);
            var heroMatches = JsonRecord.Int(heroRow, "matches");
            var itemRows = await api.GetJsonAsync(MatchStatsService.ItemStatsUrl(
                new(filters) { ["hero_ids"] = hero.GameId.ToString() }, patch.Start, until)) as JsonArray ?? [];
            var totals = MatchStatsMath.Totals(itemRows.Select(row => (JsonRecord.Int(row, "item_id"), JsonRecord.Int(row, "wins"), JsonRecord.Int(row, "matches"))));

            output.WriteLine("");
            output.WriteLine($"-- match_mode={mode} --");
            output.WriteLine($"hero-stats row: {heroRow?.ToJsonString()}");
            output.WriteLine($"{heroMatches:N0} {hero.HeroName} matches; {totals.Count} items bought, "
                             + $"{totals.Keys.Count(id => !items.ContainsKey(id))} of them not in the store");
            output.WriteLine($"{"Item",-26} {"Cost",5} {"Win %",7} {"Usage %",8} {"Wins",8} {"Losses",8}");
            foreach (var (id, (wins, matches)) in totals.OrderByDescending(pair => pair.Value.Matches).Take(40))
            {
                var item = items.GetValueOrDefault(id);
                output.WriteLine($"{item?.ItemName ?? $"#{id}",-26} {item?.Cost,5} {100.0 * wins / matches,7:0.00} "
                                 + $"{(heroMatches > 0 ? 100.0 * matches / heroMatches : double.NaN),8:0.00} {wins,8:N0} {matches - wins,8:N0}");
            }
        }
    }

    [LiveApiFact]
    public async Task ARealDownloadAndWhatTheAnalysisMakesOfIt()
    {
        var store = LoadStore();
        store.DataDir = _keep;
        using var api = new DeadlockApi();
        var service = new MatchStatsService(api);
        var plan = await service.PlanAsync(store, includeRanks: false);
        // Every match for the patches kept, and the current patch's rank groups.
        var current = plan.Phases[0];
        plan = plan with { Phases = [.. plan.Phases, current with { Part = FetchPart.Ranks, Calls = MatchFetchPlan.RankCalls(MatchStatsService.Heroes(store).Count) }] };

        store.LoadMatchSegments();
        var stored = store.MatchSegments;
        if (stored.Count == 0 || stored[0].Patch.Start != current.Patch.Start)
        {
            var clock = Stopwatch.StartNew();
            var bytes = api.BytesReceived;
            await service.FetchAsync(store, plan, null, segment =>
            {
                store.PutMatchSegment(segment);
                store.SaveMatchSegment(segment);
            }, CancellationToken.None);
            bytes = api.BytesReceived - bytes;
            output.WriteLine($"{plan.Calls} calls in {clock.Elapsed.TotalSeconds:0} s: {clock.Elapsed.TotalSeconds / plan.Calls:0.000} s and "
                             + $"{bytes / (double)plan.Calls / 1024:0.0} KiB a call, {bytes / 1024.0 / 1024:0.0} MiB in all");
        }
        foreach (var file in Directory.GetFiles(Path.Combine(_keep, DataStore.MatchCountsDir)))
            output.WriteLine($"{Path.GetFileName(file)}: {new FileInfo(file).Length / 1024.0 / 1024:0.0} MiB on disk");

        foreach (var range in new RankRange?[] { null, new(9, 11), new(1, 4) })
        {
            var result = MatchStatsMath.Analyse(store.MatchSegments, range, store.Items.Values);
            output.WriteLine("");
            output.WriteLine($"-- {result.RankLabel} --");
            foreach (var line in result.Lines())
                output.WriteLine(line);
        }
    }
}
