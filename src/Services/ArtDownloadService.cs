using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.GameApi;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Services;

/// <param name="Downloaded">Files that weren't there before.</param>
/// <param name="Updated">The ids whose file was replaced, because the API's copy had changed.</param>
/// <param name="Skipped">Files already there and current, or put there by hand.</param>
/// <param name="Unmatched">"haze (Haze) -- download failed: ...": what wasn't fetched, and why.</param>
public sealed record ArtGroupReport(string Label, int Wanted, int Offered, int Downloaded, int Skipped, IReadOnlyList<string> Unmatched,
    IReadOnlyList<string> Updated);

/// <param name="Derivation">The top-bar portraits cut from the hero cards.</param>
public sealed record ArtDownloadReport(IReadOnlyList<ArtGroupReport> Groups, TopbarDerivation.Outcome? Derivation = null)
{
    public int Downloaded => Groups.Sum(group => group.Downloaded);
    public int Updated => Groups.Sum(group => group.Updated.Count);

    public List<string> Lines()
    {
        var lines = Groups
            .Select(group => $"{group.Label}: {group.Downloaded} downloaded, {group.Updated.Count} updated, {group.Skipped} already present "
                             + $"({group.Wanted} wanted, {group.Offered} offered by the API)")
            .ToList();
        if (Derivation is { } derivation)
        {
            lines.Add($"Top-bar portraits cut from the hero cards (normal, critical, on fire): {derivation.Derived.Count} hero(es)");
            lines.AddRange(derivation.Failed.Select(line => $"  couldn't cut {line}"));
        }
        var unmatched = Groups.SelectMany(group => group.Unmatched.Select(line => $"  - {group.Label}: {line}")).ToList();
        if (unmatched.Count > 0)
        {
            lines.Add("");
            lines.Add($"{unmatched.Count} not fetched -- rename a file by hand if you find the art "
                      + "(a hero missing top-bar art is never proposed by Detect from screen):");
            lines.AddRange(unmatched);
        }
        else
        {
            lines.Add("");
            lines.Add("Everything in the CSVs matched something in the API.");
        }
        return lines;
    }
}

