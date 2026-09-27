using System.Text.Json.Nodes;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>A match stats fetch that never ends by itself: it can only be cancelled.</summary>
public sealed class HeldMatchStats : IMatchStatsService
{
    public IProgress<FetchProgress>? Progress { get; private set; }

    public Task<MatchCounts> FetchAsync(DataStore store, IProgress<FetchProgress>? progress, CancellationToken cancellationToken)
    {
        Progress = progress;
        return new TaskCompletionSource<MatchCounts>().Task.WaitAsync(cancellationToken);
    }

    public FetchResult Apply(DataStore store, MatchCounts counts) => throw new NotSupportedException();
    public FetchResult Refilter(DataStore store, RankRange? range) => throw new NotSupportedException();
    public Task<Patch?> NewerPatchAsync(JsonObject meta, CancellationToken cancellationToken = default) => Task.FromResult<Patch?>(null);
}

/// <summary>An art download that reports what the test tells it to, and ends when the test says.</summary>
public sealed class HeldArtDownload : IArtDownloadService
{
    private readonly TaskCompletionSource<ArtDownloadReport> _finish = new();

    public IProgress<FetchProgress>? Progress { get; private set; }

    public Task<ArtDownloadReport> DownloadAsync(DataStore store, string assetsDir, bool force, IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        Progress = progress;
        return _finish.Task.WaitAsync(cancellationToken);
    }

    public void Finish(ArtDownloadReport report) => _finish.SetResult(report);
}
