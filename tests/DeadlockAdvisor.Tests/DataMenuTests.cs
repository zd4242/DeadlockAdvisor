using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.MainWindow.MatchDownload;
using DeadlockAdvisor.Features.MainWindow.ModelUpdate;
using DeadlockAdvisor.Features.MainWindow.Welcome;
using DeadlockAdvisor.Features.Shared.BackgroundJobs;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Modals.Progress;
using DeadlockAdvisor.Scoring;
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
    private readonly FakeConnectivity _connectivity = new();
    private readonly ArtService _art = new(new FakeLoggingService());
    private readonly List<ViewModelBase> _shown = [];
    private readonly NotificationService _notifications = new(new FakeLoggingService());
    private readonly List<Notification> _toasts = [];
    private readonly IDisposable _watchModals;
    private readonly DataMenuViewModel _menu;
    private int _replaced;

    public DataMenuTests()
    {
        _art.SetAssetsDir(_fixture.Data.AssetsDir);
        _watchModals = _fixture.Modals.ShowModalObservable.Subscribe(_shown.Add);
        _fixture.Data.StoreReplaced.Subscribe(_ => _replaced++);
        _notifications.Notifications.Subscribe(_toasts.Add);
        _menu = Menu();
    }

    private DataMenuViewModel Menu(IMatchStatsService? matchStats = null, IArtDownloadService? artDownload = null,
        IMatchSnapshotService? snapshots = null)
    {
        var gameApi = new GameApiService(_api);
        return new DataMenuViewModel(_fixture.Data, gameApi, matchStats ?? new MatchStatsService(_api),
            snapshots ?? new MatchSnapshotService(_api), new ModelUpdateService(_api), new ExcelExportService(),
            artDownload ?? new ArtDownloadService(gameApi, _api), _art, _fixture.Modals, _notifications,
            _fixture.Settings, new NoFolderPicker(), new FakeLoggingService(), _connectivity, _fixture.Clock);
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
        Assert.Contains("Item stats refreshed: 503 stat row(s).", report.Body);
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
    public async Task ModelHealthReadsACopyOfTheSavedDataSoAnEditWhileItRunsDoesntReachIt()
    {
        var store = _fixture.Data.Store;
        var expected = Report(store);

        var running = _menu.ModelHealthCommand.Execute().ToTask();
        Rate(store, 10);
        _fixture.Data.MarkEdited(DataFiles.HeroScores);
        await running;

        Assert.Equal(expected, LastMessage().Body);
        Assert.NotEqual(expected, Report(store));
    }

    [Fact]
    public async Task ModelHealthIncludesEditsStillWaitingToBeSaved()
    {
        var store = _fixture.Data.Store;
        Rate(store, 10);
        _fixture.Data.MarkEdited(DataFiles.HeroScores);

        await _menu.ModelHealthCommand.Execute();

        Assert.Equal(Report(store), LastMessage().Body);
    }

    private static void Rate(DataStore store, int score)
    {
        foreach (var heroId in store.Heroes.Keys)
            foreach (var categoryId in store.Categories.Keys)
                store.SetHeroScore(heroId, categoryId, score);
    }

    private static string Report(DataStore store) =>
        string.Join("\n", ModelHealth.Build(store, ItemScoring.BuildWeightMatrix(store)).Lines());

    [Fact]
    public async Task ModelHealthSaysSoWhenTheDataCantBeRead()
    {
        File.WriteAllText(Path.Combine(_fixture.Data.DataDir, DataStore.HeroesFile), "hero_id,hero_name,game_id\n");

        await _menu.ModelHealthCommand.Execute();

        var message = LastMessage();
        Assert.Equal("Model health", message.Title);
        Assert.StartsWith("Couldn't read the data to check it:", message.Body);
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
    public async Task AnUnreachablePatchListSaysSoAndStartsNothing()
    {
        await _menu.DownloadMatchDataCommand.Execute();

        Assert.Empty(_shown);
        Assert.Empty(_menu.Jobs);
        Assert.False(_menu.IsDownloadingMatchData);
        Assert.StartsWith("Couldn't reach deadlock-api.com for the patch list:", Assert.Single(_toasts).Message);
    }

    [Fact]
    public async Task OfflineThePatchListSaysYoureOffline()
    {
        _connectivity.GoOffline();

        await _menu.DownloadMatchDataCommand.Execute();

        Assert.Empty(_shown);
        Assert.Equal("You're offline. The patch list comes from deadlock-api.com, so the match data waits for a connection.",
            Assert.Single(_toasts).Message);
    }

    [Fact]
    public async Task AFailedDownloadStaysInTheStatusBarWithWhatWentWrong()
    {
        await _menu.DownloadMatchDataAsync(HeldMatchStats.Plan);

        Assert.Empty(_shown);
        var job = Assert.Single(_menu.Jobs);
        Assert.True(job.HasFailed);
        Assert.False(_menu.IsDownloadingMatchData);
        Assert.Equal(0, _replaced);

        await job.OpenCommand.Execute();
        var report = LastMessage();
        Assert.Equal("Match data download failed", report.Title);
        Assert.Contains("offline (test)", report.Body);
        Assert.EndsWith("Nothing was changed.", report.Body);
        Assert.Empty(_menu.Jobs);
    }

    [Fact]
    public async Task TheDialogStartsTheChosenPlanAndRemembersTheChoice()
    {
        var matchStats = new HeldMatchStats();
        using var menu = Menu(matchStats: matchStats);
        await menu.DownloadMatchDataCommand.Execute();

        var dialog = Assert.IsType<MatchDownloadViewModel>(_shown[^1]);
        Assert.Equal([false, true], matchStats.Planned);
        Assert.False(dialog.IncludeRanks);
        dialog.IncludeRanks = true;
        Assert.True(dialog.KeepUpToDate);
        dialog.KeepUpToDate = false;
        await dialog.DownloadCommand.Execute();

        Assert.False(_fixture.Modals.IsModalOpen);
        Assert.True(_fixture.Settings.Current.MatchDataIncludeRanks);
        Assert.False(_fixture.Settings.Current.AutoUpdateMatchData);
        var details = Assert.IsType<MatchDownloadProgressViewModel>(Assert.Single(menu.Jobs).Details);
        Assert.Equal(["09-29 · every match", "09-29 · rank groups"], details.Phases.Select(phase => phase.Text));

        // The next time it starts on the rank groups.
        using var again = Menu(matchStats: new HeldMatchStats());
        await again.DownloadMatchDataCommand.Execute();
        Assert.True(Assert.IsType<MatchDownloadViewModel>(_shown[^1]).IncludeRanks);
    }

    [Fact]
    public async Task MatchDataDownloadsInTheBackgroundWithItsPhasesBehindTheChipAndAsksBeforeStopping()
    {
        var matchStats = new HeldMatchStats();
        using var menu = Menu(matchStats: matchStats);
        var run = menu.DownloadMatchDataAsync(HeldMatchStats.Plan);

        var job = Assert.Single(menu.Jobs);
        var details = Assert.IsType<MatchDownloadProgressViewModel>(job.Details);
        Assert.Empty(_shown);
        Assert.True(menu.IsDownloadingMatchData);
        Assert.False(await menu.DownloadMatchDataCommand.CanExecute.FirstAsync());
        Assert.False(await menu.ChangeDataFolderCommand.CanExecute.FirstAsync());
        Assert.Equal(("0 of 488 calls", "83 calls"), (details.CallsText, details.Phases[0].Detail));

        // The chip and the card follow the same reports.
        matchStats.Progress!.Report(new MatchFetchProgress(1, 100, 405, 183, 488, "09-29 · Phantom – Eternus · Enemies: Haze", 2_500_000,
            new FetchWait("deadlock-api.com asked to slow down", TimeSpan.FromSeconds(30))));
        Assert.Equal((PhaseState.Done, PhaseState.Running), (details.Phases[0].State, details.Phases[1].State));
        Assert.Equal(("in use", "Enemies: Haze", 100), (details.Phases[0].Detail, details.Phases[1].Detail, details.Phases[1].Done));
        Assert.Equal(("183 of 488 calls", "2.5 MB received"), (details.CallsText, details.BytesText));
        Assert.Equal("deadlock-api.com asked to slow down: waiting 30 s", details.WaitText);
        Assert.Equal((183, 488), (job.Done, job.Total));

        await job.CancelCommand.Execute();
        var ask = Assert.IsType<ConfirmationModalViewModel>(_shown[^1]);
        Assert.Equal(("Stop", "Keep going"), (ask.ConfirmText, ask.CancelText));
        ask.CancelCommand!.Execute(null);
        Assert.False(job.Token.IsCancellationRequested);

        await job.CancelCommand.Execute();
        Assert.IsType<ConfirmationModalViewModel>(_shown[^1]).ConfirmCommand!.Execute(null);
        await run;

        Assert.Empty(menu.Jobs);
        Assert.False(menu.IsDownloadingMatchData);
        Assert.True(await menu.ChangeDataFolderCommand.CanExecute.FirstAsync());
        Assert.Equal(0, _replaced);
    }
    [Fact]
    public async Task ArtDownloadsInTheBackgroundAndLeavesItsReportInTheStatusBar()
    {
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);
        var artShown = 0;
        using var watchArt = menu.ViewInteraction.Where(action => action == DataMenuViewModel.ArtChangedAction).Subscribe(_ => artShown++);

        var run = menu.DownloadArtAsync(force: false);
        var job = Assert.Single(menu.Jobs);
        Assert.Equal("Art", job.Title);
        Assert.True(menu.IsDownloadingArt);
        Assert.False(await menu.DownloadArtCommand.CanExecute.FirstAsync());
        Assert.Empty(_shown);

        download.Progress!.Report(new FetchProgress(0, 10, "Hero portraits: Abrams"));
        _fixture.Clock.AdvanceBy(TimeSpan.FromSeconds(5));
        download.Progress.Report(new FetchProgress(5, 10, "Item icons: Headshot Booster"));
        Assert.Equal("50% · under a minute left", job.StatusText);
        // Art that has arrived shows without waiting for the rest.
        Assert.Equal(1, artShown);

        download.Finish(new ArtDownloadReport([new ArtGroupReport("Hero portraits", 2, 2, 2, 0, [], [])]));
        await run;

        Assert.False(menu.IsDownloadingArt);
        Assert.Equal(BackgroundJobState.Succeeded, job.State);
        Assert.Equal("2 downloaded", job.StatusText);
        Assert.Empty(_shown);
        await job.OpenCommand.Execute();
        Assert.Equal("Art downloaded", LastMessage().Title);
        Assert.Empty(menu.Jobs);
    }

    [Fact]
    public async Task CancellingTheArtDownloadTakesItOutOfTheStatusBar()
    {
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);
        var run = menu.DownloadArtAsync(force: false);

        await Assert.Single(menu.Jobs).CancelCommand.Execute();
        await run;

        Assert.Empty(menu.Jobs);
        Assert.Empty(_shown);
        Assert.False(menu.IsDownloadingArt);
    }

    [Fact]
    public async Task ANewRunReplacesTheLastOnesUnreadReport()
    {
        await _menu.DownloadMatchDataAsync(HeldMatchStats.Plan);
        var first = Assert.Single(_menu.Jobs);

        await _menu.DownloadMatchDataAsync(HeldMatchStats.Plan);

        Assert.NotSame(first, Assert.Single(_menu.Jobs));
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
    public async Task SyncNewDataReports()
    {
        await _menu.SyncNewDataCommand.Execute();

        var report = LastMessage();
        Assert.Equal("Sync complete", report.Title);
        Assert.StartsWith("No row points at an id that no longer exists.", report.Body);
        Assert.Equal(1, _replaced);
    }

    [Fact]
    public async Task SyncNewDataKeepsEveryRuleWhenABaseTableIsEmpty()
    {
        var store = _fixture.Data.Store;
        var rules = store.ItemCoefficients.Count;
        store.Items.Clear();

        await _menu.SyncNewDataCommand.Execute();

        Assert.Equal(rules, store.ItemCoefficients.Count);
        Assert.Contains("Nothing was dropped: items.csv has no rows", LastMessage().Body);
        Assert.DoesNotContain("Dropped", LastMessage().Body);
    }

    [Fact]
    public async Task AFirstRunWithoutArtOffersArtAndMatchDataOnce()
    {
        var art = new HeldArtDownload();
        using var menu = Menu(matchStats: new HeldMatchStats(), artDownload: art);

        menu.OnStartup();

        var welcome = Assert.IsType<WelcomeViewModel>(Assert.Single(_shown));
        Assert.True(_fixture.Settings.Current.WelcomeOffered);
        Assert.Equal((true, true, true, false, true),
            (welcome.CanDownloadMatchData, welcome.Art, welcome.MatchData, welcome.Ranks, welcome.KeepUpToDate));
        Assert.Equal(("about 35 s · 1.3 MB", "about 3 min more · 6.2 MB"), (welcome.MatchDataDetail, welcome.RanksDetail));
        welcome.Ranks = true;
        welcome.KeepUpToDate = false;
        await welcome.StartCommand.Execute();

        Assert.Equal(1, art.Started);
        var job = Assert.Single(menu.Jobs, job => job.Title == "Match data");
        Assert.Equal(2, Assert.IsType<MatchDownloadProgressViewModel>(job.Details).Phases.Count);
        Assert.Equal((true, false), (_fixture.Settings.Current.MatchDataIncludeRanks, _fixture.Settings.Current.AutoUpdateMatchData));

        using var again = Menu(matchStats: new HeldMatchStats());
        again.OnStartup();
        Assert.Single(_shown);
        menu.CancelJobs();
    }

    [Fact]
    public async Task WithoutAMatchDataPlanTheWelcomeOffersTheArtAlone()
    {
        _menu.OnStartup();

        var welcome = Assert.IsType<WelcomeViewModel>(Assert.Single(_shown));
        Assert.False(welcome.CanDownloadMatchData);
        Assert.False(welcome.MatchData);
        welcome.MatchData = false;
        welcome.Art = false;
        Assert.False(await welcome.StartCommand.CanExecute.FirstAsync());
    }

    [Fact]
    public void AFirstRunWithNoConnectionWaitsRatherThanOfferingWhatCannotBeDownloaded()
    {
        _connectivity.GoOffline();

        _menu.OnStartup();

        Assert.Empty(_shown);
        Assert.Empty(_menu.Jobs);
        Assert.False(_fixture.Settings.Current.WelcomeOffered);
        Assert.DoesNotContain(MatchStatsService.Patches, _api.Asked);
    }

    [Fact]
    public async Task WhenTheConnectionReturnsTheFirstRunOfferComesAsAChipThatOpensTheDialog()
    {
        _connectivity.GoOffline();
        _menu.OnStartup();

        _connectivity.Reconnect();
        _menu.OnReconnected();

        var chip = Assert.Single(_menu.Jobs, job => job.Title == DataMenuViewModel.WelcomeChipTitle);
        Assert.Empty(_shown);
        Assert.False(_fixture.Settings.Current.WelcomeOffered);

        await chip.OpenCommand.Execute();

        Assert.IsType<WelcomeViewModel>(Assert.Single(_shown));
        Assert.True(_fixture.Settings.Current.WelcomeOffered);
        Assert.DoesNotContain(_menu.Jobs, job => job.Title == DataMenuViewModel.WelcomeChipTitle);
    }

    [Fact]
    public void AFirstRunOfferThatFindsAnotherDialogOpenBecomesAChipInsteadOfBeingDropped()
    {
        _fixture.Modals.ShowMessage("Formula update", "Something to read first.");

        _menu.OnStartup();

        Assert.Contains(_menu.Jobs, job => job.Title == DataMenuViewModel.WelcomeChipTitle);
        Assert.False(_fixture.Settings.Current.WelcomeOffered);
    }

    [Fact]
    public void ReconnectingMakesTheStartupChecksAgain()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddDays(-30), now);
        _fixture.Settings.Current.AutoUpdateModel = true;
        _api.Asked.Clear();

        menu.OnReconnected();

        Assert.Contains(ModelManifest.ManifestUrl, _api.Asked);
        Assert.Contains(MatchSnapshot.ManifestUrl, _api.Asked);
        Assert.Empty(menu.Jobs);
    }

    private static readonly List<Patch> _patches = MatchStatsMath.ParsePatches(["09-29-2026", "09-16-2026 Update"]);

    /// <summary>Match data from both patches, the current one fetched <paramref name="fetched"/>; the service's clock reads <paramref name="now"/>.</summary>
    private DataMenuViewModel MenuWithData(DateTimeOffset fetched, DateTimeOffset now)
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        WithoutModelChecks();
        _api.Json[MatchStatsService.Patches] = () => JsonNode.Parse("""[{"title": "09-29-2026"}, {"title": "09-16-2026 Update"}]""");
        var store = _fixture.Data.Store;
        store.PutMatchSegment(new MatchSegment(_patches[0], _patches[0].Start, fetched.ToUnixTimeSeconds(), false, fetched.ToUnixTimeSeconds(),
            SliceCounts.Empty, [], []));
        store.PutMatchSegment(new MatchSegment(_patches[1], _patches[1].Start, _patches[0].Start - 1, true, fetched.ToUnixTimeSeconds() + 86400 * 7,
            SliceCounts.Empty, [], []));
        return Menu(matchStats: new MatchStatsService(_api, () => now, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void FreshMatchDataIsLeftAlone()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddHours(-2), now);

        menu.OnStartup();

        Assert.Empty(menu.Jobs);
        // The shared download can't be had here, so it's deadlock-api.com's patch list.
        Assert.Equal([MatchSnapshot.ManifestUrl, MatchStatsService.Patches], _api.Asked);
    }

    [Fact]
    public void StaleMatchDataIsRefreshedQuietlyAndAFailureSaysNothing()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddDays(-4), now);

        menu.OnStartup();

        Assert.Contains(_api.Asked, url => url.Contains("/item-stats?"));
        Assert.Empty(menu.Jobs);
        Assert.Empty(_shown);
        Assert.False(menu.IsDownloadingMatchData);
    }

    [Fact]
    public void ANewPatchIsDownloadedQuietly()
    {
        using var menu = Menu(matchStats: new HeldMatchStats());
        _fixture.Settings.Current.WelcomeOffered = true;
        _fixture.Data.Store.PutMatchSegment(new MatchSegment(_patches[1], _patches[1].Start, _patches[0].Start - 1, true, _patches[0].Start,
            SliceCounts.Empty, [], []));

        menu.OnStartup();

        Assert.Equal("09-29", menu.NewerPatch!.Label);
        Assert.True(Assert.Single(menu.Jobs).IsRunning);
        Assert.Empty(_shown);
        // The test waits for the startup check, which waits for the download.
        menu.CancelJobs();
    }

    [Fact]
    public void WithUpdatesOffMatchDataIsOnlyChecked()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddDays(-4), now);
        _fixture.Settings.Current.AutoUpdateMatchData = false;

        menu.OnStartup();

        Assert.Empty(menu.Jobs);
        Assert.DoesNotContain(_api.Asked, url => url.Contains("/item-stats?"));
    }

    private string TopbarDir => Path.Combine(_fixture.Data.AssetsDir, "topbar");

    /// <summary>An install with top-bar art, as the art download leaves it.</summary>
    private void HaveTopbarArt(bool derivedByThisVersion)
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        Directory.CreateDirectory(TopbarDir);
        File.WriteAllBytes(Path.Combine(TopbarDir, "haze.png"), [1]);
        if (derivedByThisVersion)
            Vision.TopbarDerivation.Run(TopbarDir, []);
    }

    [Fact]
    public async Task TopBarArtIsCheckedQuietlyWhenItsDue()
    {
        HaveTopbarArt(derivedByThisVersion: true);
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);

        menu.OnStartup();
        Assert.Equal(1, download.Started);
        download.Finish(new ArtDownloadReport([new ArtGroupReport("Top-bar portraits", 1, 1, 0, 1, [], [])], new([], [])));
        await Task.Yield();

        // Nothing changed, so there's nothing to say.
        Assert.Empty(menu.Jobs);
        Assert.Empty(_shown);
        Assert.Equal(_fixture.Clock.Now, _fixture.Settings.Current.ArtCheckedAt);

        using var again = Menu(artDownload: download);
        again.OnStartup();
        Assert.Equal(1, download.Started);
    }

    [Fact]
    public void PortraitsCutByAnOlderVersionAreCutAgainAtOnce()
    {
        HaveTopbarArt(derivedByThisVersion: false);
        _fixture.Settings.Current.ArtCheckedAt = _fixture.Clock.Now;
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);

        menu.OnStartup();

        Assert.Equal(1, download.Started);
        download.Finish(new ArtDownloadReport([], new([], [])));
    }

    [Fact]
    public async Task AQuietCheckThatChangedSomethingSaysSo()
    {
        HaveTopbarArt(derivedByThisVersion: true);
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);

        menu.OnStartup();
        download.Finish(new ArtDownloadReport([new ArtGroupReport("Hero cards", 1, 1, 0, 0, [], ["haze"])], new(["haze"], [])));
        await Task.Yield();

        Assert.Equal(BackgroundJobState.Succeeded, Assert.Single(menu.Jobs).State);
    }

    /// <summary>Portraits for every hero but <paramref name="except"/>: art downloaded before those heroes were added.</summary>
    private void HavePortraits(params string[] except)
    {
        var folder = _art.FolderOf(ArtKind.Hero);
        Directory.CreateDirectory(folder);
        foreach (var heroId in _fixture.Data.Store.Heroes.Keys.Except(except))
            File.WriteAllBytes(Path.Combine(folder, heroId + ".png"), [1]);
        _art.Refresh();
    }

    [Fact]
    public void AModelUpdateThatBringsAHeroFetchesItsArtAtOnce()
    {
        HaveTopbarArt(derivedByThisVersion: true);
        HavePortraits();
        _fixture.Settings.Current.ArtCheckedAt = _fixture.Clock.Now;
        var heroes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(DataBytes(DataStore.HeroesFile)).TrimEnd() + "\r\nnewcomer,Newcomer,9001\r\n");
        PublishModel((DataStore.HeroesFile, heroes));
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);

        menu.OnStartup();

        Assert.Equal(heroes, DataBytes(DataStore.HeroesFile));
        Assert.Equal(1, download.Started);
        download.Finish(new ArtDownloadReport([], new([], [])));
    }

    [Fact]
    public void AModelUpdateWithoutNewHeroesLeavesTheArtAlone()
    {
        HaveTopbarArt(derivedByThisVersion: true);
        HavePortraits();
        _fixture.Settings.Current.ArtCheckedAt = _fixture.Clock.Now;
        PublishModel((DataStore.TraitWeightsFile, ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.TraitWeightsFile), "1.3")));
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);

        menu.OnStartup();

        Assert.Equal(0, download.Started);
    }

    [Fact]
    public void AHeroWithoutArtIsAskedAboutADayAfterTheLastCheckNotAWeek()
    {
        HaveTopbarArt(derivedByThisVersion: true);
        HavePortraits(except: _fixture.Data.Store.Heroes.Keys.First());
        _fixture.Clock.AdvanceBy(TimeSpan.FromDays(30));
        var download = new HeldArtDownload();

        _fixture.Settings.Current.ArtCheckedAt = _fixture.Clock.Now.AddHours(-12);
        using var soon = Menu(artDownload: download);
        soon.OnStartup();
        Assert.Equal(0, download.Started);

        _fixture.Settings.Current.ArtCheckedAt = _fixture.Clock.Now.AddDays(-2);
        using var later = Menu(artDownload: download);
        later.OnStartup();
        Assert.Equal(1, download.Started);
        download.Finish(new ArtDownloadReport([], new([], [])));
    }

    [Fact]
    public void WithoutAnyPortraitsADownloadOfArtIsntStartedForAMissingHero()
    {
        HaveTopbarArt(derivedByThisVersion: true);
        _fixture.Clock.AdvanceBy(TimeSpan.FromDays(30));
        _fixture.Settings.Current.ArtCheckedAt = _fixture.Clock.Now.AddDays(-2);
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);

        menu.OnStartup();

        Assert.Equal(0, download.Started);
    }

    [Fact]
    public void ThePatchCheckFlagsANewerPatchQuietly()
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        _api.Json[MatchStatsService.Patches] = () => JsonNode.Parse("""[{"title": "10-01-2026 Gameplay Update"}]""");

        _menu.OnStartup();

        Assert.Equal("10-01", _menu.NewerPatch!.Label);
        Assert.Empty(_shown);
    }

    [Fact]
    public void ThePatchCheckCanBeTurnedOff()
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        _fixture.Settings.Current.CheckForNewerPatch = false;
        _api.Json[MatchStatsService.Patches] = () => JsonNode.Parse("""[{"title": "10-01-2026 Gameplay Update"}]""");

        _menu.OnStartup();

        Assert.Null(_menu.NewerPatch);
        Assert.DoesNotContain(MatchStatsService.Patches, _api.Asked);
    }

    [Fact]
    public void AnUnreachablePatchCheckSaysNothing()
    {
        _fixture.Settings.Current.WelcomeOffered = true;

        _menu.OnStartup();

        Assert.Null(_menu.NewerPatch);
        Assert.Empty(_shown);
        Assert.Contains(MatchStatsService.Patches, _api.Asked);
    }

    // -- the shared download --------------------------------------------------------

    /// <summary>Both of the synthetic API's patches with their rank groups, as a download brings them back.</summary>
    private static readonly Lazy<Task<IReadOnlyList<MatchSegment>>> _sharedSegments = new(async () =>
    {
        using var data = CopyData();
        var store = DataStore.Load(data.Path);
        await SyntheticItemStatsApi.DownloadAsync(store);
        return store.MatchSegments;
    });

    /// <summary>Those patches, the current one fetched <paramref name="fetched"/>.</summary>
    private static async Task<List<MatchSegment>> SegmentsAsync(DateTimeOffset fetched) =>
        (await _sharedSegments.Value)
        .Select(segment => segment.Ended ? segment : segment with { Until = fetched.ToUnixTimeSeconds(), FetchedAt = fetched.ToUnixTimeSeconds() })
        .ToList();

    /// <summary>The shared download serving those patches, last brought up to date <paramref name="checkedAt"/>.</summary>
    private async Task<MatchSnapshot> ServeSnapshotAsync(DateTimeOffset fetched, DateTimeOffset checkedAt)
    {
        var entries = new List<SnapshotPatch>();
        foreach (var segment in await SegmentsAsync(fetched))
        {
            var (entry, gzipped) = MatchSnapshot.Pack(segment);
            _api.Bytes[MatchSnapshot.UrlOf(entry)] = gzipped;
            entries.Add(entry);
        }
        var snapshot = new MatchSnapshot(checkedAt.ToUnixTimeSeconds(), entries);
        _api.Bytes[MatchSnapshot.ManifestUrl] = snapshot.ToJsonBytes();
        return snapshot;
    }

    /// <summary>A download started in the background has its heavy steps on other threads, so what it did shows up a moment later.</summary>
    private static async Task MatchDataDownloadEndsAsync(DataMenuViewModel menu)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (menu.IsDownloadingMatchData)
            await Task.Delay(5, patience.Token);
    }

    private DataMenuViewModel SharedMenu(DateTimeOffset now)
    {
        WithoutModelChecks();
        return Menu(snapshots: new MatchSnapshotService(_api, () => now));
    }

    /// <summary>For the tests of what startup asks deadlock-api.com for: nothing about the published model.</summary>
    private void WithoutModelChecks()
    {
        _fixture.Settings.Current.AutoUpdateModel = false;
        _fixture.Settings.Current.CheckForNewHeroes = false;
    }

    [Fact]
    public async Task TheSharedDownloadIsOfferedWhenItsThereAndBringsEveryPatchWithItsRankGroups()
    {
        var now = SyntheticItemStatsApi.Now;
        await ServeSnapshotAsync(now.AddHours(-1), now.AddHours(-1));
        using var menu = SharedMenu(now);

        await menu.DownloadMatchDataCommand.Execute();

        var dialog = Assert.IsType<MatchDownloadViewModel>(_shown[^1]);
        Assert.True(dialog.IsShared);
        Assert.Equal(["New: downloading it, with rank groups", "New: downloading it, with rank groups"], dialog.Steps.Select(step => step.Text));
        Assert.StartsWith("a few seconds · ", dialog.SharedDetail);
        Assert.Contains("last fetched from deadlock-api.com 1h ago", dialog.Intro);
        await dialog.DownloadCommand.Execute();
        await MatchDataDownloadEndsAsync(menu);

        var store = _fixture.Data.Store;
        Assert.Equal(2, store.MatchSegments.Count);
        Assert.All(store.MatchSegments, segment => Assert.True(segment.HasRanks));
        Assert.NotEmpty(store.MatchLift);
        Assert.False(Assert.Single(menu.Jobs).HasFailed);
        Assert.Null(Assert.Single(menu.Jobs).Details);
        Assert.DoesNotContain(_api.Asked, url => url.StartsWith("https://api.deadlock-api.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckNowTakesWhatIsNewerFromTheSharedDownloadWithoutAskingAndThenSaysItIsCurrent()
    {
        var now = SyntheticItemStatsApi.Now;
        await ServeSnapshotAsync(now.AddHours(-1), now.AddHours(-1));
        _fixture.Settings.Current.AutoUpdateMatchData = false;
        _fixture.Settings.Current.CheckForNewerPatch = false;
        using var menu = SharedMenu(now);

        await menu.CheckMatchDataCommand.Execute();
        await MatchDataDownloadEndsAsync(menu);

        Assert.Empty(_shown);
        Assert.Equal(2, _fixture.Data.Store.MatchSegments.Count);
        Assert.Equal("Downloading match data from the shared download…", _toasts[0].Message);
        Assert.NotNull(_fixture.Settings.Current.MatchDataCheckedAt);

        _toasts.Clear();
        _api.Asked.Clear();
        await menu.CheckMatchDataCommand.Execute();

        Assert.Empty(_shown);
        Assert.Equal([MatchSnapshot.ManifestUrl], _api.Asked);
        var toast = Assert.Single(_toasts);
        Assert.Matches(@"^Match data is up to date \(patch 09-29, fetched (just now|\d+[hd] ago)\)\.$", toast.Message);
        Assert.False(menu.IsDownloadingMatchData);
    }

    [Fact]
    public async Task CheckNowOpensTheDialogOnlyWhenThereIsNoSharedDownload()
    {
        using var menu = Menu(matchStats: new HeldMatchStats());

        await menu.CheckMatchDataCommand.Execute();

        Assert.IsType<MatchDownloadViewModel>(_shown[^1]);
        Assert.False(menu.IsDownloadingMatchData);
    }

    [Fact]
    public async Task UpdatesComeFromTheSharedDownloadOnceTheCurrentPatchIsDue()
    {
        var now = SyntheticItemStatsApi.Now;
        var snapshot = await ServeSnapshotAsync(now.AddHours(-1), now.AddHours(-1));
        _fixture.Settings.Current.WelcomeOffered = true;
        var store = _fixture.Data.Store;
        foreach (var segment in await SegmentsAsync(now - MatchSnapshot.RefreshAfter - TimeSpan.FromHours(1)))
            store.PutMatchSegment(segment);
        using var menu = SharedMenu(now);

        menu.OnStartup();
        await MatchDataDownloadEndsAsync(menu);

        // The patch before is complete, so only the current one is fetched.
        Assert.Equal([MatchSnapshot.ManifestUrl, MatchSnapshot.UrlOf(snapshot.Patches[0])], _api.Asked);
        Assert.Equal(snapshot.Patches[0].FetchedAt, store.MatchSegments[0].FetchedAt);
        Assert.Empty(_shown);
    }

    [Fact]
    public async Task FreshDataIsLeftAloneAndAStaleSharedDownloadFallsBackToTheApi()
    {
        var now = SyntheticItemStatsApi.Now;
        await ServeSnapshotAsync(now.AddHours(-1), now.AddHours(-1));
        _fixture.Settings.Current.WelcomeOffered = true;
        foreach (var segment in await SegmentsAsync(now.AddHours(-2)))
            _fixture.Data.Store.PutMatchSegment(segment);
        using var menu = SharedMenu(now);

        menu.OnStartup();

        Assert.Equal([MatchSnapshot.ManifestUrl], _api.Asked);
        Assert.Empty(menu.Jobs);

        // A snapshot the job hasn't touched in days has stopped: deadlock-api.com is asked instead.
        _api.Asked.Clear();
        await ServeSnapshotAsync(now - MatchSnapshot.StaleAfter, now - MatchSnapshot.StaleAfter);
        using var later = SharedMenu(now);
        later.OnStartup();

        Assert.Equal([MatchSnapshot.ManifestUrl, MatchStatsService.Patches], _api.Asked);
    }

    [Fact]
    public async Task ADamagedSharedFileFailsAndLeavesTheDataAsItWas()
    {
        var now = SyntheticItemStatsApi.Now;
        var snapshot = await ServeSnapshotAsync(now.AddHours(-1), now.AddHours(-1));
        _api.Bytes[MatchSnapshot.UrlOf(snapshot.Patches[0])] = [1, 2, 3];
        using var menu = SharedMenu(now);
        var plan = await new MatchSnapshotService(_api, () => now).PlanAsync(_fixture.Data.Store);

        await menu.DownloadMatchDataAsync(plan!);

        var job = Assert.Single(menu.Jobs);
        Assert.True(job.HasFailed);
        Assert.Empty(_fixture.Data.Store.MatchSegments);
        await job.OpenCommand.Execute();
        Assert.Contains("Couldn't download match data from the shared download:", LastMessage().Body);
        Assert.Contains("didn't arrive intact", LastMessage().Body);
    }

    // -- formula updates --------------------------------------------------------------

    /// <summary>
    /// The fixture's data recorded as the installed model, and a newer one published with
    /// <paramref name="changed"/>; the fake API serves both.
    /// </summary>
    private ModelManifest PublishModel(params (string File, byte[] Bytes)[] changed) => PublishModelWithNotes([], changed);

    private ModelManifest PublishModelWithNotes(IReadOnlyList<ModelNote> notes, params (string File, byte[] Bytes)[] changed)
    {
        var dataDir = _fixture.Data.DataDir;
        ModelUpdateService.Record(dataDir, ModelManifest.Of(dataDir, "2026-10-02"));
        var contents = ModelManifest.ModelFiles.ToDictionary(file => file,
            file => changed.Any(change => change.File == file) ? changed.First(change => change.File == file).Bytes : File.ReadAllBytes(Path.Combine(dataDir, file)));
        var published = new ModelManifest(ModelManifest.CurrentFormat, "2026-10-09",
            contents.ToDictionary(pair => pair.Key, pair => ModelManifest.Hash(pair.Value)), new Dictionary<string, string>()) { Notes = notes };
        foreach (var (file, bytes) in contents)
            _api.Bytes[published.UrlOf(file)] = bytes;
        _api.Bytes[ModelManifest.ManifestUrl] = published.ToJsonBytes();
        return published;
    }

    /// <summary>A published model that's the one installed, in the <paramref name="format"/> some other version of the app reads.</summary>
    private void PublishModelOfFormat(int format) =>
        _api.Bytes[ModelManifest.ManifestUrl] = (PublishModel() with { Format = format }).ToJsonBytes();

    private byte[] DataBytes(string file) => File.ReadAllBytes(Path.Combine(_fixture.Data.DataDir, file));

    [Fact]
    public void OnStartupANewerModelReplacesTheFilesNobodyChangedAndReloads()
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        var weights = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.TraitWeightsFile), "1.3");
        PublishModel((DataStore.TraitWeightsFile, weights));

        _menu.OnStartup();

        Assert.Empty(_shown);
        Assert.Equal(weights, DataBytes(DataStore.TraitWeightsFile));
        Assert.Equal(1, _replaced);
        Assert.Equal("2026-10-09", ModelManifest.Installed(_fixture.Data.DataDir)!.Published);
    }

    private static byte[] Appended(byte[] csv, string row) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(csv).TrimEnd() + "\r\n" + row + "\r\n");

    /// <summary>The data folder's hero files with one more hero, "Newcomer", rated 3 on the first trait: a newer model's.</summary>
    private (byte[] Heroes, byte[] Scores) WithANewcomer() =>
        (Appended(DataBytes(DataStore.HeroesFile), "newcomer,Newcomer,9001"),
            Appended(DataBytes(DataStore.HeroScoresFile), $"newcomer,{_fixture.Data.Store.Categories.Keys.First()},3"));

    private void Edit(string file, Func<byte[], byte[]> edit) =>
        File.WriteAllBytes(Path.Combine(_fixture.Data.DataDir, file), edit(DataBytes(file)));

    [Fact]
    public void ANewHeroIsTakenIntoAHeroesFileTheOwnerChangedWithoutAskingAndGetsItsArt()
    {
        HaveTopbarArt(derivedByThisVersion: true);
        HavePortraits();
        _fixture.Settings.Current.ArtCheckedAt = _fixture.Clock.Now;
        var (heroes, scores) = WithANewcomer();
        PublishModelWithNotes([new("2026-10-09", "New hero: Newcomer.")], (DataStore.HeroesFile, heroes));
        Edit(DataStore.HeroesFile, csv => Appended(csv, "mine,Mine,9100"));
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);

        menu.OnStartup();

        Assert.DoesNotContain(_shown, shown => shown is ModelUpdateViewModel);
        Assert.Equal(["mine", "newcomer"], _fixture.Data.Store.Heroes.Keys.TakeLast(2));
        Assert.Equal("Formulas updated: New hero: Newcomer.", Assert.Single(_toasts).Message);
        // Taken, so the next startup has nothing to do about it.
        Assert.Equal(ModelManifest.Hash(heroes), ModelManifest.Installed(_fixture.Data.DataDir)!.Files[DataStore.HeroesFile]);
        Assert.Equal(1, download.Started);
        download.Finish(new ArtDownloadReport([], new([], [])));
        _toasts.Clear();
        using var later = Menu();
        later.OnStartup();
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task ANewHeroIsAddedToChangedRatingsWhileTheDialogOnlyAsksAboutTheRatings()
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        var (heroes, scores) = WithANewcomer();
        PublishModel((DataStore.HeroesFile, heroes), (DataStore.HeroScoresFile, scores));
        var mine = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.HeroScoresFile), "1");
        File.WriteAllBytes(Path.Combine(_fixture.Data.DataDir, DataStore.HeroScoresFile), mine);
        Edit(DataStore.HeroesFile, csv => Appended(csv, "mine,Mine,9100"));

        _menu.OnStartup();

        var dialog = Assert.IsType<ModelUpdateViewModel>(_shown[^1]);
        Assert.Equal(["Hero trait ratings"], dialog.Choices.Select(choice => choice.Title));
        Assert.Equal("Added Newcomer from the published heroes.", Assert.Single(_toasts).Message);
        Assert.Equal(3, _fixture.Data.Store.HeroScore("newcomer", _fixture.Data.Store.Categories.Keys.First()));
        await dialog.UpdateCommand.Execute();

        // Kept as theirs, the new hero's ratings with them.
        var kept = Encoding.UTF8.GetString(DataBytes(DataStore.HeroScoresFile));
        Assert.StartsWith(Encoding.UTF8.GetString(mine).TrimEnd(), kept);
        Assert.Contains("\r\nnewcomer,", kept);
    }

    [Fact]
    public async Task WithUpdatesOffAChipOffersTheNewHeroesAndAClickAddsThemWithTheirArt()
    {
        HaveTopbarArt(derivedByThisVersion: true);
        HavePortraits();
        _fixture.Settings.Current.ArtCheckedAt = _fixture.Clock.Now;
        _fixture.Settings.Current.AutoUpdateModel = false;
        var (heroes, scores) = WithANewcomer();
        var mine = DataBytes(DataStore.HeroesFile);
        PublishModel((DataStore.HeroesFile, heroes), (DataStore.HeroScoresFile, scores));
        var download = new HeldArtDownload();
        using var menu = Menu(artDownload: download);

        menu.OnStartup();

        var chip = Assert.Single(menu.Jobs);
        Assert.Equal("New heroes", chip.Title);
        Assert.Equal("Newcomer", chip.StatusText);
        Assert.Equal(mine, DataBytes(DataStore.HeroesFile));
        Assert.Equal(0, download.Started);

        await chip.OpenCommand.Execute();

        Assert.Equal("newcomer", _fixture.Data.Store.Heroes.Keys.Last());
        Assert.Equal(3, _fixture.Data.Store.HeroScore("newcomer", _fixture.Data.Store.Categories.Keys.First()));
        Assert.DoesNotContain(menu.Jobs, job => job.Title == "New heroes");
        Assert.Equal("Added Newcomer from the published heroes.", Assert.Single(_toasts).Message);
        Assert.Equal(1, download.Started);
        download.Finish(new ArtDownloadReport([], new([], [])));
    }

    [Fact]
    public void NoChipIsOfferedWithUpdatesOnTheCheckOffOrNothingNew()
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        var (heroes, scores) = WithANewcomer();
        PublishModel((DataStore.HeroesFile, heroes), (DataStore.HeroScoresFile, scores));

        // Updates on: the update takes them.
        _menu.OnStartup();
        Assert.Empty(_menu.Jobs);
        Assert.Equal("newcomer", _fixture.Data.Store.Heroes.Keys.Last());

        // Updates off, and nothing the published model has that the folder lacks.
        _fixture.Settings.Current.AutoUpdateModel = false;
        using var nothingNew = Menu();
        nothingNew.OnStartup();
        Assert.Empty(nothingNew.Jobs);

        // Updates off with the check off: not even asked.
        _api.Asked.Clear();
        _fixture.Settings.Current.CheckForNewHeroes = false;
        using var off = Menu();
        off.OnStartup();
        Assert.Empty(off.Jobs);
        Assert.DoesNotContain(ModelManifest.ManifestUrl, _api.Asked);
    }

    [Fact]
    public async Task AnUpdateSaysWhatItsPublisherSaidChanged()
    {
        var weights = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.TraitWeightsFile), "1.3");
        PublishModelWithNotes([new("2026-10-09", "Trait weights lean harder on burst.")], (DataStore.TraitWeightsFile, weights));

        await _menu.CheckModelCommand.Execute();

        var toast = Assert.Single(_toasts);
        Assert.Equal("Formulas updated: Trait weights lean harder on burst. The old files are in data\\.backups.", toast.Message);
        Assert.Equal([new ModelNote("2026-10-09", "Trait weights lean harder on burst.")], ModelManifest.Installed(_fixture.Data.DataDir)!.Notes);
    }

    [Fact]
    public async Task ResettingPutsTheTickedFilesBackAndUndoingPutsThemBackAgain()
    {
        PublishModel();
        var mine = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.HeroScoresFile), "1");
        File.WriteAllBytes(Path.Combine(_fixture.Data.DataDir, DataStore.HeroScoresFile), mine);
        var published = DataBytes(DataStore.ItemCoefficientsFile);
        File.WriteAllBytes(Path.Combine(_fixture.Data.DataDir, DataStore.ItemCoefficientsFile), ModelUpdateTests.FirstRowEnding(published, "3"));

        await _menu.ResetModelCommand.Execute();

        var dialog = Assert.IsType<ModelUpdateViewModel>(_shown[^1]);
        Assert.Equal("Reset formulas", dialog.Title);
        Assert.Equal(["Hero trait ratings", "Item formulas"], dialog.Choices.Select(choice => choice.Title));
        Assert.All(dialog.Choices, choice => Assert.True(choice.Replace));
        dialog.Choices[0].Replace = false;
        await dialog.UpdateCommand.Execute();

        Assert.Equal(mine, DataBytes(DataStore.HeroScoresFile));
        Assert.Equal(published, DataBytes(DataStore.ItemCoefficientsFile));
        Assert.Equal("Reset the item formulas to the version published 2026-10-09. Settings → Data can undo it.", _toasts[^1].Message);
        Assert.NotNull(_fixture.Settings.Current.ModelCheckedAt);

        await _menu.UndoModelUpdateCommand.Execute();
        Assert.IsType<ConfirmationModalViewModel>(_shown[^1]).ConfirmCommand!.Execute(null);

        Assert.Equal(ModelUpdateTests.FirstRowEnding(published, "3"), DataBytes(DataStore.ItemCoefficientsFile));
        Assert.Equal("Put back the item formulas from before the last formula update or reset.", _toasts[^1].Message);
        await _menu.UndoModelUpdateCommand.Execute();
        Assert.Contains("There's no formula update to undo", LastMessage().Body);
    }

    [Fact]
    public async Task ChangedFilesAreAskedAboutAndOnlyTheTickedOnesReplaced()
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        var theirScores = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.HeroScoresFile), "2");
        var theirCoefficients = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.ItemCoefficientsFile), "4");
        PublishModel((DataStore.HeroScoresFile, theirScores), (DataStore.ItemCoefficientsFile, theirCoefficients));
        var myScores = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.HeroScoresFile), "1");
        File.WriteAllBytes(Path.Combine(_fixture.Data.DataDir, DataStore.HeroScoresFile), myScores);
        var myCoefficients = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.ItemCoefficientsFile), "3");
        File.WriteAllBytes(Path.Combine(_fixture.Data.DataDir, DataStore.ItemCoefficientsFile), myCoefficients);

        _menu.OnStartup();

        var dialog = Assert.IsType<ModelUpdateViewModel>(_shown[^1]);
        Assert.Equal(["Hero trait ratings", "Item formulas"], dialog.Choices.Select(choice => choice.Title));
        Assert.All(dialog.Choices, choice => Assert.False(choice.Replace));
        dialog.Choices[0].Replace = true;
        await dialog.UpdateCommand.Execute();

        Assert.Equal(theirScores, DataBytes(DataStore.HeroScoresFile));
        Assert.Equal(myCoefficients, DataBytes(DataStore.ItemCoefficientsFile));
        Assert.Equal(1, _replaced);

        // The kept file isn't asked about again until asked for.
        _shown.Clear();
        using var later = Menu();
        later.OnStartup();
        Assert.DoesNotContain(_shown, shown => shown is ModelUpdateViewModel);
        await later.CheckModelCommand.Execute();
        Assert.Equal(["Item formulas"], Assert.IsType<ModelUpdateViewModel>(_shown[^1]).Choices.Select(choice => choice.Title));
    }

    [Fact]
    public async Task CheckingForFormulaUpdatesSaysWhenThereAreNoneOrGitHubIsDown()
    {
        await _menu.CheckModelCommand.Execute();
        Assert.Contains("Couldn't reach GitHub", LastMessage().Body);
        _fixture.Modals.CloseModal();

        PublishModel();
        await _menu.CheckModelCommand.Execute();
        Assert.Contains("up to date: the version published 2026-10-09", LastMessage().Body);
        Assert.Equal(0, _replaced);
    }

    /// <summary>GitHub answered, so "couldn't reach" would be wrong, and an old app is told what to do about it.</summary>
    [Fact]
    public async Task AModelOfANewerFormatSaysTheAppNeedsUpdatingRatherThanThatGitHubIsDown()
    {
        PublishModelOfFormat(ModelManifest.CurrentFormat + 1);

        await _menu.CheckModelCommand.Execute();
        Assert.DoesNotContain("Couldn't reach", LastMessage().Body);
        Assert.Contains("need a newer version of this app", LastMessage().Body);
        Assert.Equal("Formula update", LastMessage().Title);
        _fixture.Modals.CloseModal();

        await _menu.ResetModelCommand.Execute();
        Assert.Equal("Reset formulas", LastMessage().Title);
        Assert.Contains("need a newer version of this app", LastMessage().Body);
        Assert.Equal(0, _replaced);
    }

    [Fact]
    public void AModelOfANewerFormatSaysNothingAtStartup()
    {
        PublishModelOfFormat(ModelManifest.CurrentFormat + 1);
        _fixture.Settings.Current.WelcomeOffered = true;

        _menu.OnStartup();

        Assert.Empty(_shown);
        Assert.Empty(_toasts);
        Assert.Null(_fixture.Settings.Current.ModelCheckedAt);
    }

    // -- checks while the app stays open ----------------------------------------------

    /// <summary>The scheduler as the window wires it, on the fixture's clock and ticking exactly on the interval.</summary>
    private UpdateScheduler TickingEverySixHours(DataMenuViewModel menu)
    {
        var scheduler = new UpdateScheduler(_fixture.Clock, () => 0.5, () => menu.OnScheduledCheck());
        scheduler.Start();
        return scheduler;
    }

    private void NextTick() => _fixture.Clock.AdvanceBy(UpdateScheduler.Interval);

    [Fact]
    public void ATickChecksTheMatchDataLikeARestartWould()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddDays(-4), now);
        using var scheduler = TickingEverySixHours(menu);
        Assert.DoesNotContain(_api.Asked, url => url.Contains("/item-stats?"));

        NextTick();

        Assert.Contains(_api.Asked, url => url.Contains("/item-stats?"));
        Assert.Empty(_shown);
    }

    [Fact]
    public void ATickFlagsAPatchThatCameOutWhileTheAppWasOpenAndDownloadsItQuietly()
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        using var menu = Menu(matchStats: new HeldMatchStats());
        _fixture.Data.Store.PutMatchSegment(new MatchSegment(_patches[1], _patches[1].Start, _patches[0].Start - 1, true, _patches[0].Start,
            SliceCounts.Empty, [], []));
        using var scheduler = TickingEverySixHours(menu);
        Assert.Null(menu.NewerPatch);

        NextTick();

        Assert.Equal("09-29", menu.NewerPatch!.Label);
        Assert.True(Assert.Single(menu.Jobs).IsRunning);
        Assert.Empty(_shown);
        menu.CancelJobs();
    }

    [Fact]
    public void ATickLeavesFreshMatchDataAlone()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddHours(-2), now);
        using var scheduler = TickingEverySixHours(menu);

        NextTick();

        Assert.Empty(menu.Jobs);
        Assert.DoesNotContain(_api.Asked, url => url.Contains("/item-stats?"));
    }

    [Fact]
    public void AnOfflineTickDoesNothingAndTheNextOneAfterReconnectingWorks()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddDays(-4), now);
        using var scheduler = TickingEverySixHours(menu);
        _connectivity.GoOffline();

        NextTick();

        Assert.Empty(_api.Asked);

        _connectivity.Reconnect();
        NextTick();

        Assert.Contains(_api.Asked, url => url.Contains("/item-stats?"));
    }

    [Fact]
    public void ATickWhileADialogIsOpenDoesNothingAndTheNextOneAfterItClosesWorks()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddDays(-4), now);
        using var scheduler = TickingEverySixHours(menu);
        _fixture.Modals.ShowMessage("Something", "To read first.");

        NextTick();

        Assert.Empty(_api.Asked);

        _fixture.Modals.CloseModal();
        NextTick();

        Assert.Contains(_api.Asked, url => url.Contains("/item-stats?"));
    }

    [Fact]
    public async Task ATickWhileAJobHoldsTheDataDoesNothing()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        using var menu = MenuWithData(now.AddDays(-4), now);
        using var scheduler = TickingEverySixHours(menu);

        var running = menu.ModelHealthCommand.Execute().ToTask();
        Assert.True(menu.IsBusy);
        NextTick();
        await running;

        Assert.Empty(_api.Asked);
        _fixture.Modals.CloseModal();
        NextTick();
        Assert.Contains(_api.Asked, url => url.Contains("/item-stats?"));
    }

    [Fact]
    public void TicksNeverStartASecondDownload()
    {
        _fixture.Settings.Current.WelcomeOffered = true;
        var stats = new HeldMatchStats();
        using var menu = Menu(matchStats: stats);
        _fixture.Data.Store.PutMatchSegment(new MatchSegment(_patches[1], _patches[1].Start, _patches[0].Start - 1, true, _patches[0].Start,
            SliceCounts.Empty, [], []));
        using var scheduler = TickingEverySixHours(menu);

        NextTick();
        NextTick();

        Assert.True(Assert.Single(menu.Jobs).IsRunning);
        menu.CancelJobs();
    }

    [Fact]
    public void ATickWithoutAnyArtOrMatchDataLeavesTheFirstRunOfferToItsOwnChip()
    {
        using var menu = Menu();
        using var scheduler = TickingEverySixHours(menu);

        NextTick();

        Assert.Empty(_shown);
        Assert.Empty(menu.Jobs);
        Assert.DoesNotContain(MatchStatsService.Patches, _api.Asked);
    }

    [Fact]
    public void ATickInstallsANewerModelQuietlyWhenTheEditorsAreHidden()
    {
        _fixture.Settings.Current.ShowModelEditors = false;
        var weights = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.TraitWeightsFile), "1.3");
        PublishModel((DataStore.TraitWeightsFile, weights));
        using var scheduler = TickingEverySixHours(_menu);

        NextTick();

        Assert.Equal(weights, DataBytes(DataStore.TraitWeightsFile));
        Assert.Empty(_shown);
        Assert.Empty(_menu.Jobs);
        Assert.Equal(1, _replaced);
    }

    [Fact]
    public async Task WithTheEditorsShownATickOffersTheModelAndWritesNothingUntilAskedTo()
    {
        _fixture.Settings.Current.ShowModelEditors = true;
        var before = DataBytes(DataStore.TraitWeightsFile);
        var weights = ModelUpdateTests.FirstRowEnding(before, "1.3");
        PublishModel((DataStore.TraitWeightsFile, weights));
        using var scheduler = TickingEverySixHours(_menu);

        NextTick();

        var offered = Assert.Single(_menu.Jobs);
        Assert.Equal((DataMenuViewModel.ModelChipTitle, "update available"), (offered.Title, offered.StatusText));
        Assert.Equal(before, DataBytes(DataStore.TraitWeightsFile));
        Assert.Equal("2026-10-02", ModelManifest.Installed(_fixture.Data.DataDir)!.Published);
        Assert.Equal(0, _replaced);

        // The next tick offers it again rather than piling up chips.
        NextTick();
        var chip = Assert.Single(_menu.Jobs);

        await chip.OpenCommand.Execute();

        Assert.Equal(weights, DataBytes(DataStore.TraitWeightsFile));
        Assert.Empty(_menu.Jobs);
    }

    [Fact]
    public void ATickOffersFilesTheOwnerChangedAsAChipRatherThanADialog()
    {
        _fixture.Settings.Current.ShowModelEditors = false;
        PublishModel((DataStore.TraitWeightsFile, ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.TraitWeightsFile), "1.3")));
        var mine = ModelUpdateTests.FirstRowEnding(DataBytes(DataStore.TraitWeightsFile), "2");
        File.WriteAllBytes(Path.Combine(_fixture.Data.DataDir, DataStore.TraitWeightsFile), mine);
        using var scheduler = TickingEverySixHours(_menu);

        NextTick();

        Assert.Empty(_shown);
        Assert.Equal(DataMenuViewModel.ModelChipTitle, Assert.Single(_menu.Jobs).Title);
        Assert.Equal(mine, DataBytes(DataStore.TraitWeightsFile));
    }

    [Fact]
    public void ATickWithNothingNewInTheModelSaysNothing()
    {
        PublishModel();
        using var scheduler = TickingEverySixHours(_menu);

        NextTick();

        Assert.Empty(_shown);
        Assert.Empty(_menu.Jobs);
        Assert.Empty(_toasts);
        Assert.NotNull(_fixture.Settings.Current.ModelCheckedAt);
    }

    [Fact]
    public async Task CheckingEverythingOnDemandSaysHowTheFormulaCheckWent()
    {
        PublishModel();

        await _menu.CheckAllAsync(manual: true);

        Assert.Contains("up to date: the version published 2026-10-09", LastMessage().Body);
    }
}
