using System.Text.Json.Nodes;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>A match data download that never ends by itself: it can only be cancelled.</summary>
public sealed class HeldMatchStats : IMatchStatsService
{
    public static readonly Patch Patch = new("09-29-2026", 1790726400);

    /// <summary>One patch, every match then its rank groups.</summary>
    public static readonly MatchFetchPlan Plan = new(1790856000, [Patch],
    [
        new FetchPhase(FetchPart.EveryMatch, Patch, Patch.Start, 1790856000, false, FetchReason.New, MatchFetchPlan.EveryMatchCalls(38)),
        new FetchPhase(FetchPart.Ranks, Patch, Patch.Start, 1790856000, false, FetchReason.New, MatchFetchPlan.RankCalls(38)),
    ]);

    public IProgress<MatchFetchProgress>? Progress { get; private set; }

    /// <summary>The plans asked for, by whether they include the rank groups.</summary>
    public List<bool> Planned { get; } = [];

    public Task<IReadOnlyList<Patch>> PatchesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Patch>>([Patch]);

    MatchFetchPlan IMatchStatsService.Plan(DataStore store, IReadOnlyList<Patch> patches, bool includeRanks)
    {
        Planned.Add(includeRanks);
        return includeRanks ? Plan : Plan with { Phases = [Plan.Phases[0]] };
    }

    public Task FetchAsync(DataStore store, MatchFetchPlan plan, IProgress<MatchFetchProgress>? progress, Action<MatchSegment> finished,
        CancellationToken cancellationToken)
    {
        Progress = progress;
        return new TaskCompletionSource().Task.WaitAsync(cancellationToken);
    }

    public FetchResult Apply(DataStore store, MatchSegment segment, IEnumerable<Patch> keep) => throw new NotSupportedException();
    public FetchResult Reanalyse(DataStore store, RankRange? range) => throw new NotSupportedException();
    public Task<Patch?> NewerPatchAsync(JsonObject meta, CancellationToken cancellationToken = default) => Task.FromResult<Patch?>(null);
}

/// <summary>An art download that reports what the test tells it to, and ends when the test says.</summary>
public sealed class HeldArtDownload : IArtDownloadService
{
    private readonly TaskCompletionSource<ArtDownloadReport> _finish = new();

    public IProgress<FetchProgress>? Progress { get; private set; }

    public int Started { get; private set; }

    public Task<ArtDownloadReport> DownloadAsync(DataStore store, string assetsDir, bool force, IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        Started++;
        Progress = progress;
        return _finish.Task.WaitAsync(cancellationToken);
    }

    public void Finish(ArtDownloadReport report) => _finish.SetResult(report);
}
