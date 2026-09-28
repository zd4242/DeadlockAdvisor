namespace DeadlockAdvisor.Enums;

/// <summary>Which of the current match survives a randomize; the rest is drawn fresh.</summary>
public enum RandomizeKeep
{
    Nothing,

    /// <summary>Your own hero.</summary>
    Self,

    /// <summary>You and your allies, so only the enemy team changes (plus any empty ally slots).</summary>
    OwnTeam,
}
