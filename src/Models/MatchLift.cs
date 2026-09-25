namespace DeadlockAdvisor.Models;

/// <summary>
/// How much an item's win rate moves against one enemy hero ("against") or on one hero of yours
/// ("as"), from real matches, in win-rate points with the hero's own strength taken out.
/// <see cref="LiftShrunk"/> is the number to use: pulled toward 0 as far as the sample is noisy.
/// </summary>
/// <param name="Relation">"against" or "as", kept as written in the file.</param>
/// <param name="Scope">"full" or "lane", kept as written in the file.</param>
public sealed record MatchLift(
    string ItemId,
    string HeroId,
    string Relation,
    string Scope,
    int Matches,
    double Lift,
    double Se,
    double LiftShrunk);
