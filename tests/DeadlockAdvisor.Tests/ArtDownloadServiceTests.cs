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
        // The shop tile, not the white glyph.
        Assert.Equal([5, 6], File.ReadAllBytes(Asset("items", "spirit_resist_t1.png")));
        Assert.Equal(3, report.Downloaded);

        var heroes = report.Groups[0];
        Assert.Contains("generic (Generic)", heroes.Unmatched);
        Assert.Contains("low_hp (Low HP) -- matched, but has no image", heroes.Unmatched);
        var items = report.Groups[1];
        Assert.Contains(items.Unmatched, line => line.StartsWith("pct_dmg_t3 (Percent Damage Item) -- download failed:"));
        Assert.Contains("irrelevant_t1 (Irrelevant Item)", items.Unmatched);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_assets.Path, "items")), path => path.EndsWith(".part"));
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

        Assert.Equal(3 + 3 + 3 + 1, steps.Count);
        Assert.Equal((9, 9, "done"), (steps[^1].Done, steps[^1].Total, steps[^1].Text));
        Assert.Equal("Hero portraits: Generic", steps[0].Text);
    }

    private sealed class SyncProgress(Action<FetchProgress> report) : IProgress<FetchProgress>
    {
        public void Report(FetchProgress value) => report(value);
    }
}
