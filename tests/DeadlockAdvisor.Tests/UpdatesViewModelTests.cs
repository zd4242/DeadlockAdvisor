using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Features.MainWindow.Updates;
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
        _updates = new UpdatesViewModel(_fixture.Data, _fixture.Settings, _connectivity, _art, _api, _menu, _app, _status);
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
        Assert.Equal(["App", "Formulas", "Match data", "Art"], updates.Rows.Select(row => row.Title));
        Assert.Equal("Version 0.1.1 · checked 2h ago", Row(UpdateSource.App).Summary);
        Assert.Equal("Published 2026-10-02 · checked 2h ago", Row(UpdateSource.Formulas).Summary);
        Assert.Equal("patch 09-16 · up to date · checked 2h ago", Row(UpdateSource.MatchData).Summary);
        Assert.Equal($"{_fixture.Data.Store.Heroes.Count} portraits, 0 icons · checked 2h ago", Row(UpdateSource.Art).Summary);
    }

    [Fact]
    public void NothingThatWasNeverCheckedIsCalledUpToDate()
    {
        var updates = Build();

        Assert.Equal(UpdateState.NotChecked, updates.State);
        Assert.Equal("Not checked yet", updates.Headline);
        Assert.Equal(UpdateState.NotChecked, Row(UpdateSource.App).State);
        Assert.Equal("Version 0.1.1 · not checked yet", Row(UpdateSource.App).Summary);
        Assert.Equal(UpdateState.NotChecked, Row(UpdateSource.Formulas).State);
        Assert.Equal(UpdateState.NotChecked, Row(UpdateSource.MatchData).State);
        Assert.Equal(UpdateState.Off, Row(UpdateSource.Art).State);
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
        Assert.Equal("Updating match data 31%", updates.Headline);

        var artRun = _menu.DownloadArtAsync(force: false);
        Assert.Equal("Updating match data and art", updates.Headline);
        art.Finish(new ArtDownloadReport([new ArtGroupReport("Hero portraits", 38, 38, 38, 0, [], [])]));
        await artRun;

        Assert.Equal("Updating match data 31%", updates.Headline);
        _menu.CancelJobs();
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
        Assert.Equal("New formulas", row.Headline);
        Assert.Equal("Apply", row.ActionText);
        Assert.Same(_menu.Jobs.Single().OpenCommand, row.Action);
        Assert.Equal("New formulas", updates.Headline);

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

        Assert.All(updates.Rows, row => Assert.Equal(UpdateState.Off, row.State));
        Assert.Equal(UpdateState.Off, updates.State);
        Assert.Equal("Updates are off", updates.Headline);
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