public interface IArtDownloadService
{
    /// <summary>
    /// Fetch hero portraits, item icons, rank badges, and the top-bar art and hero cards detection
    /// matches against into <paramref name="assetsDir"/>, named after our ids. What's there already is kept
    /// unless the API's copy has changed since it was downloaded, or <paramref name="force"/> asks
    /// for everything again. Throws if the API's lists can't be fetched, or if the connection fails
    /// several images in a row, keeping what arrived; a single failed image is reported instead.
    /// </summary>
    Task<ArtDownloadReport> DownloadAsync(DataStore store, string assetsDir, bool force, IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Downloads the hero and item art and the top-bar portraits. Matching is by name, not id: the
/// API's ids are Valve class names that don't line up with ours, but the display names do once
/// punctuation and case are ignored. Anything unmatched is listed rather than guessed at: a wrong
/// portrait is worse than a placeholder tile.
/// </summary>
public sealed class ArtDownloadService : IArtDownloadService
{
    public const string UserAgent = "deadlock-advisor/1.0 (asset downloader)";

    /// <summary>
    /// How many images in a row can fail to connect before the download gives up. Each is a connection that
    /// went nowhere, so past a few the rest would only wait their turn to do the same.
    /// </summary>
    public const int MaxConnectionFailures = 3;

    /// <summary>How many images of one group are asked for at once. The CDN is a community service's bucket, so not many.</summary>
    public const int MaxInFlight = 4;

    /// <summary>How long to wait before each retry of an image the site answered with a server error or "slow down".</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    private readonly IGameApiService _gameApi;
    private readonly IDeadlockApi _api;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ArtDownloadService(IGameApiService gameApi, IDeadlockApi api) : this(gameApi, api, Task.Delay)
    {
    }

    internal ArtDownloadService(IGameApiService gameApi, IDeadlockApi api, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _gameApi = gameApi;
        _api = api;
        _delay = delay;
    }

    // Hero cards are 280x380 character art, cropped square for the UI; item shop images are the
    // full-colour 200x200 tiles (the plain `image` is a white glyph). Top-bar art is what the game
    // draws along the top of the screen, which is what detection matches against, and the cards
    // (normal, critical and on a streak) are what the portraits the API has no top-bar art for are
    // cut from.
    private static readonly string[] _heroImageKeys = ["icon_hero_card", "icon_image_small", "minimap_image"];
    private static readonly string[] _itemImageKeys = ["shop_image", "image"];
    private static readonly string[] _topBarImageKeys = ["top_bar_vertical_image"];
    private static readonly string[] _rankImageKeys = ["large"];
    private static readonly string[] _imageSuffixes = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <param name="Owned">
    /// Whether the app owns the folder's files: a file that differs from the API's is replaced even
    /// if this app didn't download it. Portraits and icons can be art someone put there by hand.
    /// </param>
    private sealed record Group(string Label, IReadOnlyDictionary<string, string> Wanted, JsonArray Records, string[] ImageKeys, string Directory,
        bool Owned);

    /// <param name="Already">The image's file as it is on disk now, whatever its extension, or null.</param>
    /// <param name="Known">What the manifest says that file is.</param>
    private sealed record Image(string Id, string Name, string Url, string Destination, string? Already, ArtManifest.Entry? Known);

    private enum Outcome { Downloaded, Updated, Skipped, Failed }

    /// <param name="Problem">Why an image wasn't fetched, for the report.</param>
    private sealed record ImageResult(string Id, Outcome Outcome, string? Problem = null);

    /// <summary>What every group of one download shares.</summary>
    private sealed class DownloadRun(ArtManifest manifest, bool force)
    {
        // The file each URL was written to, or confirmed current at, in this run: a later group wanting
        // the same image copies it instead of asking again.
        private readonly ConcurrentDictionary<string, string> _sources = new();
        private int _connectionFailures;
        private Exception? _fatal;

        public ArtManifest Manifest { get; } = manifest;
        public bool Force { get; } = force;

        /// <summary>The failure that ended the download, once the connection has failed too many times.</summary>
        public Exception? Fatal => _fatal;

        public void HaveFile(string url, string path) => _sources[url] = path;

        public (string Path, ArtManifest.Entry Entry)? Source(string url) =>
            _sources.TryGetValue(url, out var path) && File.Exists(path) && Manifest.Get(path) is { } entry ? (path, entry) : null;

        /// <summary>Another image the connection itself failed for; true once that has happened too many times in a row.</summary>
        public bool ConnectionFailed(Exception failure)
        {
            if (Interlocked.Increment(ref _connectionFailures) < MaxConnectionFailures)
                return false;
            Interlocked.CompareExchange(ref _fatal, failure, null);
            return true;
        }

        public void Reached() => Interlocked.Exchange(ref _connectionFailures, 0);
    }

    public async Task<ArtDownloadReport> DownloadAsync(DataStore store, string assetsDir, bool force, IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var heroRecords = await _gameApi.FetchHeroesAsync(cancellationToken);
        var itemRecords = await _gameApi.FetchUpgradesAsync(cancellationToken);
        var heroes = store.Heroes.Values.ToDictionary(hero => hero.HeroId, hero => hero.HeroName);
        var items = store.Items.Values.ToDictionary(item => item.ItemId, item => item.ItemName);
        var rankRecords = await FetchRanksAsync(cancellationToken);
        // Tier 0 is Obscurus, the unranked, which no rank group names.
        var ranks = new Dictionary<string, string>();
        foreach (var record in rankRecords.OfType<JsonNode>())
        {
            var (tier, name) = ((int)JsonRecord.Int(record, "tier"), JsonRecord.Text(record, "name"));
            if (tier > 0 && name.Length > 0)
                ranks.TryAdd(RankBucket.ArtId(tier), name);
        }
        var topbarDir = Path.Combine(assetsDir, "topbar");
        Group[] groups =
        [
            new("Hero portraits", heroes, heroRecords, _heroImageKeys, Path.Combine(assetsDir, "heroes"), Owned: false),
            new("Item icons", items, itemRecords, _itemImageKeys, Path.Combine(assetsDir, "items"), Owned: false),
            new("Top-bar portraits", heroes, heroRecords, _topBarImageKeys, topbarDir, Owned: true),
            new("Hero cards", heroes, heroRecords, ["icon_hero_card"], TopbarDerivation.CardFolder(topbarDir, PortraitState.Normal), Owned: true),
            new("Critical cards", heroes, heroRecords, ["hero_card_critical"], TopbarDerivation.CardFolder(topbarDir, PortraitState.Critical), Owned: true),
            new("On-fire cards", heroes, heroRecords, ["hero_card_gloat"], TopbarDerivation.CardFolder(topbarDir, PortraitState.Gloat), Owned: true),
            new("Rank badges", ranks, rankRecords, _rankImageKeys, Path.Combine(assetsDir, "ranks"), Owned: false),
        ];

        var manifest = ArtManifest.Load(assetsDir);
        var total = groups.Sum(group => group.Wanted.Count);
        var done = 0;
        var reports = new List<ArtGroupReport>();
        var run = new DownloadRun(manifest, force);
        try
        {
            foreach (var group in groups)
            {
                reports.Add(await RunGroupAsync(group, run, text =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new FetchProgress(done++, total, text));
                }, cancellationToken));
            }
        }
        finally
        {
            // What arrived before a cancel or failure is on disk, so it's in the manifest too.
            manifest.Save();
        }
        progress?.Report(new FetchProgress(total, total, "Cutting top-bar portraits from the cards"));
        var derivation = TopbarDerivation.Run(topbarDir, heroes.Keys, force);
        progress?.Report(new FetchProgress(total, total, "done"));
        return new ArtDownloadReport(reports, derivation);
    }

