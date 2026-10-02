using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.MainWindow.MatchDownload;
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
        _menu = Menu();
    }

    private DataMenuViewModel Menu(IMatchStatsService? matchStats = null, IArtDownloadService? artDownload = null,
        IMatchSnapshotService? snapshots = null)
    {
        var gameApi = new GameApiService(_api);
        return new DataMenuViewModel(_fixture.Data, gameApi, matchStats ?? new MatchStatsService(_api),
            snapshots ?? new MatchSnapshotService(_api), new ExcelExportService(),
            artDownload ?? new ArtDownloadService(gameApi, _api), _art, _fixture.Modals, new NotificationService(new FakeLoggingService()),
            _fixture.Settings, new NoFolderPicker(), new FakeLoggingService(), _fixture.Clock);
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
        Assert.Contains("Item stats refreshed: 495 stat row(s).", report.Body);
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
    public async Task OfflineThePatchListSaysSoAndStartsNothing()
    {
        await _menu.DownloadMatchDataCommand.Execute();

        Assert.Empty(_shown);
        Assert.Empty(_menu.Jobs);
        Assert.False(_menu.IsDownloadingMatchData);
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
        Assert.Equal(("0 of 480 calls", "80 calls"), (details.CallsText, details.Phases[0].Detail));

        // The chip and the card follow the same reports.
        matchStats.Progress!.Report(new MatchFetchProgress(1, 100, 400, 180, 480, "09-29 · Phantom – Eternus · Enemies: Haze", 2_500_000,
            new FetchWait("deadlock-api.com asked to slow down", TimeSpan.FromSeconds(30))));
        Assert.Equal((PhaseState.Done, PhaseState.Running), (details.Phases[0].State, details.Phases[1].State));
        Assert.Equal(("in use", "Enemies: Haze", 100), (details.Phases[0].Detail, details.Phases[1].Detail, details.Phases[1].Done));
        Assert.Equal(("180 of 480 calls", "2.5 MB received"), (details.CallsText, details.BytesText));
        Assert.Equal("deadlock-api.com asked to slow down: waiting 30 s", details.WaitText);
        Assert.Equal((180, 480), (job.Done, job.Total));

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
    public async Task SyncNewDataBackfillsAndReports()
    {
        await _menu.SyncNewDataCommand.Execute();

        var report = LastMessage();
        Assert.Equal("Sync complete", report.Title);
        Assert.StartsWith("Added 0 missing hero × trait row(s), defaulted to 0.", report.Body);
        Assert.Equal(1, _replaced);
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
        Assert.Equal(("about 30 s · 1.2 MB", "about 3 min more · 6.1 MB"), (welcome.MatchDataDetail, welcome.RanksDetail));
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
    public async Task OfflineTheWelcomeOffersTheArtAlone()
    {
        _menu.OnStartup();

        var welcome = Assert.IsType<WelcomeViewModel>(Assert.Single(_shown));
        Assert.False(welcome.CanDownloadMatchData);
        Assert.False(welcome.MatchData);
        welcome.MatchData = false;
        welcome.Art = false;
        Assert.False(await welcome.StartCommand.CanExecute.FirstAsync());
    }

    private static readonly List<Patch> _patches = MatchStatsMath.ParsePatches(["09-29-2026", "09-16-2026 Update"]);

    /// <summary>Match data from both patches, the current one fetched <paramref name="fetched"/>; the service's clock reads <paramref name="now"/>.</summary>
    private DataMenuViewModel MenuWithData(DateTimeOffset fetched, DateTimeOffset now)
    {
        _fixture.Settings.Current.WelcomeOffered = true;
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

    private DataMenuViewModel SharedMenu(DateTimeOffset now) => Menu(snapshots: new MatchSnapshotService(_api, () => now));

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

        var store = _fixture.Data.Store;
        Assert.Equal(2, store.MatchSegments.Count);
        Assert.All(store.MatchSegments, segment => Assert.True(segment.HasRanks));
        Assert.NotEmpty(store.MatchLift);
        Assert.False(Assert.Single(menu.Jobs).HasFailed);
        Assert.Null(Assert.Single(menu.Jobs).Details);
        Assert.DoesNotContain(_api.Asked, url => url.StartsWith("https://api.deadlock-api.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdatesComeFromTheSharedDownloadOnceTheCurrentPatchIsHalfADayOld()
    {
        var now = SyntheticItemStatsApi.Now;
        var snapshot = await ServeSnapshotAsync(now.AddHours(-1), now.AddHours(-1));
        _fixture.Settings.Current.WelcomeOffered = true;
        var store = _fixture.Data.Store;
        foreach (var segment in await SegmentsAsync(now.AddHours(-13)))
            store.PutMatchSegment(segment);
        using var menu = SharedMenu(now);

        menu.OnStartup();

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
        await ServeSnapshotAsync(now.AddDays(-3), now.AddDays(-3));
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
}
