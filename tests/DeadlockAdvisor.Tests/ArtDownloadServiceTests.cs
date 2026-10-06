using System.Text.Json.Nodes;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.GameApi;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public sealed class ArtDownloadServiceTests : IDisposable
{
    private readonly TempDirectory _assets = new();
    private readonly FakeDeadlockApi _api = new();
    private readonly ArtDownloadService _service;

    public ArtDownloadServiceTests()
    {
        _service = new ArtDownloadService(new GameApiService(_api), _api);
        _api.Json[$"{GameSync.Api}/heroes?only_active=true"] = () => JsonNode.Parse("""
            [
              {"name": "Heavy Spirit", "images": {"icon_hero_card": "https://cdn/hs_card.png", "top_bar_vertical_image": "https://cdn/hs_top.webp"}},
              {"name": "Low HP", "images": {}}
            ]
            """);
        _api.Json[$"{GameSync.Api}/items"] = () => JsonNode.Parse("""
            [
              {"name": "Spirit Resist Trinket", "type": "upgrade", "shop_image": "https://cdn/srt.png", "image": "https://cdn/srt_glyph.png"},
              {"name": "Percent Damage Item", "type": "upgrade", "image": "https://cdn/pd.jpg"},
              {"name": "Irrelevant Item", "type": "ability", "shop_image": "https://cdn/nope.png"}
            ]
            """);
        _api.Bytes["https://cdn/hs_card.png"] = [1, 2, 3];
        _api.Bytes["https://cdn/hs_top.webp"] = [4];
        _api.Bytes["https://cdn/srt.png"] = [5, 6];
    }

    public void Dispose() => _assets.Dispose();

    private string Asset(string folder, string file) => Path.Combine(_assets.Path, folder, file);

    [Fact]
    public async Task DownloadsWhatMatchesByNameAndListsTheRest()
    {
        var report = await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Asset("heroes", "heavy_spirit.png")));
        Assert.Equal([4], File.ReadAllBytes(Asset("topbar", "heavy_spirit.webp")));
        // The card again, for cutting portraits from, where the template bank doesn't look.
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Asset(Path.Combine("topbar", "_cards", "normal"), "heavy_spirit.png")));
        // The shop tile, not the white glyph.
        Assert.Equal([5, 6], File.ReadAllBytes(Asset("items", "spirit_resist_t1.png")));
        Assert.Equal(4, report.Downloaded);
        Assert.Contains("heavy_spirit (Heavy Spirit) -- matched, but has no image", report.Groups.Single(group => group.Label == "Critical cards").Unmatched);

        var heroes = report.Groups[0];
        Assert.Contains("generic (Generic)", heroes.Unmatched);
        Assert.Contains("low_hp (Low HP) -- matched, but has no image", heroes.Unmatched);
        var items = report.Groups[1];
        Assert.Contains(items.Unmatched, line => line.StartsWith("pct_dmg_t3 (Percent Damage Item) -- download failed:"));
        Assert.Contains("irrelevant_t1 (Irrelevant Item)", items.Unmatched);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_assets.Path, "items")), path => path.EndsWith(".part"));
    }

    [Fact]
    public async Task RankBadgesAreNamedAfterTheirTierAndLeaveOutTheUnranked()
    {
        _api.Json[MatchStatsService.Ranks] = () => JsonNode.Parse("""
            [
              {"tier": 0, "name": "Obscurus", "images": {"large": "https://cdn/r0.png"}},
              {"tier": 1, "name": "Initiate", "images": {"large": "https://cdn/r1.png", "large_webp": "https://cdn/r1.webp"}},
              {"tier": 11, "name": "Eternus", "images": {"large": "https://cdn/r11.png"}},
              {"tier": 5, "name": "Mystic", "images": {}}
            ]
            """);
        _api.Bytes["https://cdn/r1.png"] = [11];
        _api.Bytes["https://cdn/r11.png"] = [12];

        var report = await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);

        Assert.Equal([11], File.ReadAllBytes(Asset("ranks", "01.png")));
        Assert.Equal([12], File.ReadAllBytes(Asset("ranks", "11.png")));
        Assert.False(File.Exists(Asset("ranks", "00.png")));
        var badges = report.Groups.Single(group => group.Label == "Rank badges");
        Assert.Equal((3, 2), (badges.Wanted, badges.Downloaded));
        Assert.Equal(["05 (Mystic) -- matched, but has no image"], badges.Unmatched);
    }

    /// <summary>The badges only dress the rank pickers: without the list, the portraits and icons still come.</summary>
    [Fact]
    public async Task ARanksListThatWontLoadLeavesOnlyTheBadgesOut()
    {
        var report = await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);

        Assert.Equal(4, report.Downloaded);
        Assert.Equal((0, 0), (report.Groups.Single(group => group.Label == "Rank badges").Wanted,
            report.Groups.Single(group => group.Label == "Rank badges").Downloaded));
    }

    [Fact]
    public async Task PresentArtIsKeptUnlessForcedAndAReplacementDropsTheOldExtension()
    {
        Directory.CreateDirectory(Path.Combine(_assets.Path, "heroes"));
        File.WriteAllBytes(Asset("heroes", "heavy_spirit.jpg"), [9]);

        var kept = await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);
        Assert.Equal(1, kept.Groups[0].Skipped);
        Assert.True(File.Exists(Asset("heroes", "heavy_spirit.jpg")));
        Assert.False(File.Exists(Asset("heroes", "heavy_spirit.png")));

        await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: true, null, CancellationToken.None);
        Assert.False(File.Exists(Asset("heroes", "heavy_spirit.jpg")));
        Assert.True(File.Exists(Asset("heroes", "heavy_spirit.png")));
    }

    [Fact]
    public async Task ProgressCountsEveryWantedEntry()
    {
        var steps = new List<FetchProgress>();
        await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, new SyncProgress(steps.Add), CancellationToken.None);

        // Three heroes in five hero groups, three items, cutting portraits from the cards, and done.
        Assert.Equal(3 * 5 + 3 + 2, steps.Count);
        Assert.Equal((18, 18, "done"), (steps[^1].Done, steps[^1].Total, steps[^1].Text));
        Assert.Equal("Hero portraits: Generic", steps[0].Text);
    }

    [Fact]
    public async Task DownloadingAgainOnlyAsksWhetherAnythingChanged()
    {
        await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);
        _api.NotModified.Clear();

        var again = await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);

        Assert.Equal((0, 0), (again.Downloaded, again.Updated));
        Assert.Contains("https://cdn/hs_top.webp", _api.NotModified);
        Assert.Contains("https://cdn/srt.png", _api.NotModified);
        Assert.True(File.Exists(Path.Combine(_assets.Path, ArtManifest.FileName)));
    }

    [Fact]
    public async Task ArtTheApiChangedIsReplacedAndNamed()
    {
        await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);
        _api.Bytes["https://cdn/hs_top.webp"] = [7, 7];
        _api.Bytes["https://cdn/srt.png"] = [8];

        var again = await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);

        Assert.Equal([7, 7], File.ReadAllBytes(Asset("topbar", "heavy_spirit.webp")));
        Assert.Equal(["heavy_spirit"], again.Groups.Single(group => group.Label == "Top-bar portraits").Updated);
        // Downloaded by this app, so kept current even in a folder that can hold art put there by hand.
        Assert.Equal([8], File.ReadAllBytes(Asset("items", "spirit_resist_t1.png")));
        Assert.Equal(2, again.Updated);
    }

    [Fact]
    public async Task TopBarArtFromBeforeTheManifestIsCheckedAgainstTheApis()
    {
        Directory.CreateDirectory(Path.Combine(_assets.Path, "topbar"));
        Directory.CreateDirectory(Path.Combine(_assets.Path, "heroes"));
        File.WriteAllBytes(Asset("topbar", "heavy_spirit.webp"), [9]);
        File.WriteAllBytes(Asset("heroes", "heavy_spirit.png"), [9]);

        var report = await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);

        // The top-bar folder is the app's, so a stale copy is replaced; a portrait could be anyone's.
        Assert.Equal([4], File.ReadAllBytes(Asset("topbar", "heavy_spirit.webp")));
        Assert.Equal([9], File.ReadAllBytes(Asset("heroes", "heavy_spirit.png")));
        Assert.Equal(["heavy_spirit"], report.Groups.Single(group => group.Label == "Top-bar portraits").Updated);
        _api.NotModified.Clear();

        await _service.DownloadAsync(TestStore.Make(), _assets.Path, force: false, null, CancellationToken.None);

        Assert.Contains("https://cdn/hs_top.webp", _api.NotModified);
        Assert.Equal([9], File.ReadAllBytes(Asset("heroes", "heavy_spirit.png")));
    }

    private sealed class SyncProgress(Action<FetchProgress> report) : IProgress<FetchProgress>
    {
        public void Report(FetchProgress value) => report(value);
    }
}
