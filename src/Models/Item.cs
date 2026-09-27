namespace DeadlockAdvisor.Models;

/// <param name="Category">The shop category (weapon / vitality / spirit), unrelated to trait categories.</param>
/// <param name="SingleTarget">
/// Its active is cast on one hero (Decay, Knockdown, Rescue Beam), so it's scored on its best targets;
/// see <see cref="Scoring.BestTargets"/>. Set by the game sync.
/// </param>
public sealed record Item(string ItemId, string ItemName, string Category, int Tier, long GameId = 0, int Cost = 0, bool SingleTarget = false);