    /// <summary>
    /// The ranks and their badges. They only dress the rank pickers, so a site that won't answer for them
    /// costs those their icons rather than the whole download.
    /// </summary>
    private async Task<JsonArray> FetchRanksAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _api.GetJsonAsync(MatchStatsService.Ranks, cancellationToken) as JsonArray ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// One group's images, up to <see cref="MaxInFlight"/> at a time. The tasks start from the calling context and
    /// are bounded by a semaphore, not <c>Parallel.ForEachAsync</c>, so their continuations (and the progress
    /// reports) stay on the UI thread when there is one.
    /// </summary>
    private async Task<ArtGroupReport> RunGroupAsync(Group group, DownloadRun run, Action<string> step, CancellationToken cancellationToken)
    {
        var byKey = new Dictionary<string, JsonNode>();
        foreach (var record in group.Records.OfType<JsonNode>())
            byKey.TryAdd(GameSync.Norm(JsonRecord.Text(record, "name")), record);
        Directory.CreateDirectory(group.Directory);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(MaxInFlight);
        var results = new List<Task<ImageResult>>();
        try
        {
            foreach (var (ourId, ourName) in group.Wanted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                step($"{group.Label}: {ourName}");
                if (!byKey.TryGetValue(GameSync.Norm(ourName), out var record) && !byKey.TryGetValue(GameSync.Norm(ourId), out record))
                {
                    results.Add(Task.FromResult(new ImageResult(ourId, Outcome.Failed, $"{ourId} ({ourName})")));
                    continue;
                }
                if (PickImage(record, group.ImageKeys) is not { } url)
                {
                    results.Add(Task.FromResult(new ImageResult(ourId, Outcome.Failed, $"{ourId} ({ourName}) -- matched, but has no image")));
                    continue;
                }

                var suffix = Path.GetExtension(new Uri(url).AbsolutePath);
                var destination = Path.Combine(group.Directory, ourId + (suffix.Length > 0 ? suffix : ".png"));
                var already = Existing(group.Directory, ourId);
                var known = already is null ? null : run.Manifest.Get(already);
                if (already is not null && !run.Force && known is null && !group.Owned)
                {
                    results.Add(Task.FromResult(new ImageResult(ourId, Outcome.Skipped)));
                    continue;
                }

                await slots.WaitAsync(stop.Token);
                results.Add(RunImageAsync(new Image(ourId, ourName, url, destination, already, known), run, slots, stop));
            }
            await Task.WhenAll(results);
        }
        catch
        {
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(results);
            }
            catch (Exception)
            {
                // The others only stopped because of the failure being rethrown.
            }
            if (run.Fatal is { } fatal)
                ExceptionDispatchInfo.Throw(fatal);
            throw;
        }

