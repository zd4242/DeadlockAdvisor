using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Models;

public readonly record struct ScoreKey(string HeroId, string CategoryId);

public readonly record struct CoefficientKey(string ItemId, string CategoryId, Relation Relation);

public readonly record struct WeightKey(string CategoryId, Relation Relation);

public readonly record struct StatRuleKey(string Stat, string CategoryId, Relation Relation);

public readonly record struct MatchLiftKey(string ItemId, string HeroId, string Relation);
