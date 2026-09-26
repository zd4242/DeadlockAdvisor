using System.Reactive.Linq;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Modals.Progress;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.GameApi;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using static DeadlockAdvisor.Tests.Support.Golden;

namespace DeadlockAdvisor.Tests;

/// <summary>The Data menu's actions end to end, over a temp data folder and a fake deadlock-api.com.</summary>
public sealed class DataMenuTests : IDisposable
{
    private sealed class NoFolderPicker : IFilePickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startIn = null) => Task.FromResult<string?>(null);
    }

    private readonly DataFixture _fixture = new();
    private readonly FakeDeadlockApi _api = new();
    private readonly ArtService _art = new(new FakeLoggingService());
    private readonly List<ViewModelBase> _shown = [];
    private readonly IDisposable _watchModals;
    private readonly DataMenuViewModel _menu;
    private int _replaced;

    public DataMenuTests()
    {
        _art.SetAssetsDir(_fixture.Data.AssetsDir);
        _watchModals = _fixture.Modals.ShowModalObservable.Subscribe(_shown.Add);
        _fixture.Data.StoreReplaced.Subscribe(_ => _replaced++);
        var gameApi = new GameApiService(_api);
        _menu = new DataMenuViewModel(_fixture.Data, gameApi, new MatchStatsService(_api), new ExcelExportService(),
            new ArtDownloadService(gameApi, _api), _art, _fixture.Modals, new NotificationService(new FakeLoggingService()),
            _fixture.Settings, new NoFolderPicker(), new FakeLoggingService());
    }

    public void Dispose()
    {
        _watchModals.Dispose();
        _menu.Dispose();
        _fixture.Dispose();
    }

    private MessageModalViewModel LastMessage() => Assert.IsType<MessageModalViewModel>(_shown[^1]);

    private void ServeTheSnapshot()
    {
        _api.Json[$"{GameSync.Api}/heroes?only_active=true"] = () => Json("game_api/heroes.json");
        _api.Json[$"{GameSync.Api}/items/by-type/upgrade"] = () => Json("game_api/shop_items.json");
    }

    [Fact]
    public async Task SyncFromGameApiReportsWhatChangedAndReloadsThePages()
    {
        ServeTheSnapshot();

        await _menu.SyncGameApiCommand.Execute();

        Assert.IsType<ProgressModalViewModel>(_shown[0]);
        var report = LastMessage();
        Assert.Equal("Synced from game API", report.Title);
        Assert.Contains("Item stats refreshed: 333 stat row(s).", report.Body);
        Assert.Contains("Long Range: Weapon Damage (conditional) none -> 40%", report.Body);
        Assert.Contains("coefficient(s) now come from item stats, via 9 line(s) in stat_rules.csv.", report.Body);
        Assert.Equal(1, _replaced);
        Assert.False(_fixture.Modals.IsModalOpen && _shown[^1] is ProgressModalViewModel);
    }

    [Fact]
    public async Task ModelHealthReportSimulatesMatchesAndComparesWithTheData()
    {
        await _menu.ModelHealthCommand.Execute();

        var report = LastMessage();
        Assert.Equal("Model health", report.Title);
        Assert.StartsWith("Simulated 2,000 random full matches", report.Body);
        Assert.Contains("Match data disagrees", report.Body);
        Assert.False(_menu.IsBusy);
    }

    [Fact]
    public async Task AFailedSyncSaysSoAndChangesNothing()
    {
        await _menu.SyncGameApiCommand.Execute();

        var report = LastMessage();
        Assert.Equal("Sync failed", report.Title);
        Assert.EndsWith("Nothing was changed.", report.Body);
        Assert.Equal(0, _replaced);
        Assert.False(_menu.IsBusy);
    }

    [Fact]
    public async Task AFailedFetchSaysSo()
    {
        await _menu.FetchMatchStatsCommand.Execute();

        var report = LastMessage();
        Assert.Equal("Fetch failed", report.Title);
        Assert.Contains("offline (test)", report.Body);
        Assert.Equal(0, _replaced);
    }

    [Fact]
    public async Task ExportWritesAReadOnlySnapshotWorkbook()
    {
        await _menu.ExportCommand.Execute();

        Assert.Equal("Exported", LastMessage().Title);
        Assert.Contains("  Heroes: 38 rows", LastMessage().Body);
        using var workbook = new XLWorkbook(_menu.ExportPath);
        Assert.Equal(["Read Me", "Categories", "Heroes", "Items", "HeroCategoryScores", "ItemFormulaCoefficients", "TraitWeights", "StatRules", "ItemStats"],
            workbook.Worksheets.Select(sheet => sheet.Name));
        var heroes = workbook.Worksheet("Heroes");
        Assert.Equal("hero_id", heroes.Cell(1, 1).GetString());
        Assert.Equal(39, heroes.LastRowUsed()!.RowNumber());
        Assert.True(heroes.Cell(2, 3).Value.IsNumber);
        Assert.Equal("THIS WORKBOOK IS READ-ONLY. Editing it changes nothing.", workbook.Worksheet("Read Me").Cell(4, 1).GetString());
    }

    [Fact]
    public async Task SyncNewDataBackfillsAndReports()
    {
        await _menu.SyncNewDataCommand.Execute();

        var report = LastMessage();
        Assert.Equal("Sync complete", report.Title);
        Assert.StartsWith("Added 0 missing hero × trait row(s), defaulted to 0.", report.Body);
        Assert.Equal(1, _replaced);
    }

    [Fact]
    public void AFirstRunWithoutArtOffersToDownloadItOnce()
    {
        _menu.OnStartup();

        var offer = Assert.IsType<ConfirmationModalViewModel>(Assert.Single(_shown));
        Assert.Equal("Download", offer.ConfirmText);
        Assert.Equal("Not now", offer.CancelText);
        Assert.True(_fixture.Settings.Current.ArtDownloadOffered);
        offer.CancelCommand!.Execute(null);

        _menu.OnStartup();
        Assert.Single(_shown);
    }

    [Fact]
    public void ThePatchCheckFlagsANewerPatchQuietly()
    {
        _fixture.Settings.Current.ArtDownloadOffered = true;
        _api.Json[MatchStatsService.Patches] = () => JsonNode.Parse("""[{"title": "10-01-2026 Gameplay Update"}]""");

        _menu.OnStartup();

        Assert.Equal("10-01", _menu.NewerPatch!.Label);
        Assert.Empty(_shown);
    }

    [Fact]
    public void AnUnreachablePatchCheckSaysNothing()
    {
        _fixture.Settings.Current.ArtDownloadOffered = true;

        _menu.OnStartup();

        Assert.Null(_menu.NewerPatch);
        Assert.Empty(_shown);
        Assert.Contains(MatchStatsService.Patches, _api.Asked);
    }
}
