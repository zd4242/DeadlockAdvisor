using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// Which of an item's rules stand for an effect that lands on other heroes (<see cref="ItemEffect"/>), so a second source on the
/// team can discount them (<see cref="TeamOverlap"/>). A debuff's worth is in the item's typed rules, which the stat rules don't
/// reach (<c>GameSync.Unscored</c>); an aura's is in the stat rules on the stat it gives, which the rest of the item's stats
/// (its own weapon damage, say) are not part of.
/// </summary>
/// <param name="TypedLines">The item puts a debuff on enemies, so its typed coefficients are that debuff's.</param>
/// <param name="AuraStats">The stats the item's aura gives.</param>
public sealed record OverlapShape(bool TypedLines, IReadOnlySet<string> AuraStats)
{
    /// <summary>
    /// The part of one line's effective coefficient that is the shared effect: the typed part for a debuff, the stat parts
    /// on an aura's stats, times the line's trait weight like the whole.
    /// </summary>
    public double Coefficient(DataStore store, string itemId, string categoryId, Relation relation)
    {
        var shared = TypedLines ? store.Coefficient(itemId, categoryId, relation) : 0.0;
        shared += DataStore.SumAmounts(store.DerivedParts(itemId, categoryId, relation).Where(part => AuraStats.Contains(part.Stat)));
        return store.TraitWeight(categoryId, relation) * shared;
    }
}

public static class OverlapWeights
{
    /// <summary>The shape of an item's shared effects; null for an item with none.</summary>
    public static OverlapShape? Of(DataStore store, string itemId) =>
        store.ItemEffects.TryGetValue(itemId, out var effects) ? Shape(effects) : null;

    /// <summary>Every item with a shared effect.</summary>
    public static Dictionary<string, OverlapShape> All(DataStore store) =>
        store.ItemEffects.ToDictionary(pair => pair.Key, pair => Shape(pair.Value));

    private static OverlapShape Shape(IReadOnlyList<ItemEffect> effects) => new(
        effects.Any(effect => effect.Scope == EffectScope.EnemyDebuff),
        effects.Where(effect => effect.Scope == EffectScope.Aura).Select(effect => effect.Effect).ToHashSet());
}
