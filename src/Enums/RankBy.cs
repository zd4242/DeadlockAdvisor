namespace DeadlockAdvisor.Enums;

/// <summary>What orders the Match page's results. The match data is never added into the formula score; it can only reorder or filter.</summary>
public enum RankBy
{
    /// <summary>The formula score.</summary>
    Formula,

    /// <summary>The match data's net verdict, <see cref="Scoring.ScoredItem.DataStrength"/>.</summary>
    MatchData,

    /// <summary>Only items both rate above 0, as high as the less keen of the two, each as a share of its best item.</summary>
    Both,
}
