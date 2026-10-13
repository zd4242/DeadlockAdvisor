using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Scoring;

/// <summary>
/// What a maintainer should look at in the item leans (<see cref="DataStore.LeanOf"/>): the items whose stats send them somewhere other than
/// their shop's colour, the overrides and whether they still say anything the stats don't, and the stats nobody has voted on. Lines for the
/// Model Health report; nothing here changes a score.
/// </summary>
public static class LeanReview
{
    /// <summary>A derived lean this far from the shop's colour, on the other side of 0, is worth a look.</summary>
    public const double ShownFrom = 0.25;

    public const int Limit = 25;

    public static List<string> Lines(DataStore store)
    {
        var lines = new List<string>();
        if (store.StatLeans.Count == 0 && store.ItemLeanOverrides.Count == 0)
            return lines;

        var against = store.Items.Values
            .Where(item => ItemScoring.Tiers.Contains(item.Tier) && !store.ItemLeanOverrides.ContainsKey(item.ItemId))
            .Select(item => (Item: item, Lean: store.LeanOf(item.ItemId)))
            .Where(entry => entry.Lean.Source == LeanSource.Derived && Opposes(entry.Lean.Lean, ItemLean.OfShop(entry.Item.Category)))
            .OrderByDescending(entry => Math.Abs(entry.Lean.Lean - ItemLean.OfShop(entry.Item.Category)))
            .ToList();
        lines.Add("");
        lines.Add(against.Count == 0
            ? "No item's stats send it away from its shop's damage type (a weapon item toward bullet damage, a spirit item toward spirit damage)."
            : $"Items whose stats send them away from their shop's damage type ({against.Count}); check each really does what the votes say:");
        foreach (var (item, lean) in against.Take(Limit))
            lines.Add($"  T{item.Tier} {item.ItemName} ({item.Category}): {Format.SignedFixed(lean.Lean, 2)} from "
                      + string.Join(", ", lean.Votes.Where(vote => vote.Vote != 0).Select(vote => $"{vote.Label} {Format.SignedFixed(vote.Vote, 2)}")));
        if (against.Count > Limit)
            lines.Add($"  … and {against.Count - Limit} more");

        if (store.ItemLeanOverrides.Count > 0)
        {
            lines.Add("");
            lines.Add($"Item leans fixed by hand ({store.ItemLeanOverrides.Count}):");
            foreach (var entry in store.ItemLeanOverrides.Values)
            {
                var name = store.Items.TryGetValue(entry.ItemId, out var item) ? item.ItemName : entry.ItemId;
                var derived = store.DerivedLeanOf(entry.ItemId);
                var same = Math.Abs(derived.Lean - Math.Clamp(entry.Lean, -1.0, 1.0)) < 0.01;
                lines.Add($"  {name}: {Format.SignedFixed(entry.Lean, 2)}, \"{entry.Reason}\"" + (same ? " (the stats say the same now: remove it)" : $" (the stats say {Format.SignedFixed(derived.Lean, 2)})"));
            }
        }

        var unvoted = StatsWithoutAVote(store);
        if (store.StatLeans.Count > 0 && unvoted.Count > 0)
        {
            lines.Add("");
            lines.Add($"Stats and debuffs with no row in {DataStore.StatLeansFile} ({unvoted.Count}); add one, a 0 if it points neither way: "
                      + string.Join(", ", unvoted.Select(pair => $"{pair.Key} ({pair.Value} item{(pair.Value == 1 ? "" : "s")})")));
        }
        return lines;
    }

    /// <summary>The stats items have and the enemy debuffs they put on that the table has no row for, with how many items each is on.</summary>
    public static SortedDictionary<string, int> StatsWithoutAVote(DataStore store)
    {
        var missing = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var keys = store.ItemStats.Values.Select(stats => stats.Select(stat => stat.Stat).Distinct())
            .Concat(store.ItemEffects.Values.Select(effects => effects.Where(effect => effect.Scope == EffectScope.EnemyDebuff).Select(effect => effect.Effect).Distinct()));
        foreach (var key in keys.SelectMany(key => key).Where(key => !store.StatLeans.ContainsKey(key)))
            missing[key] = missing.GetValueOrDefault(key) + 1;
        return missing;
    }

    private static bool Opposes(double lean, double shop) =>
        shop == 0 ? Math.Abs(lean) >= ShownFrom : Math.Sign(lean) != Math.Sign(shop) && Math.Abs(lean) >= ShownFrom;
}
