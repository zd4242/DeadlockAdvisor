using System.IO;
using System.Text.Json;
using System.Threading;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>
/// What the scheduled job behind <see cref="MatchSnapshot"/> does on each run (tools/MatchSnapshot runs it):
/// starts a data folder from the bundled seed, puts back the patches the last run published, and, when
/// something is due, syncs the heroes with the game and fetches it with the rank groups, exactly as the
/// app's own download would. A patch that's over and settled is never fetched again, the current one at
/// most every <see cref="MatchSnapshot.RefreshAfter"/>, so a run with nothing due makes one call, for the
/// patch list.
/// </summary>
public sealed class MatchSnapshotJob
{
    private readonly IDeadlockApi _api;
    private readonly TextWriter _log;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MatchSnapshotJob(IDeadlockApi api, TextWriter log)
        : this(api, log, () => DateTimeOffset.UtcNow, Task.Delay)
    {
    }

    internal MatchSnapshotJob(IDeadlockApi api, TextWriter log, Func<DateTimeOffset> utcNow, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _api = api;
        _log = log;
        _utcNow = utcNow;
        _delay = delay;
    }

    /// <summary>
    /// Bring the snapshot up to date: every patch kept, gzipped, and the manifest naming them, into
    /// <paramref name="outDir"/>. Throws on any failure, so nothing half-done is published.
    /// </summary>
    /// <param name="workDir">An empty folder to work in.</param>
    /// <param name="restoreDir">The last run's manifest and files, or null (or a folder without a manifest) on the first run.</param>
    public async Task<MatchSnapshot> RunAsync(string workDir, string? restoreDir, string outDir, CancellationToken cancellationToken = default)
    {
        var dataDir = Path.Combine(workDir, "data");
        DataService.SeedIfEmpty(dataDir);
        Restore(restoreDir, dataDir);
        var store = DataStore.Load(dataDir);

        var stats = new MatchStatsService(_api, _utcNow, _delay);
        var patches = await stats.PatchesAsync(cancellationToken);
        var plan = stats.Plan(store, patches, includeRanks: true);
        if (plan.IsDue(store.MatchSegments, MatchSnapshot.RefreshAfter))
        {
            // A hero added since the seed was published is asked about too.
            var sync = await new GameApiService(_api).SyncAsync(store, cancellationToken);
            _log.WriteLine($"Heroes: {MatchStatsService.Heroes(store).Count}. " + string.Join(" ", sync.Lines().Take(3)));
            plan = stats.Plan(store, patches, includeRanks: true);
            foreach (var step in plan.Describe())
                _log.WriteLine($"{step.Patch}: {step.Text}");
            _log.WriteLine($"Fetching: {plan.Calls} calls.");
            await stats.FetchAsync(store, plan, new PhaseLog(_log, plan), segment =>
            {
                store.PutMatchSegment(segment);
                store.SaveMatchSegment(segment);
            }, cancellationToken);
        }
        else
        {
            _log.WriteLine("Nothing is due: the published patches are kept as they are.");
        }
        store.PruneMatchSegments(plan.Keep.Select(patch => patch.Start).ToHashSet());
        return Write(store.MatchSegments, _utcNow().ToUnixTimeSeconds(), outDir);
    }

    /// <summary>The last run's patches into the data folder. One that's missing or damaged is left out, so it's fetched again.</summary>
    private void Restore(string? restoreDir, string dataDir)
    {
        var manifestPath = restoreDir is null ? null : Path.Combine(restoreDir, MatchSnapshot.ManifestFile);
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            _log.WriteLine("No previous snapshot: starting afresh.");
            return;
        }
        var countsDir = Path.Combine(dataDir, DataStore.MatchCountsDir);
        Directory.CreateDirectory(countsDir);
        foreach (var entry in MatchSnapshot.Parse(File.ReadAllBytes(manifestPath)).Patches)
        {
            try
            {
                var segment = MatchSnapshot.Unpack(entry, File.ReadAllBytes(Path.Combine(restoreDir!, entry.File)));
                File.WriteAllBytes(Path.Combine(countsDir, segment.FileName), segment.ToJsonBytes());
            }
            catch (Exception ex) when (MatchSnapshot.IsUnusable(ex))
            {
                _log.WriteLine($"Patch {entry.Patch.Label} from the last run is unusable ({ex.Message}); fetching it again.");
            }
        }
    }

    /// <summary>Each segment gzipped under its content-named file, and the manifest naming them.</summary>
    public static MatchSnapshot Write(IReadOnlyList<MatchSegment> segments, long checkedAt, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var entries = new List<SnapshotPatch>();
        foreach (var segment in segments.OrderByDescending(segment => segment.Patch.Start))
        {
            var (entry, gzipped) = MatchSnapshot.Pack(segment);
            File.WriteAllBytes(Path.Combine(outDir, entry.File), gzipped);
            entries.Add(entry);
        }
        var snapshot = new MatchSnapshot(checkedAt, entries);
        File.WriteAllBytes(Path.Combine(outDir, MatchSnapshot.ManifestFile), snapshot.ToJsonBytes());
        return snapshot;
    }

    /// <summary>A line per phase as it starts, and per pause the API asks for.</summary>
    private sealed class PhaseLog(TextWriter log, MatchFetchPlan plan) : IProgress<MatchFetchProgress>
    {
        private readonly Lock _lock = new();
        private int _phase = -1;

        public void Report(MatchFetchProgress value)
        {
            lock (_lock)
            {
                if (value.Phase != _phase)
                {
                    _phase = value.Phase;
                    log.WriteLine($"  {plan.Phases[_phase].Text}: {plan.Phases[_phase].Calls} calls");
                }
                if (value.Wait is { } wait)
                    log.WriteLine($"    waiting {wait.Length.TotalSeconds:0} s: {wait.Reason}");
            }
        }
    }
}
