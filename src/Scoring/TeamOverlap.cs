using System.Collections.Concurrent;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Scoring;

public readonly record struct HeldItem(string HeroId, string ItemId);

/// <summary>How much of one effect of an item the team already has.</summary>
/// <param name="Coverage">0 to 1: the strongest held source's value over the item's own, at most 1.</param>
/// <param name="By">The strongest source; null when nobody holds one.</param>
public sealed record OverlapCover(string Effect, string Scope, double Coverage, HeldItem? By);

/// <summary>Items of one build chain that two teammates hold, so one of them adds nothing to the other's effect.</summary>
public sealed record SharedEffect(string Effect, string Scope, IReadOnlyList<HeldItem> Holders)
{
    /// <summary>Stable for the same heroes holding the same items, to tell a new overlap from one already told.</summary>
    public string Key => $"{Scope}/{Effect}/{string.Join(",", Holders.OrderBy(held => held.HeroId, StringComparer.Ordinal).Select(held => $"{held.HeroId}:{held.ItemId}"))}";
}

/// <summary>
/// What you and your allies hold whose effect lands on other heroes (<see cref="ItemEffect"/>), as the scoreboard showed it. An effect
/// that is already there doesn't add up: a second copy of the same item, or an item on the same build chain
/// (<see cref="DataStore.BuildChain"/>: Mystic Vulnerability and Escalating Exposure), only counts for how much stronger it is. Items
/// that merely share an effect but not a chain stack in full. Enemies' items never count, and a hero whose cells weren't read
/// holds nothing as far as this goes.
/// </summary>
public sealed class TeamOverlap
{
    public static readonly TeamOverlap None = new(null, []);

    private readonly DataStore? _store;
    private readonly List<HeldItem> _held;
    private readonly ConcurrentDictionary<string, IReadOnlyList<OverlapCover>> _covers = [];

    private TeamOverlap(DataStore? store, List<HeldItem> held)
    {
        _store = store;
        _held = held;
    }

    public bool IsEmpty => _held.Count == 0;

    /// <summary>The items you and your allies hold that have a shared effect. Empty without a read of anyone's items.</summary>
    public static TeamOverlap For(DataStore store, MatchState match)
    {
        if (store.ItemEffects.Count == 0 || match.Builds.IsEmpty)
            return None;
        var held = new List<HeldItem>();
        foreach (var heroId in match.Allies.Concat(match.SelfHero is { } self ? [self] : []))
        {
            foreach (var itemId in match.BuildOf(heroId)?.ItemIds.Distinct() ?? [])
                held.Add(new HeldItem(heroId, itemId));
        }
        return Of(store, held);
    }

    /// <summary>The team holding exactly these items; the ones with no shared effect say nothing and are left out.</summary>
    public static TeamOverlap Of(DataStore store, IEnumerable<HeldItem> held)
    {
        var shared = held.Where(item => store.ItemEffects.ContainsKey(item.ItemId)).ToList();
        return shared.Count == 0 ? None : new TeamOverlap(store, shared);
    }

    /// <summary>The share of the item's shared effects the team has, averaged over them: 0 when none is held, 1 when all are.</summary>
    public double Coverage(string itemId)
    {
        var covers = Covers(itemId);
        return covers.Count == 0 ? 0.0 : covers.Average(cover => cover.Coverage);
    }

    /// <summary>Each of the item's shared effects with how much of it a held item already gives; empty for an item with none.</summary>
    public IReadOnlyList<OverlapCover> Covers(string itemId)
    {
        if (_store is null || !_store.ItemEffects.TryGetValue(itemId, out var effects))
            return [];
        return _covers.GetOrAdd(itemId, _ =>
        {
            var chain = _store.BuildChain(itemId);
            var sources = _held.Where(held => chain.Contains(held.ItemId)).ToList();
            return effects.Select(effect => Cover(effect, sources)).ToList();
        });
    }

    private OverlapCover Cover(ItemEffect effect, IReadOnlyList<HeldItem> sources)
    {
        var best = 0.0;
        HeldItem? by = null;
        foreach (var held in sources)
        {
            var value = _store!.ItemEffects[held.ItemId]
                .Where(other => other.Effect == effect.Effect && other.Scope == effect.Scope)
                .Select(other => other.Value)
                .DefaultIfEmpty(0.0)
                .Max();
            var share = Math.Min(1.0, value / effect.Value);
            if (share <= best)
                continue;
            best = share;
            by = held;
        }
        return new OverlapCover(effect.Effect, effect.Scope, best, by);
    }

    /// <summary>
    /// The effects two teammates both give through related items. One entry per effect and chain, with everyone
    /// holding a part of it, however many hold it.
    /// </summary>
    public IReadOnlyList<SharedEffect> Shared()
    {
        if (_store is null)
            return [];
        var shared = new List<SharedEffect>();
        var byEffect = _held
            .SelectMany(held => _store.ItemEffects[held.ItemId].Select(effect => (effect.Effect, effect.Scope, Held: held)))
            .GroupBy(entry => (entry.Effect, entry.Scope));
        foreach (var group in byEffect)
        {
            var clusters = new List<List<HeldItem>>();
            foreach (var (_, _, held) in group)
            {
                var chain = _store.BuildChain(held.ItemId);
                var related = clusters.Where(cluster => cluster.Any(other => chain.Contains(other.ItemId))).ToList();
                var merged = related.SelectMany(cluster => cluster).Append(held).ToList();
                clusters.RemoveAll(related.Contains);
                clusters.Add(merged);
            }
            shared.AddRange(clusters
                .Where(cluster => cluster.Select(held => held.HeroId).Distinct().Count() > 1)
                .Select(cluster => new SharedEffect(group.Key.Effect, group.Key.Scope, cluster)));
        }
        return shared;
    }
}
