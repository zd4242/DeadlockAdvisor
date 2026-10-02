namespace DeadlockAdvisor.Models;

/// <summary>
/// How much an item's win rate moves against one enemy hero ("against") or on one hero of yours
/// ("as"), from real matches, in win-rate points with the hero's own strength taken out.
/// <see cref="LiftShrunk"/> is the number to use: pulled toward 0 as far as the sample is noisy, then
/// moved by <see cref="RankShift"/> toward the ranks the data leans to.
/// </summary>
/// <param name="Relation">"against" or "as", kept as written in the file.</param>
/// <param name="Matches">Over every match, as are <paramref name="Lift"/> and <paramref name="Se"/>.</param>
/// <param name="RankShift">What leaning toward the chosen ranks added to <paramref name="LiftShrunk"/>; 0 over every match.</param>
public sealed record MatchLift(
    string ItemId,
    string HeroId,
    string Relation,
    int Matches,
    double Lift,
    double Se,
    double LiftShrunk,
    double RankShift = 0);
