using System.Threading;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>One patch file a snapshot download fetches, and why.</summary>
public sealed record SnapshotStep(SnapshotPatch Entry, FetchReason Reason);

/// <summary>
/// What downloading the shared snapshot would do: the patches it has newer than what's stored. It always
/// brings the rank groups, and a patch stored complete is only fetched for rank groups it lacks.
/// </summary>
public sealed record SnapshotPlan(MatchSnapshot Snapshot, long Now, IReadOnlyList<SnapshotStep> Steps)
    : MatchDownloadPlan(Now, Snapshot.Patches.Select(patch => patch.Patch).ToList())
{
    public override bool HasWork => Steps.Count > 0;

    public override bool IncludesRanks => Steps.Any(step => step.Entry.Ranks);

    /// <summary>The gzipped files it fetches.</summary>
    public long Bytes => Steps.Sum(step => step.Entry.Bytes);

    /// <summary>When the newest patch in the snapshot was fetched from deadlock-api.com.</summary>
    public long FetchedAt => Snapshot.Patches.Count > 0 ? Snapshot.Patches[0].FetchedAt : Snapshot.CheckedAt;

    /// <summary>
    /// A patch that's new, has ended, or brings missing rank groups, or the current patch's counts older than
    /// <see cref="MatchSnapshot.RefreshAfter"/>: a download costs seconds, so the data can stay fresher than from the API.
    /// </summary>
    public override bool IsDue(IReadOnlyList<MatchSegment> stored) =>
        Steps.Any(step => step.Reason != FetchReason.Refresh
                          || stored.FirstOrDefault(segment => segment.Patch.Start == step.Entry.Start) is not { } segment
                          || Now - segment.FetchedAt >= MatchSnapshot.RefreshAfter.TotalSeconds);

    public override List<PlanStep> Describe() =>
        Snapshot.Patches.Select(patch =>
        {
            var step = Steps.FirstOrDefault(step => step.Entry.Start == patch.Start);
            return PlanStep.For(patch.Patch, step?.Reason, step?.Entry.Ranks ?? false);
        }).ToList();

    public override long DiskBytes(IReadOnlyList<MatchSegment> stored) =>
        Snapshot.Patches.Sum(patch =>
        {
            var ranks = Steps.Any(step => step.Entry.Start == patch.Start)
                ? patch.Ranks
                : stored.FirstOrDefault(segment => segment.Patch.Start == patch.Start)?.HasRanks ?? patch.Ranks;
            return ranks ? MatchFetchEstimate.PatchWithRanksBytes : MatchFetchEstimate.PatchBytes;
        });

    /// <summary>
    /// What to fetch from <paramref name="snapshot"/> for the counts stored. Null when the stored data has a
    /// newer patch than the snapshot (say, one deadlock-api.com was asked about since): the snapshot is behind
    /// and would drop it.
    /// </summary>
    public static SnapshotPlan? For(MatchSnapshot snapshot, IReadOnlyList<MatchSegment> stored, long now)
    {
        if (snapshot.Patches.Count == 0 || stored.Any(segment => segment.Patch.Start > snapshot.Patches[0].Start))
            return null;

        var steps = new List<SnapshotStep>();
        foreach (var entry in snapshot.Patches)
        {
            var segment = stored.FirstOrDefault(segment => segment.Patch.Start == entry.Start);
            var addsRanks = entry.Ranks && segment is { HasRanks: false };
            FetchReason? reason = segment switch
            {
                null => FetchReason.New,
                // Complete counts don't change, and newer ones than the snapshot's are better kept.
                { Complete: true } => addsRanks ? FetchReason.AddRanks : null,
                _ when entry.FetchedAt <= segment.FetchedAt => addsRanks && entry.Complete ? FetchReason.AddRanks : null,
                _ when entry.Ended && !segment.Ended || entry.Complete => FetchReason.Finish,
                _ => FetchReason.Refresh,
            };
            if (reason is { } why)
                steps.Add(new SnapshotStep(entry, why));
        }
        return new SnapshotPlan(snapshot, now, steps);
    }
}

public interface IMatchSnapshotService
{
    /// <summary>
    /// The published snapshot, or null when it can't be had: GitHub is unreachable, the manifest is of a
    /// version this app doesn't read, or the job behind it has stopped (<see cref="MatchSnapshot.StaleAfter"/>).
    /// </summary>
    Task<MatchSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>What downloading <paramref name="snapshot"/> would do now; null when it's behind the stored data.</summary>
    SnapshotPlan? Plan(DataStore store, MatchSnapshot snapshot);

    /// <summary>
    /// Fetch each patch file in turn, checking it, and hand its segment to <paramref name="finished"/> before
    /// the next, so a download stopped part-way keeps what it finished. Throws <see cref="OperationCanceledException"/>,
    /// an HTTP / timeout error, <see cref="System.IO.InvalidDataException"/> for a damaged file, or whatever
    /// <paramref name="finished"/> throws.
    /// </summary>
    Task FetchAsync(SnapshotPlan plan, IProgress<FetchProgress>? progress, Action<MatchSegment> finished, CancellationToken cancellationToken);
}

public static class MatchSnapshotServiceExtensions
{
    /// <summary>The snapshot's plan for the stored data, or null when there's no usable snapshot: one small download.</summary>
    public static async Task<SnapshotPlan?> PlanAsync(this IMatchSnapshotService service, DataStore store,
        CancellationToken cancellationToken = default) =>
        await service.SnapshotAsync(cancellationToken) is { } snapshot ? service.Plan(store, snapshot) : null;
}

/// <summary>The shared match data on GitHub (<see cref="MatchSnapshot"/>): the manifest, then the patch files it names.</summary>
public sealed class MatchSnapshotService : IMatchSnapshotService
{
    private readonly IDeadlockApi _api;
    private readonly Func<DateTimeOffset> _utcNow;

    public MatchSnapshotService(IDeadlockApi api)
        : this(api, () => DateTimeOffset.UtcNow)
    {
    }

    internal MatchSnapshotService(IDeadlockApi api, Func<DateTimeOffset> utcNow)
    {
        _api = api;
        _utcNow = utcNow;
    }

    private long Now => _utcNow().ToUnixTimeSeconds();

    public async Task<MatchSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = MatchSnapshot.Parse(await _api.GetBytesAsync(MatchSnapshot.ManifestUrl, DeadlockApi.UserAgent, cancellationToken));
            return snapshot.IsStale(Now) ? null : snapshot;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TimeoutException || MatchSnapshot.IsUnusable(ex))
        {
            return null;
        }
    }

    public SnapshotPlan? Plan(DataStore store, MatchSnapshot snapshot) => SnapshotPlan.For(snapshot, store.MatchSegments, Now);

    public async Task FetchAsync(SnapshotPlan plan, IProgress<FetchProgress>? progress, Action<MatchSegment> finished,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var entry = plan.Steps[i].Entry;
            progress?.Report(new FetchProgress(i, plan.Steps.Count, $"Patch {entry.Patch.Label}"));
            var gzipped = await _api.GetBytesAsync(MatchSnapshot.UrlOf(entry), DeadlockApi.UserAgent, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            finished(MatchSnapshot.Unpack(entry, gzipped));
        }
        progress?.Report(new FetchProgress(plan.Steps.Count, plan.Steps.Count, "Done"));
    }
}
