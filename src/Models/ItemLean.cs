namespace DeadlockAdvisor.Models;

/// <summary>
/// What an item that has the stat (or puts the enemy debuff) pushes its holder's damage toward: +1 is bullet damage, -1 spirit damage,
/// 0 neither. Conditional stats count at <see cref="ConditionalFactor"/> of it. A hand-kept model file (<c>stat_leans.csv</c>), so a
/// stat the table has no row for is one nobody has decided on, and the Model Health report lists it.
/// </summary>
public sealed record StatLean(string Stat, double Lean, double ConditionalFactor = 1.0, string Note = "");

/// <summary>An item's lean fixed by hand where its stats don't say (<c>item_lean_overrides.csv</c>), and why.</summary>
public sealed record ItemLeanOverride(string ItemId, double Lean, string Reason);

public enum LeanSource
{
    /// <summary>From the stats and enemy debuffs the item has, through the stat table.</summary>
    Derived,

    /// <summary>Fixed by hand.</summary>
    Override,

    /// <summary>The item's shop alone (weapon is bullet, spirit is spirit), because the stat table says nothing about it.</summary>
    Shop,
}

/// <param name="Key">The stat or effect that voted.</param>
/// <param name="Vote">What it added, after the conditional factor.</param>
public sealed record LeanVote(string Key, string Label, double Vote);

/// <summary>One item's lean, from -1 (spirit damage) to +1 (bullet damage), and where it came from.</summary>
public sealed record ItemLean(double Lean, LeanSource Source, IReadOnlyList<LeanVote> Votes)
{
    /// <summary>The lean the item's shop gives it alone: weapon items +1, spirit items -1, vitality 0.</summary>
    public static double OfShop(string category) => category.ToLowerInvariant() switch
    {
        "weapon" => 1.0,
        "spirit" => -1.0,
        _ => 0.0,
    };
}
