namespace DeadlockAdvisor.Models;

/// <summary>
/// One of an item's real numbers, e.g. Spirit Resilience's 30% Spirit Resist. An item can carry
/// the same stat twice: once always-on and once conditional (extra resist below half health).
/// </summary>
public sealed record ItemStat(string ItemId, string Stat, string Label, double Value, string Unit, bool Conditional);