        var done = results.Select(task => task.Result).ToList();
        return new ArtGroupReport(group.Label, group.Wanted.Count, byKey.Count, done.Count(result => result.Outcome == Outcome.Downloaded),
            done.Count(result => result.Outcome == Outcome.Skipped), done.Where(result => result.Problem is not null).Select(result => result.Problem!).ToList(),
            done.Where(result => result.Outcome == Outcome.Updated).Select(result => result.Id).ToList());
    }

    private async Task<ImageResult> RunImageAsync(Image image, DownloadRun run, SemaphoreSlim slots, CancellationTokenSource stop)
    {
        try
        {
            return await GetImageAsync(image, run, stop.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: null } or TimeoutException)
        {
            // The connection itself failed, not just this image: past a few in a row the rest would fail the same way.
            if (run.ConnectionFailed(ex))
            {
                await stop.CancelAsync();
                throw;
            }
            return Failed(image, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // An answer, even an error status, or a problem writing the file: this image's alone.
            if (ex is HttpRequestException)
                run.Reached();
            return Failed(image, ex);
        }
        finally
        {
            slots.Release();
        }
    }

    private static ImageResult Failed(Image image, Exception ex) =>
        new(image.Id, Outcome.Failed, $"{image.Id} ({image.Name}) -- download failed: {ex.Message}");

    private async Task<ImageResult> GetImageAsync(Image image, DownloadRun run, CancellationToken cancellationToken)
    {
        var (_, _, url, destination, already, known) = image;
        byte[] payload;
        ArtManifest.Entry entry;
        if (run.Source(url) is { } source)
        {
            if (already is not null && !run.Force && known?.Url == url && known.Sha256 == source.Entry.Sha256)
                return new ImageResult(image.Id, Outcome.Skipped);
            payload = await File.ReadAllBytesAsync(source.Path, cancellationToken);
            entry = source.Entry;
        }
        else
        {
            // A file downloaded before is only asked about: the answer is usually "not modified".
            var etag = already is not null && !run.Force && known?.Url == url ? known.ETag : null;
            var fetched = await FetchAsync(url, etag, cancellationToken);
            run.Reached();
            if (fetched.Bytes is not { } bytes)
            {
                if (already is not null)
                    run.HaveFile(url, already);
                return new ImageResult(image.Id, Outcome.Skipped);
            }
            payload = bytes;
            entry = new ArtManifest.Entry(url, fetched.ETag, ArtManifest.Hash(bytes));
        }

        if (already is not null && !run.Force && ArtManifest.Hash(await File.ReadAllBytesAsync(already, cancellationToken)) == entry.Sha256)
        {
            run.Manifest.Set(already, entry);
            run.HaveFile(url, already);
            return new ImageResult(image.Id, Outcome.Skipped);
        }

        // Via a temp file, so an interrupted download can't leave a half-written image behind.
        await AtomicFile.WriteAsync(destination, stream => stream.WriteAsync(payload, cancellationToken).AsTask());
        run.Manifest.Set(destination, entry);
        run.HaveFile(url, destination);

        // A different extension than last time would leave both on disk, and whichever the art
        // index saw first would win.
        if (already is not null && !string.Equals(already, destination, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(already);
            run.Manifest.Remove(already);
        }
        return new ImageResult(image.Id, already is null ? Outcome.Downloaded : Outcome.Updated);
    }

    /// <summary>Ask for an image, trying again after a server error or a "slow down", which are usually gone a moment later.</summary>
    private async Task<ChangedFile> FetchAsync(string url, string? etag, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _api.GetBytesIfChangedAsync(url, UserAgent, etag, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < RetryDelays.Length && ex.StatusCode is { } status
                                                  && (status == HttpStatusCode.TooManyRequests || (int)status >= 500))
            {
                await _delay(RetryDelays[attempt], cancellationToken);
            }
        }
    }

    private static string? PickImage(JsonNode record, IEnumerable<string> keys)
    {
        var images = JsonRecord.Get(record, "images") is { } nested && JsonRecord.Truthy(nested) ? nested : record;
        return keys.Select(key => JsonRecord.Get(images, key)).Where(JsonRecord.Truthy).Select(JsonRecord.Str).FirstOrDefault();
    }

    private static string? Existing(string directory, string stem) =>
        _imageSuffixes.Select(suffix => Path.Combine(directory, stem + suffix)).FirstOrDefault(File.Exists);
}
