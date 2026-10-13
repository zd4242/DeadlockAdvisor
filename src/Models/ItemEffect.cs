namespace DeadlockAdvisor.Models;

/// <summary>Whom a shared effect reaches.</summary>
public static class EffectScope
{
    /// <summary>A debuff the item puts on enemies: Bullet Resist Shredder's lowered Bullet Resist.</summary>
    public const string EnemyDebuff = "enemy_debuff";

    /// <summary>A passive stat the item gives everyone around it: Heroic Aura's Bullet Resist.</summary>
    public const string Aura = "aura";
}

/// <summary>
/// An effect of an item that lands on other heroes, so a second source of it doesn't add up. Written by the
/// game sync (<c>item_effects.csv</c>), never by hand.
/// </summary>
/// <param name="Effect">What it does: a debuff's name, or for an aura the stat it gives (an <see cref="ItemStat.Stat"/>).</param>
/// <param name="Scope">An <see cref="EffectScope"/>.</param>
/// <param name="Value">How strong, always positive: a Bullet Resist reduction of 10% is 10.</param>
public sealed record ItemEffect(string ItemId, string Effect, string Scope, double Value);
