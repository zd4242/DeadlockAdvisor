using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.GameApi;

namespace DeadlockAdvisor.Services;

/// <param name="Unmatched">"haze (Haze) -- download failed: ...": what wasn't fetched, and why.</param>
public sealed record ArtGroupReport(string Label, int Wanted, int Offered, int Downloaded, int Skipped, IReadOnlyList<string> Unmatched);

public sealed record ArtDownloadReport(IReadOnlyList<ArtGroupReport> Groups)
{
    public int Downloaded => Groups.Sum(group => group.Downloaded);

    public List<string> Lines()
    {
        var lines = Groups
            .Select(group => $"{group.Label}: {group.Downloaded} downloaded, {group.Skipped} already present "
                             + $"({group.Wanted} wanted, {group.Offered} offered by the API)")
            .ToList();
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
    /// Fetch hero portraits, item icons and the top-bar art detection matches against into
    /// <paramref name="assetsDir"/>, named after our ids. Only what's missing unless
    /// <paramref name="force"/>. Throws if the API's lists can't be fetched; a single failed image is
    /// reported instead.
    /// </summary>
    Task<ArtDownloadReport> DownloadAsync(DataStore store, string assetsDir, bool force, IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// The Python app's download_assets.py and download_topbar_art.py. Matching is by name, not id: the
/// API's ids are Valve class names that don't line up with ours, but the display names do once
/// punctuation and case are ignored. Anything unmatched is listed rather than guessed at: a wrong
/// portrait is worse than a placeholder tile.
/// </summary>
public sealed class ArtDownloadService(IGameApiService gameApi, IDeadlockApi api) : IArtDownloadService
{
    public const string UserAgent = "deadlock-advisor/1.0 (asset downloader)";

    // Hero cards are 280x380 character art, cropped square for the UI; item shop images are the
    // full-colour 200x200 tiles (the plain `image` is a white glyph). Top-bar art is exactly what
    // the game draws along the top of the screen, which is what detection matches against.
    private static readonly string[] _heroImageKeys = ["icon_hero_card", "icon_image_small", "minimap_image"];
    private static readonly string[] _itemImageKeys = ["shop_image", "image"];
    private static readonly string[] _topBarImageKeys = ["top_bar_vertical_image"];
    private static readonly string[] _imageSuffixes = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    private sealed record Group(string Label, IReadOnlyDictionary<string, string> Wanted, JsonArray Records, string[] ImageKeys, string Directory);

    public async Task<ArtDownloadReport> DownloadAsync(DataStore store, string assetsDir, bool force, IProgress<FetchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var heroRecords = await gameApi.FetchHeroesAsync(cancellationToken);
        var itemRecords = await gameApi.FetchUpgradesAsync(cancellationToken);
        var heroes = store.Heroes.Values.ToDictionary(hero => hero.HeroId, hero => hero.HeroName);
        var items = store.Items.Values.ToDictionary(item => item.ItemId, item => item.ItemName);
        Group[] groups =
        [
            new("Hero portraits", heroes, heroRecords, _heroImageKeys, Path.Combine(assetsDir, "heroes")),
            new("Item icons", items, itemRecords, _itemImageKeys, Path.Combine(assetsDir, "items")),
            new("Top-bar portraits", heroes, heroRecords, _topBarImageKeys, Path.Combine(assetsDir, "topbar")),
        ];

        var total = groups.Sum(group => group.Wanted.Count);
        var done = 0;
        var reports = new List<ArtGroupReport>();
        foreach (var group in groups)
        {
            reports.Add(await RunGroupAsync(group, force, text =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new FetchProgress(done++, total, text));
            }, cancellationToken));
        }
        progress?.Report(new FetchProgress(total, total, "done"));
        return new ArtDownloadReport(reports);
    }

    private async Task<ArtGroupReport> RunGroupAsync(Group group, bool force, Action<string> step, CancellationToken cancellationToken)
    {
        var byKey = new Dictionary<string, JsonNode>();
        foreach (var record in group.Records.OfType<JsonNode>())
            byKey.TryAdd(GameSync.Norm(PyJson.Text(record, "name")), record);
        Directory.CreateDirectory(group.Directory);

        var downloaded = 0;
        var skipped = 0;
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
            if (already is not null && !force)
            {
                skipped++;
                continue;
            }

            try
            {
                // Via a temp file, so an interrupted download can't leave a half-written image behind.
                var payload = await api.GetBytesAsync(url, UserAgent, cancellationToken);
                var partial = destination + ".part";
                await File.WriteAllBytesAsync(partial, payload, cancellationToken);
                File.Move(partial, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
            {
                unmatched.Add($"{ourId} ({ourName}) -- download failed: {ex.Message}");
                continue;
            }

            // A different extension than last time would leave both on disk, and whichever the art
            // index saw first would win.
            if (already is not null && !string.Equals(already, destination, StringComparison.OrdinalIgnoreCase))
                File.Delete(already);
            downloaded++;
        }
        return new ArtGroupReport(group.Label, group.Wanted.Count, byKey.Count, downloaded, skipped, unmatched);
    }

    private static string? PickImage(JsonNode record, IEnumerable<string> keys)
    {
        var images = PyJson.Get(record, "images") is { } nested && PyJson.Truthy(nested) ? nested : record;
        return keys.Select(key => PyJson.Get(images, key)).Where(PyJson.Truthy).Select(PyJson.Str).FirstOrDefault();
    }

    private static string? Existing(string directory, string stem) =>
        _imageSuffixes.Select(suffix => Path.Combine(directory, stem + suffix)).FirstOrDefault(File.Exists);
}
