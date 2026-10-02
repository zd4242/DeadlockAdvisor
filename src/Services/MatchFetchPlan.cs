using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;

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

    /// <summary>
    /// What happens to each patch kept, newest first: ("Patch 09-29", "refreshed, it's still collecting
    /// matches · with rank groups"), or that it's kept as it is.
    /// </summary>
    public List<(string Patch, string Text)> Describe(IReadOnlyList<MatchSegment> stored) =>
        Keep.Select(patch =>
        {
            var everyMatch = Phases.FirstOrDefault(phase => phase.Patch.Start == patch.Start && phase.Part == FetchPart.EveryMatch);
            var ranks = Phases.Any(phase => phase.Patch.Start == patch.Start && phase.Part == FetchPart.Ranks);
            var text = everyMatch?.Reason switch
            {
                FetchReason.New => "new",
                FetchReason.Refresh => "refreshed: it's still collecting matches",
                FetchReason.Finish => "finished: it's over, so this is the last time",
                _ => ranks ? "kept, adding its rank groups" : "kept as it is: complete",
            };
            return ($"Patch {patch.Label}", text + (ranks ? " · with rank groups" : ""));
        }).ToList();

    /// <summary>About how much the kept patches' counts take on disk once this is done.</summary>
    public long DiskBytes(IReadOnlyList<MatchSegment> stored) =>
        Keep.Sum(patch =>
        {
            var refreshed = Phases.Any(phase => phase.Patch.Start == patch.Start && phase.Part == FetchPart.EveryMatch);
            var ranks = Phases.Any(phase => phase.Patch.Start == patch.Start && phase.Part == FetchPart.Ranks)
                        || !refreshed && stored.Any(segment => segment.Patch.Start == patch.Start && segment.HasRanks);
            return ranks ? MatchFetchEstimate.PatchWithRanksBytes : MatchFetchEstimate.PatchBytes;
        });

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

/// <summary>What a download is likely to take, from how the last ones went.</summary>
/// <param name="SecondsPerCall">The pace: the requests go out 0.4 s apart at best.</param>
/// <param name="BytesPerCall">As they come over the wire, compressed.</param>
public sealed record MatchFetchEstimate(double SecondsPerCall, double BytesPerCall)
{
    /// <summary>Measured on deadlock-api.com in October 2026: 560 calls in 227 s, 8.1 MiB.</summary>
    public static readonly MatchFetchEstimate Measured = new(0.405, 15_200);

    /// <summary>One patch's counts on disk, every match only, and with the rank groups too (measured alike).</summary>
    public const long PatchBytes = 450_000;
    public const long PatchWithRanksBytes = 1_150_000;

    /// <summary>A run this short says little about the pace: the setup calls weigh too much.</summary>
    public const int MinCallsToLearn = 40;

    public TimeSpan Time(MatchFetchPlan plan) => TimeSpan.FromSeconds(plan.Calls * SecondsPerCall);

    public long Bytes(MatchFetchPlan plan) => (long)(plan.Calls * BytesPerCall);

    /// <summary>The estimate moved a third of the way toward how a finished run went.</summary>
    public MatchFetchEstimate Learn(int calls, TimeSpan elapsed, long bytes)
    {
        if (calls < MinCallsToLearn)
            return this;
        return new MatchFetchEstimate(
            SecondsPerCall + (elapsed.TotalSeconds / calls - SecondsPerCall) / 3,
            BytesPerCall + ((double)bytes / calls - BytesPerCall) / 3);
    }

    /// <summary>"about 40 s", "about 3 min".</summary>
    public static string DescribeTime(TimeSpan time) =>
        time < TimeSpan.FromSeconds(55)
            ? $"about {Math.Max(5, (int)Math.Round(time.TotalSeconds / 5) * 5)} s"
            : $"about {(int)Math.Ceiling(time.TotalMinutes)} min";

    /// <summary>"1.2 MB", "350 KB".</summary>
    public static string DescribeBytes(long bytes) =>
        bytes < 1_000_000 ? $"{Math.Max(1, bytes / 1000)} KB" : $"{NumberFormat.Fixed(bytes / 1_000_000.0, 1)} MB";
}