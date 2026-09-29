using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Models;

/// <param name="Category">The shop category (weapon / vitality / spirit), unrelated to trait categories.</param>
/// <param name="CastOn">
/// Its active is cast on one hero of this team: <see cref="Relation.Against"/> for an enemy (Decay,
/// Knockdown), <see cref="Relation.With"/> for an ally (Rescue Beam). Null when it isn't single-target.
/// <see cref="Scoring.BestTargets.AppliesTo"/> says which relations are scored on their best targets. Set by the game sync.
/// </param>
public sealed record Item(string ItemId, string ItemName, string Category, int Tier, long GameId = 0, int Cost = 0, Relation? CastOn = null);
