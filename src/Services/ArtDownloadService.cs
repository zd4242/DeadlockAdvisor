using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
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
    /// Fetch hero portraits, item icons, and the top-bar art and hero cards detection matches
    /// against into <paramref name="assetsDir"/>, named after our ids. What's there already is kept
    /// unless the API's copy has changed since it was downloaded, or <paramref name="force"/> asks
    /// for everything again. Throws if the API's lists can't be fetched; a single failed image is
    /// reported instead.
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
public sealed class ArtDownloadService(IGameApiService gameApi, IDeadlockApi api) : IArtDownloadService
{
    public const string UserAgent = "deadlock-advisor/1.0 (asset downloader)";

    // Hero cards are 280x380 character art, cropped square for the UI; item shop images are the
    // full-colour 200x200 tiles (the plain `image` is a white glyph). Top-bar art is what the game
    // draws along the top of the screen, which is what detection matches against, and the cards
    // (normal, critical and on a streak) are what the portraits the API has no top-bar art for are
    // cut from.
    private static readonly string[] _heroImageKeys = ["icon_hero_card", "icon_image_small", "minimap_image"];
    private static readonly string[] _itemImageKeys = ["shop_image", "image"];
    private static readonly string[] _topBarImageKeys = ["top_bar_vertical_image"];
    private static readonly string[] _imageSuffixes = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <param name="Owned">
    /// Whether the app owns the folder's files: a file that differs from the API's is replaced even
    /// if this app didn't download it. Portraits and icons can be art someone put there by hand.
    /// </param>
    private sealed record Group(string Label, IReadOnlyDictionary<string, string> Wanted, JsonArray Records, string[] ImageKeys, string Directory,
        bool Owned);

    public async Task<ArtDownloadReport> DownloadAsync(DataStore store, string assetsDir, bool force, IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var heroRecords = await gameApi.FetchHeroesAsync(cancellationToken);
        var itemRecords = await gameApi.FetchUpgradesAsync(cancellationToken);
        var heroes = store.Heroes.Values.ToDictionary(hero => hero.HeroId, hero => hero.HeroName);
        var items = store.Items.Values.ToDictionary(item => item.ItemId, item => item.ItemName);
        var topbarDir = Path.Combine(assetsDir, "topbar");
        Group[] groups =
        [
            new("Hero portraits", heroes, heroRecords, _heroImageKeys, Path.Combine(assetsDir, "heroes"), Owned: false),
            new("Item icons", items, itemRecords, _itemImageKeys, Path.Combine(assetsDir, "items"), Owned: false),
            new("Top-bar portraits", heroes, heroRecords, _topBarImageKeys, topbarDir, Owned: true),
            new("Hero cards", heroes, heroRecords, ["icon_hero_card"], TopbarDerivation.CardFolder(topbarDir, PortraitState.Normal), Owned: true),
            new("Critical cards", heroes, heroRecords, ["hero_card_critical"], TopbarDerivation.CardFolder(topbarDir, PortraitState.Critical), Owned: true),
            new("On-fire cards", heroes, heroRecords, ["hero_card_gloat"], TopbarDerivation.CardFolder(topbarDir, PortraitState.Gloat), Owned: true),
        ];

        var manifest = ArtManifest.Load(assetsDir);
        var total = groups.Sum(group => group.Wanted.Count);
        var done = 0;
        var reports = new List<ArtGroupReport>();
        try
        {
            foreach (var group in groups)
            {
                reports.Add(await RunGroupAsync(group, manifest, force, text =>
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

    private async Task<ArtGroupReport> RunGroupAsync(Group group, ArtManifest manifest, bool force, Action<string> step,
        CancellationToken cancellationToken)
    {
        var byKey = new Dictionary<string, JsonNode>();
        foreach (var record in group.Records.OfType<JsonNode>())
            byKey.TryAdd(GameSync.Norm(JsonRecord.Text(record, "name")), record);
        Directory.CreateDirectory(group.Directory);

        var downloaded = 0;
        var skipped = 0;
        var updated = new List<string>();
        var unmatched = new List<string>();
        foreach (var (ourId, ourName) in group.Wanted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            step($"{group.Label}: {ourName}");
            if (!byKey.TryGetValue(GameSync.Norm(ourName), out var record) && !byKey.TryGetValue(GameSync.Norm(ourId), out record))
            {
                unmatched.Add($"{ourId} ({ourName})");
                continue;
            }
            if (PickImage(record, group.ImageKeys) is not { } url)
            {
                unmatched.Add($"{ourId} ({ourName}) -- matched, but has no image");
                continue;
            }

            var suffix = Path.GetExtension(new Uri(url).AbsolutePath);
            var destination = Path.Combine(group.Directory, ourId + (suffix.Length > 0 ? suffix : ".png"));
            var already = Existing(group.Directory, ourId);
            var known = already is null ? null : manifest.Get(already);
            if (already is not null && !force && known is null && !group.Owned)
            {
                skipped++;
                continue;
            }

            try
            {
                // A file downloaded before is only asked about: the answer is usually "not modified".
                var etag = already is not null && !force && known?.Url == url ? known.ETag : null;
                var fetched = await api.GetBytesIfChangedAsync(url, UserAgent, etag, cancellationToken);
                if (fetched.Bytes is not { } payload)
                {
                    skipped++;
                    continue;
                }
                var hash = ArtManifest.Hash(payload);
                if (already is not null && !force && ArtManifest.Hash(await File.ReadAllBytesAsync(already, cancellationToken)) == hash)
                {
                    manifest.Set(already, new ArtManifest.Entry(url, fetched.ETag, hash));
                    skipped++;
                    continue;
                }

                // Via a temp file, so an interrupted download can't leave a half-written image behind.
                await AtomicFile.WriteAsync(destination, stream => stream.WriteAsync(payload, cancellationToken).AsTask());
                manifest.Set(destination, new ArtManifest.Entry(url, fetched.ETag, hash));
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
            {
                unmatched.Add($"{ourId} ({ourName}) -- download failed: {ex.Message}");
                continue;
            }

            // A different extension than last time would leave both on disk, and whichever the art
            // index saw first would win.
            if (already is not null && !string.Equals(already, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(already);
                manifest.Remove(already);
            }
            if (already is null)
                downloaded++;
            else
                updated.Add(ourId);
        }
        return new ArtGroupReport(group.Label, group.Wanted.Count, byKey.Count, downloaded, skipped, unmatched, updated);
    }

    private static string? PickImage(JsonNode record, IEnumerable<string> keys)
    {
        var images = JsonRecord.Get(record, "images") is { } nested && JsonRecord.Truthy(nested) ? nested : record;
        return keys.Select(key => JsonRecord.Get(images, key)).Where(JsonRecord.Truthy).Select(JsonRecord.Str).FirstOrDefault();
    }

    private static string? Existing(string directory, string stem) =>
        _imageSuffixes.Select(suffix => Path.Combine(directory, stem + suffix)).FirstOrDefault(File.Exists);
}
