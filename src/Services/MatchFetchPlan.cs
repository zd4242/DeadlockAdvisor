using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Services;

/// <summary>Which part of a patch's matches a phase fetches.</summary>
public enum FetchPart
{
    EveryMatch,
    Ranks,
}

/// <summary>Why a patch is in a download.</summary>
public enum FetchReason
{
    /// <summary>Nothing from this patch yet.</summary>
    New,

    /// <summary>The current patch, which keeps collecting matches.</summary>
    Refresh,

    /// <summary>A patch that has ended since it was fetched, or hadn't settled yet: once more, then never again.</summary>
    Finish,

    /// <summary>A finished patch fetched without its rank groups.</summary>
    AddRanks,
}

/// <summary>One step of a download: one part of one patch's matches, over one window.</summary>
/// <param name="Until">Unix seconds, inclusive.</param>
/// <param name="Ended">The next patch is out, so <paramref name="Until"/> is this one's end.</param>
/// <param name="Calls">How many /item-stats calls it takes.</param>
public sealed record FetchPhase(FetchPart Part, Patch Patch, long From, long Until, bool Ended, FetchReason Reason, int Calls)
{
    /// <summary>"09-29 · every match", "09-16 · rank groups".</summary>
    public string Text => $"{Patch.Label} · {(Part == FetchPart.EveryMatch ? "every match" : "rank groups")}";
}

/// <summary>
/// What a download fetches and keeps, worked out before it starts so it can be described and timed. The
/// match data covers the current patch and the one before, and a third when those two are both young.
/// A patch that's over and has settled is never fetched again, so a refresh costs one patch's calls,
/// however long ago the last one was: the API adds the matches up itself, so a call over a day costs
/// the same as one over a month. Every match comes first, for every patch, so the data is in use within
/// a minute; the rank groups, five times the calls, follow.
/// </summary>
/// <param name="Now">Unix seconds: when the windows that are still open end.</param>
/// <param name="Keep">The patches the match data covers, newest first; any other patch's counts are dropped.</param>
public sealed record MatchFetchPlan(long Now, IReadOnlyList<Patch> Keep, IReadOnlyList<FetchPhase> Phases)
{
    /// <summary>Under this many days in the current and the previous patch together, the patch before them is kept too.</summary>
    public const int MinHistoryDays = 14;

    /// <summary>Every match over one window: every match and your hero, both one call per half, then each enemy hero's.</summary>
    public static int EveryMatchCalls(int heroCount) => 2 * (heroCount + 2);

    /// <summary>The same for each rank group.</summary>
    public static int RankCalls(int heroCount) => MatchStatsMath.RankGroups.Count * EveryMatchCalls(heroCount);

    /// <summary>The /item-stats calls the whole download takes.</summary>
    public int Calls => Phases.Sum(phase => phase.Calls);

    public bool IncludesRanks => Phases.Any(phase => phase.Part == FetchPart.Ranks);

    /// <param name="segments">What's stored now.</param>
    /// <param name="patches">Newest first, as <see cref="MatchStatsMath.ParsePatches"/> gives them.</param>
    public static MatchFetchPlan For(IReadOnlyList<MatchSegment> segments, IReadOnlyList<Patch> patches, long now, bool includeRanks, int heroCount)
    {
        // A patch is dated from the midnight after its title's date, so a patch out this evening hasn't started yet.
        var started = patches.Where(patch => patch.Start < now).ToList();
        var keep = started.Take(2).ToList();
        if (started.Count > 2 && now - started[1].Start < MinHistoryDays * 86400L)
            keep.Add(started[2]);

        var everyMatch = new List<FetchPhase>();
        var ranks = new List<FetchPhase>();
        for (var k = 0; k < keep.Count; k++)
        {
            var patch = keep[k];
            var stored = segments.FirstOrDefault(segment => segment.Patch.Start == patch.Start);
            if (stored is { Complete: true })
            {
                if (includeRanks && !stored.HasRanks)
                    ranks.Add(new FetchPhase(FetchPart.Ranks, patch, stored.From, stored.Until, stored.Ended, FetchReason.AddRanks, RankCalls(heroCount)));
                continue;
            }

            var ended = k > 0;
            var until = ended ? keep[k - 1].Start - 1 : now;
            var reason = stored is null ? FetchReason.New : ended ? FetchReason.Finish : FetchReason.Refresh;
            everyMatch.Add(new FetchPhase(FetchPart.EveryMatch, patch, patch.Start, until, ended, reason, EveryMatchCalls(heroCount)));
            if (includeRanks)
                ranks.Add(new FetchPhase(FetchPart.Ranks, patch, patch.Start, until, ended, reason, RankCalls(heroCount)));
        }
        return new MatchFetchPlan(now, keep, [.. everyMatch, .. ranks]);
    }
}
