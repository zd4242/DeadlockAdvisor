using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.MainWindow.Updates;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;
using ReactiveUI;

namespace DeadlockAdvisor.Tests;

/// <summary>The Updates chip's view model: each row's state and the whole's, worked out from the services over fakes.</summary>
public sealed class UpdatesViewModelTests : IDisposable
{
    private const string ReleasePage = "https://github.com/zd4242/DeadlockAdvisor/releases/tag/v0.2.0";

    /// <summary>The app's update service, with the newest release and how often it was asked for the test to choose.</summary>
    private sealed class StubAppUpdate : IAppUpdateService
    {
        public Version? Current { get; set; } = new(0, 1, 1);
        public AppRelease? Latest { get; set; } = new("0.1.1", "https://github.com/zd4242/DeadlockAdvisor/releases/tag/v0.1.1");
        public TaskCompletionSource<AppRelease?>? Hold { get; set; }
        public int Checks { get; private set; }
        public bool CanInstall(AppRelease release) => true;
        public void RestartAfterExit(bool restart = true) { }
        public Task CleanUpAsync() => Task.CompletedTask;

        public Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            return Hold?.Task ?? Task.FromResult(Latest);
        }

        public Task DownloadAsync(AppRelease release, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FailingArtDownload : IArtDownloadService
    {
        public Task<ArtDownloadReport> DownloadAsync(DataStore store, string assetsDir, bool force, IProgress<FetchProgress>? progress,
            CancellationToken cancellationToken) => throw new HttpRequestException("The site is down.");
    }

    private readonly DataFixture _fixture = new();
    private readonly FakeDeadlockApi _api = new();
    private readonly FakeConnectivity _connectivity = new();
    private readonly ArtService _art = new(new FakeLoggingService());
    private readonly StubAppUpdate _appUpdate = new();
    private readonly NotificationService _notifications = new(new FakeLoggingService());
    private readonly List<ViewModelBase> _shown = [];
    private readonly IDisposable _watchModals;
    private readonly ReactiveCommand<string, Unit> _open = ReactiveCommand.Create<string>(_ => { });
    private readonly ReactiveCommand<Unit, Unit> _recent = ReactiveCommand.Create(() => { });
    private DataMenuViewModel _menu = null!;
    private AppUpdateViewModel _app = null!;
    private DataStatusViewModel _status = null!;
    private UpdatesViewModel _updates = null!;

    public UpdatesViewModelTests()
    {
        _art.SetAssetsDir(_fixture.Data.AssetsDir);
        _watchModals = _fixture.Modals.ShowModalObservable.Subscribe(_shown.Add);
        _fixture.Settings.Current.WelcomeOffered = true;
    }

    public void Dispose()
    {
        _watchModals.Dispose();
        _updates?.Dispose();
        _status?.Dispose();
        _app?.Dispose();
        _menu?.Dispose();
        _open.Dispose();
        _fixture.Dispose();
    }

    private UpdatesViewModel Build(IMatchStatsService? matchStats = null, IArtDownloadService? artDownload = null)
    {
        var gameApi = new GameApiService(_api);
        _menu = new DataMenuViewModel(_fixture.Data, gameApi, matchStats ?? new MatchStatsService(_api), new MatchSnapshotService(_api),
            new ModelUpdateService(_api), new ExcelExportService(), artDownload ?? new ArtDownloadService(gameApi, _api), _art, _fixture.Modals,
            _notifications, _fixture.Settings, new NoFolderPicker(), new FakeLoggingService(), _connectivity, _fixture.Clock);
        _app = new AppUpdateViewModel(_appUpdate, _fixture.Settings, _notifications, _open);
        _status = new DataStatusViewModel(_fixture.Data, _menu, _fixture.Settings);
        _updates = new UpdatesViewModel(_fixture.Data, _fixture.Settings, _connectivity, _art, _api, _notifications, _fixture.Modals, _menu, _app, _status, _recent);
        return _updates;
    }

    private sealed class NoFolderPicker : IFilePickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startIn = null) => Task.FromResult<string?>(null);
    }

    private UpdateRowViewModel Row(UpdateSource source) => _updates.Rows.Single(row => row.Source == source);

    /// <summary>Every source checked two hours ago, with art and a record of the model's version: a current install.</summary>
    private void ACurrentInstall()
    {
        var settings = _fixture.Settings.Current;
        var checkedAt = DateTimeOffset.Now.AddHours(-2);
        settings.AppUpdateCheckedAt = settings.ModelCheckedAt = settings.MatchDataCheckedAt = settings.ArtCheckedAt = checkedAt;
        ModelUpdateService.Record(_fixture.Data.DataDir, ModelManifest.Of(_fixture.Data.DataDir, "2026-10-02"));
        HavePortraits();
    }

    private void HavePortraits()
    {
        var folder = _art.FolderOf(ArtKind.Hero);
        Directory.CreateDirectory(folder);
        foreach (var heroId in _fixture.Data.Store.Heroes.Keys)
            File.WriteAllBytes(Path.Combine(folder, heroId + ".png"), [1]);
        _art.Refresh();
    }

    private void PublishNewerWeights()
    {
        var dataDir = _fixture.Data.DataDir;
        ModelUpdateService.Record(dataDir, ModelManifest.Of(dataDir, "2026-10-02"));
        var contents = ModelManifest.ModelFiles.ToDictionary(file => file, file => file == DataStore.TraitWeightsFile
            ? ModelUpdateTests.FirstRowEnding(File.ReadAllBytes(Path.Combine(dataDir, file)), "1.3")
            : File.ReadAllBytes(Path.Combine(dataDir, file)));
        var published = new ModelManifest(ModelManifest.CurrentFormat, "2026-10-09",
            contents.ToDictionary(pair => pair.Key, pair => ModelManifest.Hash(pair.Value)), new Dictionary<string, string>()) { Notes = [] };
        foreach (var (file, bytes) in contents)
            _api.Bytes[published.UrlOf(file)] = bytes;
        _api.Bytes[ModelManifest.ManifestUrl] = published.ToJsonBytes();
    }

    private void ANewerPatchIsOut()
    {
        _fixture.Settings.Current.AutoUpdateMatchData = false;
        _api.Json[MatchStatsService.Patches] = () => JsonNode.Parse("""[{"title": "10-01-2026 Gameplay Update"}]""");
    }

    [Fact]
    public void ACurrentInstallReadsUpToDateAndSaysWhatWasCheckedAndWhen()
    {
        ACurrentInstall();

        var updates = Build();

        Assert.Equal(UpdateState.UpToDate, updates.State);
        Assert.Equal("Up to date", updates.Headline);
        Assert.Equal("Everything is up to date.", updates.Status);
        Assert.All(updates.Rows, row => Assert.Equal(UpdateState.UpToDate, row.State));
        Assert.Equal(["App", "Advisor rating", "Match results", "Art"], updates.Rows.Select(row => row.Title));
        Assert.Equal("Version 0.1.1 · checked 2h ago", Row(UpdateSource.App).Summary);
        Assert.Equal("Published 2026-10-02 · checked 2h ago", Row(UpdateSource.Formulas).Summary);
        Assert.Equal("patch 09-16 · up to date · checked 2h ago", Row(UpdateSource.MatchData).Summary);
        Assert.Equal($"{_fixture.Data.Store.Heroes.Count} portraits, 0 icons · checked 2h ago", Row(UpdateSource.Art).Summary);
    }

    [Fact]
    public void AHeroWithoutAPortraitIsNamedAndMakesTheArtRowWaiting()
    {
        ACurrentInstall();
        var heroes = _fixture.Data.Store.Heroes;
        File.Delete(Path.Combine(_art.FolderOf(ArtKind.Hero), heroes.Keys.First() + ".png"));
        _art.Refresh();

        var updates = Build();

        var row = Row(UpdateSource.Art);
        Assert.Equal(UpdateState.Available, row.State);
        Assert.Equal($"No art for {heroes.Values.First().HeroName}", row.Headline);
        Assert.Contains($"No portrait yet for {heroes.Values.First().HeroName}", row.Summary);
        Assert.Equal(UpdateState.Available, updates.State);
    }

    [Fact]
    public void NothingThatWasNeverCheckedIsCalledUpToDate()
    {
        HavePortraits();
        var updates = Build();

        Assert.Equal(UpdateState.NotChecked, updates.State);
        Assert.Equal("Not checked yet", updates.Headline);
        Assert.Equal(UpdateState.NotChecked, Row(UpdateSource.App).State);
        Assert.Equal("Version 0.1.1 · not checked yet", Row(UpdateSource.App).Summary);
        Assert.Equal(UpdateState.NotChecked, Row(UpdateSource.Formulas).State);
        Assert.Equal(UpdateState.NotChecked, Row(UpdateSource.MatchData).State);
        Assert.Equal(UpdateState.UpToDate, Row(UpdateSource.Art).State);
    }

    /// <summary>Not "Up to date" while there is no match data or art to be current: the chip says what is missing.</summary>
    [Fact]
    public void WhatWasNeverDownloadedIsSaidSoRatherThanUpToDate()
    {
        ACurrentInstall();
        foreach (var file in Directory.GetFiles(_art.FolderOf(ArtKind.Hero)))
            File.Delete(file);
        _art.Refresh();
        var updates = Build();

        Assert.Equal(UpdateState.NotDownloaded, Row(UpdateSource.Art).State);
        Assert.Equal(UpdateState.NotDownloaded, updates.State);
        Assert.Equal("Art not downloaded", updates.Headline);
        Assert.Equal("Some of it hasn't been downloaded yet.", updates.Status);
        Assert.False(updates.IsGood);
        Assert.False(updates.IsAttention);

        _fixture.Data.Store.MatchMeta.Clear();
        _fixture.Data.NotifyReplaced();

        Assert.Equal(UpdateState.NotDownloaded, Row(UpdateSource.MatchData).State);
        Assert.Equal("Match results not downloaded", Row(UpdateSource.MatchData).Headline);
        Assert.Equal("Match results and art not downloaded", updates.Headline);
    }

    [Fact]
    public void EachRowSaysInPlainWordsWhatItIs()
    {
        var updates = Build();

        Assert.Equal(["", "hero ratings and item formulas", "how items do in real matches", "hero portraits and item icons"],
            updates.Rows.Select(row => row.About));
    }

    [Fact]
    public async Task ACheckUnderWayReadsCheckingUntilItsAnswer()
    {
        ACurrentInstall();
        _appUpdate.Hold = new TaskCompletionSource<AppRelease?>();
        var updates = Build();

        var checking = _app.CheckAsync();

        Assert.Equal(UpdateState.Checking, Row(UpdateSource.App).State);
        Assert.Equal("Version 0.1.1 · checking GitHub…", Row(UpdateSource.App).Summary);
        Assert.Equal(UpdateState.Checking, updates.State);
        Assert.Equal("Checking for updates…", updates.Headline);

        _appUpdate.Hold.SetResult(_appUpdate.Latest);
        await checking;

        Assert.Equal(UpdateState.UpToDate, updates.State);
    }

    [Fact]
    public void WithoutAConnectionTheRowsReadOfflineAndTheChipFollowsTheConnection()
    {
        ACurrentInstall();
        var updates = Build();

        _connectivity.GoOffline();

        Assert.Equal(UpdateState.Offline, updates.State);
        Assert.Equal("Updates paused", updates.Headline);
        Assert.Equal("No internet connection: everything works from what's saved.", updates.Status);
        Assert.All(updates.Rows, row => Assert.Equal(UpdateState.Offline, row.State));
        Assert.Equal("Version 0.1.1 · checked 2h ago", Row(UpdateSource.App).Summary);

        _connectivity.Reconnect();

        Assert.Equal(UpdateState.UpToDate, updates.State);
    }

    [Fact]
    public async Task ANewerPatchIsSomethingWaitingAndDownloadingIsItsAction()
    {
        ACurrentInstall();
        ANewerPatchIsOut();
        var updates = Build();

        await _menu.CheckAllAsync(manual: false);

        var row = Row(UpdateSource.MatchData);
        Assert.Equal(UpdateState.Available, row.State);
        Assert.Equal("Patch 10-01 is out", row.Headline);
        Assert.Equal("Update…", row.ActionText);
        Assert.Same(_menu.DownloadMatchDataCommand, row.Action);
        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Equal("Patch 10-01 is out", updates.Headline);
        Assert.Equal("An update is waiting.", updates.Status);
    }

    [Fact]
    public async Task AnUpdateToTheAppIsOfferedWithItsPageAndAWayToSkipIt()
    {
        ACurrentInstall();
        _appUpdate.Latest = new AppRelease("0.2.0", ReleasePage);
        var updates = Build();

        await _app.CheckAsync();

        var row = Row(UpdateSource.App);
        Assert.Equal(UpdateState.Available, row.State);
        Assert.Equal("Version 0.2.0 is out · you have 0.1.1", row.Summary);
        Assert.Equal("Update", row.ActionText);
        Assert.Same(_app.UpdateCommand, row.Action);
        Assert.Equal(["What's new", "Skip this version"], row.Links.Select(link => link.Text));
        Assert.Equal("Version 0.2.0 is out", updates.Headline);

        row.Links.Single(link => link.Text == "Skip this version").Command.Execute(null);

        Assert.Equal("0.2.0", _fixture.Settings.Current.SkippedAppVersion);
        Assert.Equal(UpdateState.UpToDate, row.State);
        Assert.Equal(UpdateState.UpToDate, updates.State);
    }

    [Fact]
    public async Task SeveralThingsWaitingAreCountedNotListed()
    {
        ACurrentInstall();
        ANewerPatchIsOut();
        _appUpdate.Latest = new AppRelease("0.2.0", ReleasePage);
        var updates = Build();

        await _app.CheckAsync();
        await _menu.CheckAllAsync(manual: false);

        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Equal("2 updates", updates.Headline);
        Assert.Equal("2 updates are waiting.", updates.Status);
    }

    [Fact]
    public async Task DownloadsShowTheirProgressAndOutrankWhatIsWaiting()
    {
        ACurrentInstall();
        _appUpdate.Latest = new AppRelease("0.2.0", ReleasePage);
        var matchStats = new HeldMatchStats();
        var art = new HeldArtDownload();
        var updates = Build(matchStats, art);
        await _app.CheckAsync();
        Assert.Equal(UpdateState.Available, updates.State);

        _ = _menu.DownloadMatchDataAsync(HeldMatchStats.Plan);
        matchStats.Progress!.Report(new MatchFetchProgress(2, 52, 400, 812, 2600, "09-29 · Emissary – Oracle · Enemies: Haze", 0));

        var row = Row(UpdateSource.MatchData);
        Assert.Equal(UpdateState.Updating, row.State);
        Assert.Equal(31.0, row.Percent);
        Assert.Equal("Stop", row.ActionText);
        Assert.Equal(UpdateState.Updating, updates.State);
        Assert.Equal("Updating match results 31%", updates.Headline);

        var artRun = _menu.DownloadArtAsync(force: false);
        Assert.Equal("Updating match results and art", updates.Headline);
        art.Finish(new ArtDownloadReport([new ArtGroupReport("Hero portraits", 38, 38, 38, 0, [], [])]));
        await artRun;

        Assert.Equal("Updating match results 31%", updates.Headline);
        _menu.CancelJobs();
    }

    /// <summary>The same word for the same thing: a current row's button checks, and the download that opens a dialog is a link.</summary>
    [Fact]
    public void ACurrentRowsButtonChecksAndKeepsItsDownloadAsALink()
    {
        Build(EverythingIsCurrent(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)));

        var matchData = Row(UpdateSource.MatchData);
        Assert.Equal(UpdateState.UpToDate, matchData.State);
        Assert.Equal("Check", matchData.ActionText);
        Assert.Same(_menu.CheckMatchDataCommand, matchData.Action);
        var again = Assert.Single(matchData.Links);
        Assert.Equal("Download again…", again.Text);
        Assert.Same(_menu.DownloadMatchDataCommand, again.Command);

        var art = Row(UpdateSource.Art);
        Assert.Equal(UpdateState.UpToDate, art.State);
        Assert.Equal("Check", art.ActionText);
        Assert.Same(_menu.CheckArtCommand, art.Action);
        var download = Assert.Single(art.Links);
        Assert.Equal("Download…", download.Text);
        Assert.Same(_menu.DownloadArtCommand, download.Command);

        Assert.Equal("Check", Row(UpdateSource.Formulas).ActionText);
        Assert.Equal("Check", Row(UpdateSource.App).ActionText);
    }

    [Fact]
    public async Task CheckingTheArtSaysSoWhenNothingWasNew()
    {
        ACurrentInstall();
        var art = new HeldArtDownload();
        Build(artDownload: art);

        var run = _menu.CheckArtCommand.Execute().ToTask();
        Assert.Equal(1, art.Started);
        art.Finish(new ArtDownloadReport([new ArtGroupReport("Hero portraits", 38, 38, 0, 38, [], [])]));
        await run;

        Assert.Equal(["The art is up to date."], Said());
        Assert.Empty(_menu.Jobs);
    }

    /// <summary>A routine check that can't reach the site says nothing, but this one was asked for.</summary>
    [Fact]
    public async Task CheckingTheArtSaysSoWhenTheSiteCantBeReached()
    {
        ACurrentInstall();
        Build(artDownload: new FailingArtDownload());

        await _menu.CheckArtCommand.Execute();

        Assert.True(_menu.Jobs.Single().HasFailed);
        Assert.Equal(["Art download failed. The status bar has the details."], Said());
        Assert.Equal(UpdateState.Failed, Row(UpdateSource.Art).State);
    }

    [Fact]
    public async Task ADownloadThatFailedReadsFailedUntilItsChipIsDismissed()
    {
        ACurrentInstall();
        var updates = Build(artDownload: new FailingArtDownload());

        await _menu.DownloadArtAsync(force: false);

        var row = Row(UpdateSource.Art);
        Assert.Equal(UpdateState.Failed, row.State);
        Assert.Equal("Art download failed", row.Headline);
        Assert.Equal("Try again", row.ActionText);
        Assert.Same(_menu.DownloadArtCommand, row.Action);
        Assert.Equal(["What went wrong"], row.Links.Select(link => link.Text));
        Assert.Equal(UpdateState.Failed, updates.State);
        Assert.Equal("Art download failed", updates.Headline);

        await _menu.Jobs.Single().DismissCommand.Execute();

        Assert.Equal(UpdateState.UpToDate, row.State);
        Assert.Equal(UpdateState.UpToDate, updates.State);
    }

    [Fact]
    public void AnOfferTheDataMenuPutInTheStatusBarShowsInTheFormulasRowAndAClickTakesIt()
    {
        ACurrentInstall();
        _fixture.Settings.Current.ShowModelEditors = true;
        PublishNewerWeights();
        var updates = Build();

        Assert.True(_menu.OnScheduledCheck());

        var row = Row(UpdateSource.Formulas);
        Assert.Equal(UpdateState.Available, row.State);
        Assert.Equal("New advisor rating", row.Headline);
        Assert.Equal("Apply", row.ActionText);
        Assert.Same(_menu.Jobs.Single().OpenCommand, row.Action);
        Assert.Equal("New advisor rating", updates.Headline);

        row.Action!.Execute(null);

        Assert.Empty(_menu.Jobs);
        Assert.StartsWith("Published 2026-10-09 · checked ", Row(UpdateSource.Formulas).Summary);
        Assert.Equal(UpdateState.UpToDate, row.State);
    }

    [Fact]
    public void TheFirstRunsOfferAsAChipCountsAsSomethingWaiting()
    {
        _fixture.Settings.Current.WelcomeOffered = false;
        var updates = Build();
        Assert.NotEqual(UpdateState.Available, updates.State);

        _menu.OnReconnected();

        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Equal("Downloads available", updates.Headline);
    }

    [Fact]
    public void WhatIsTurnedOffNeitherCountsAgainstNorForUpToDate()
    {
        ACurrentInstall();
        _fixture.Settings.Current.AutoUpdateModel = false;
        var updates = Build();

        Assert.Equal(UpdateState.Off, Row(UpdateSource.Formulas).State);
        Assert.Equal("Published 2026-10-02 · automatic updates are off", Row(UpdateSource.Formulas).Summary);
        Assert.Equal(UpdateState.UpToDate, updates.State);

        _fixture.Settings.Update(settings =>
        {
            settings.CheckForAppUpdates = false;
            settings.AutoUpdateMatchData = false;
            settings.CheckForNewerPatch = false;
            settings.ArtCheckedAt = null;
        });
        foreach (var file in Directory.GetFiles(_art.FolderOf(ArtKind.Hero)))
            File.Delete(file);
        _art.Refresh();
        _fixture.Settings.Update(_ => { });

        Assert.All(updates.Rows.Where(row => row.Source != UpdateSource.Art), row => Assert.Equal(UpdateState.Off, row.State));
        Assert.Equal(UpdateState.NotDownloaded, Row(UpdateSource.Art).State);
        Assert.Equal("Art not downloaded", updates.Headline);
    }

    [Fact]
    public void ABuildMadeOutsideTheReleaseWorkflowHasNothingToCompare()
    {
        ACurrentInstall();
        _appUpdate.Current = null;

        Build();

        var row = Row(UpdateSource.App);
        Assert.Equal(UpdateState.Off, row.State);
        Assert.Null(row.Action);
    }

    [Fact]
    public async Task CheckAllRunsTheFormulaMatchDataAndAppChecks()
    {
        ACurrentInstall();
        var updates = Build();

        await updates.CheckAllCommand.Execute();

        Assert.Contains(ModelManifest.ManifestUrl, _api.Asked);
        Assert.Contains(MatchStatsService.Patches, _api.Asked);
        Assert.Equal(1, _appUpdate.Checks);
        Assert.False(updates.IsCheckingAll);
    }

    private static readonly List<Patch> _patches = MatchStatsMath.ParsePatches(["09-29-2026", "09-16-2026 Update"]);

    /// <summary>The model published is the one installed, and match data from both patches was fetched two hours before <paramref name="now"/>.</summary>
    private MatchStatsService EverythingIsCurrent(DateTimeOffset now)
    {
        ACurrentInstall();
        var dataDir = _fixture.Data.DataDir;
        var published = ModelManifest.Of(dataDir, "2026-10-02") with { Notes = [] };
        foreach (var file in ModelManifest.ModelFiles)
            _api.Bytes[published.UrlOf(file)] = File.ReadAllBytes(Path.Combine(dataDir, file));
        _api.Bytes[ModelManifest.ManifestUrl] = published.ToJsonBytes();

        _api.Json[MatchStatsService.Patches] = () => JsonNode.Parse("""[{"title": "09-29-2026"}, {"title": "09-16-2026 Update"}]""");
        var fetched = now.AddHours(-2).ToUnixTimeSeconds();
        var store = _fixture.Data.Store;
        store.PutMatchSegment(new MatchSegment(_patches[0], _patches[0].Start, fetched, false, fetched, SliceCounts.Empty, [], []));
        store.PutMatchSegment(new MatchSegment(_patches[1], _patches[1].Start, _patches[0].Start - 1, true, fetched, SliceCounts.Empty, [], []));
        store.MatchMeta["latest_patch"] = new JsonObject { ["label"] = _patches[0].Label, ["start"] = _patches[0].Start };
        return new MatchStatsService(_api, () => now, (_, _) => Task.CompletedTask);
    }

    private List<string> Said() => _notifications.Recent.Select(notification => notification.Message).ToList();

    [Fact]
    public async Task CheckAllSaysOnceThatEverythingIsUpToDateWhenNothingElseWasSaid()
    {
        var updates = Build(EverythingIsCurrent(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)));

        await updates.CheckAllCommand.Execute();

        Assert.Equal(["Everything is up to date."], Said());
        Assert.Empty(_shown);
    }

    /// <summary>"Everything is up to date" after "couldn't reach GitHub" would contradict it.</summary>
    [Fact]
    public async Task CheckAllLeavesTheSumUpOutWhenASourceSaidSomething()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var updates = Build(EverythingIsCurrent(now));
        _api.Bytes.Remove(ModelManifest.ManifestUrl);

        await updates.CheckAllCommand.Execute();

        var said = Assert.Single(Said());
        Assert.Contains("Couldn't reach GitHub", said);
    }

    [Fact]
    public async Task CheckAllWhileOfflineChecksTheConnectionAndSaysSoWithoutAskingAnyone()
    {
        ACurrentInstall();
        var updates = Build();
        _connectivity.GoOffline();

        await updates.CheckAllCommand.Execute();

        Assert.Equal(1, _connectivity.Retries);
        Assert.Equal(["No internet connection: everything works from what's saved."], Said());
        Assert.Empty(_api.Asked);
        Assert.Equal(0, _appUpdate.Checks);
    }

    [Fact]
    public void TheFooterTellsWhatTheSessionHasDownloaded()
    {
        ACurrentInstall();
        var updates = Build();
        Assert.Equal("Nothing downloaded this session", updates.Downloaded);

        _api.BytesReceived = 3_200_000;
        updates.Refresh();

        Assert.Equal("Downloaded this session: 3.2 MB", updates.Downloaded);
    }

    [Fact]
    public void TheMatchDataRowCarriesTheMatchDataCardAsItsDetails()
    {
        var row = Build().Rows.Single(row => row.Source == UpdateSource.MatchData);

        Assert.Same(_status, row.Details);
        Assert.False(row.IsExpanded);

        row.ToggleDetailsCommand.Execute().Subscribe();

        Assert.True(row.IsExpanded);
        Assert.All(_updates.Rows.Where(other => other != row), other => Assert.False(other.HasDetails));
    }
}
