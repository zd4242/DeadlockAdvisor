using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Vision;

/// <summary>What one read of the scoreboard's top bar found: the strip, why none was taken, and the net worth beside it.</summary>
/// <param name="Detection">Null when no hero strip was found.</param>
/// <param name="RejectedFit">The best grid's fit when it was too poor to be a strip, null when there was none to report.</param>
public sealed record ScoreboardRead(Detection? Detection, double? RejectedFit, NetWorthReading NetWorth);

/// <summary>
/// The front half of Detect, shared with the scoreboard read: the grid (the cached one, searched afresh when it
/// stopped fitting), the heroes in their slots, the match already applied leaned on, and the net worth. A pure
/// function of the capture, so it can run off the UI thread.
/// </summary>
public static class ScoreboardReader
{
    /// <summary>
    /// A cached grid fitting worse than this (<see cref="Detection.Fit"/>) is searched for afresh and
    /// the better read kept: it has stopped fitting, say after a change of HUD scale. Counting
    /// confident slots instead paid for a five-second search whenever a few players were dead.
    /// </summary>
    public const double CacheTrustFit = 0.7;

    /// <param name="roster">The hero applied to each slot of the match so far, by slot, which a read of the same match leans on.</param>
    /// <param name="rosterSelf">The slot applied as you, if any.</param>
    public static ScoreboardRead Read(ScreenCapture capture, TemplateBank bank, Geometry? cached,
        IReadOnlyDictionary<int, string> roster, int? rosterSelf, IProgress<double>? progress = null)
    {
        var detection = Detect(capture, bank, cached, progress);
        var rejectedFit = detection is { FoundStrip: false } ? detection.Fit : (double?)null;
        if (rejectedFit is not null)
            detection = null;
        if (detection is null)
            return new ScoreboardRead(null, rejectedFit, NetWorthReading.Empty);

        detection = RosterContinuity.Apply(detection, roster, rosterSelf);
        return new ScoreboardRead(detection, null, NetWorthReader.Read(capture.Band, detection.Geometry, NetWorthGlyphs.Bundled));
    }

    public static Detection? Detect(ScreenCapture capture, TemplateBank bank, Geometry? cached, IProgress<double>? progress = null)
    {
        // The band is only the top of the screen; the search sizes itself against the whole screen's height.
        var detection = Detector.Detect(capture.Band, bank, cached, screenHeight: capture.ScreenHeight, progress: progress);
        if (cached is not null && detection is not null && detection.Fit < CacheTrustFit)
        {
            // The HUD scale changed, or the cached grid was never as good as it looked: pay for a fresh
            // search and keep whichever fits better.
            var fresh = Detector.Detect(capture.Band, bank, screenHeight: capture.ScreenHeight, progress: progress);
            if (fresh is not null && fresh.Fit > detection.Fit)
                detection = fresh;
        }
        return detection;
    }
}
