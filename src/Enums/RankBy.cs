namespace DeadlockAdvisor.Enums;

/// <summary>What orders the Match page's results. The match data is never added into the formula score; it can only reorder or filter.</summary>
public enum RankBy
{
    /// <summary>The formula score and the data strength added in common units (<see cref="Scoring.BlendScale"/>). The default.</summary>
    Both,

    /// <summary>The formula score.</summary>
    Formula,

    /// <summary>The match data's net verdict, <see cref="Scoring.ScoredItem.DataStrength"/>.</summary>
    MatchData,
}
