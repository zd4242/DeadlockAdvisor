using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Models;

/// <summary>
/// "Every point of <see cref="Stat"/> is worth <see cref="PerUnit"/> of coefficient on
/// (category, relation)"; conditional stats count at <see cref="ConditionalFactor"/> of that.
/// </summary>
public sealed record StatRule(
    string Stat,
    string CategoryId,
    Relation Relation,
    double PerUnit,
    double ConditionalFactor = 1.0,
    string Note = "");
