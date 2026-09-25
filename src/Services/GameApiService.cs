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
    /// only touched once both answers are in, so a failed fetch changes nothing.
    /// </summary>
    Task<SyncReport> SyncAsync(DataStore store, CancellationToken cancellationToken = default);
}

public sealed class GameApiService(IDeadlockApi api) : IGameApiService
{
    public async Task<JsonArray> FetchHeroesAsync(CancellationToken cancellationToken = default) =>
        await api.GetJsonAsync($"{GameSync.Api}/heroes?only_active=true", cancellationToken) as JsonArray ?? [];

    public async Task<JsonArray> FetchShopItemsAsync(CancellationToken cancellationToken = default)
    {
        var records = await api.GetJsonAsync($"{GameSync.Api}/items/by-type/upgrade", cancellationToken) as JsonArray ?? [];
        return new JsonArray(records
            .Where(record => PyJson.Truthy(PyJson.Get(record, "shopable")) && GameSync.ShopTiers.Contains(PyJson.Int(record, "item_tier")))
            .Select(record => record!.DeepClone())
            .ToArray());
    }

    public async Task<JsonArray> FetchUpgradesAsync(CancellationToken cancellationToken = default)
    {
        var records = await api.GetJsonAsync($"{GameSync.Api}/items", cancellationToken) as JsonArray ?? [];
        return new JsonArray(records
            .Where(record => PyJson.Text(record, "type") == "upgrade")
            .Select(record => record!.DeepClone())
            .ToArray());
    }

    public async Task<SyncReport> SyncAsync(DataStore store, CancellationToken cancellationToken = default)
    {
        var heroes = await FetchHeroesAsync(cancellationToken);
        var items = await FetchShopItemsAsync(cancellationToken);
        var report = GameSync.Apply(store, heroes, items);
        GameSync.SaveSynced(store, report);
        return report;
    }
}
