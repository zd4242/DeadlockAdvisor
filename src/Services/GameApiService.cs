using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.GameApi;

namespace DeadlockAdvisor.Services;

public interface IGameApiService
{
    /// <summary>Every active hero, as the API describes them (names, ids, image URLs).</summary>
    Task<JsonArray> FetchHeroesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every item you can buy in a normal match.</summary>
    Task<JsonArray> FetchShopItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every upgrade the API knows, shop or not: what the item art comes from.</summary>
    Task<JsonArray> FetchUpgradesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch, fold into <paramref name="store"/> and write only the files that changed. The store is
    /// only touched once the heroes and the shop are in, so a failed fetch changes nothing.
    /// </summary>
    /// <param name="measureHeroes">
    /// Also fetch how much damage each hero takes, for the durability trait (<see cref="HeroDurability"/>). One
    /// call, and the sync goes ahead without it if it fails.
    /// </param>
    Task<SyncReport> SyncAsync(DataStore store, CancellationToken cancellationToken = default, bool measureHeroes = true);
}

public sealed class GameApiService : IGameApiService
{
    private readonly IDeadlockApi _api;
    private readonly Func<DateTimeOffset> _utcNow;

    public GameApiService(IDeadlockApi api)
        : this(api, () => DateTimeOffset.UtcNow)
    {
    }

    internal GameApiService(IDeadlockApi api, Func<DateTimeOffset> utcNow)
    {
        _api = api;
        _utcNow = utcNow;
    }

    public async Task<JsonArray> FetchHeroesAsync(CancellationToken cancellationToken = default) =>
        await _api.GetJsonAsync($"{GameSync.Api}/heroes?only_active=true", cancellationToken) as JsonArray ?? [];

    public async Task<JsonArray> FetchShopItemsAsync(CancellationToken cancellationToken = default)
    {
        var records = await _api.GetJsonAsync($"{GameSync.Api}/items/by-type/upgrade", cancellationToken) as JsonArray ?? [];
        return new JsonArray(records
            .Where(record => JsonRecord.Truthy(JsonRecord.Get(record, "shopable")) && GameSync.ShopTiers.Contains(JsonRecord.Int(record, "item_tier")))
            .Select(record => record!.DeepClone())
            .ToArray());
    }

    public async Task<JsonArray> FetchUpgradesAsync(CancellationToken cancellationToken = default)
    {
        var records = await _api.GetJsonAsync($"{GameSync.Api}/items", cancellationToken) as JsonArray ?? [];
        return new JsonArray(records
            .Where(record => JsonRecord.Text(record, "type") == "upgrade")
            .Select(record => record!.DeepClone())
            .ToArray());
    }

    /// <summary>How each hero's matches over the last <see cref="HeroDurability.Window"/> went, every hero in one answer.</summary>
    private async Task<JsonArray> FetchHeroStatsAsync(CancellationToken cancellationToken) =>
        await _api.GetJsonAsync(HeroDurability.Url(_utcNow()), cancellationToken) as JsonArray ?? [];

    public async Task<SyncReport> SyncAsync(DataStore store, CancellationToken cancellationToken = default, bool measureHeroes = true)
    {
        var heroes = await FetchHeroesAsync(cancellationToken);
        var items = await FetchShopItemsAsync(cancellationToken);
        JsonArray? heroStats = null;
        string? unmeasured = null;
        if (measureHeroes)
        {
            try
            {
                heroStats = await FetchHeroStatsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
            {
                unmeasured = $"Durability wasn't measured, because deadlock-api.com wouldn't give the hero stats ({ex.Message}). Sync again to try once more.";
            }
        }
        var report = GameSync.Apply(store, heroes, items, heroStats);
        report.DurabilityNote = unmeasured;
        GameSync.SaveSynced(store, report);
        return report;
    }
}
