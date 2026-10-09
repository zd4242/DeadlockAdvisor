namespace DeadlockAdvisor.Features.Match.Detect;

/// <summary>How a Detect run ended, for telling someone who isn't looking at the window.</summary>
public enum DetectOutcome
{
    /// <summary>Read and applied without review.</summary>
    Applied,

    /// <summary>Opened the review.</summary>
    NeedsReview,

    /// <summary>The capture had no hero strip in it.</summary>
    NothingFound,

    /// <summary>The screen couldn't be captured.</summary>
    CaptureFailed,

    /// <summary>There was no reference art to match against.</summary>
    NoArt,
}
