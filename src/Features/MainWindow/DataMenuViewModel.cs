using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow.MatchDownload;
using DeadlockAdvisor.Features.MainWindow.ModelUpdate;
using DeadlockAdvisor.Features.MainWindow.Welcome;
using DeadlockAdvisor.Features.Shared.BackgroundJobs;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Modals.Progress;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// The Data menu: syncing with the game, fetching match stats, reloading, exporting, and where the
/// data and art live. The long downloads, match stats and art, run in the background as
/// <see cref="Jobs"/> the status bar shows and can cancel; the quick game sync runs behind a progress
/// modal. Nothing is written until a job has everything it needs.
/// </summary>
public class DataMenuViewModel : ViewModelBase
{
    public const string ArtChangedAction = "ArtChanged";
    public const string WelcomeChipTitle = "Downloads";

    // Showing new art re-reads every image on screen, so it's done this often at most while art arrives.
    private static readonly TimeSpan _artShowGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _toastTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _newsToastTime = TimeSpan.FromSeconds(12);

    private readonly IScheduler _clock;
    private readonly IDataService _data;
    private readonly IGameApiService _gameApi;
    private readonly IMatchStatsService _matchStats;
    private readonly IMatchSnapshotService _snapshots;
    private readonly IModelUpdateService _models;
    private readonly IExcelExportService _excel;
    private readonly IArtDownloadService _artDownload;
    private readonly IArtService _art;
    private readonly IModalService _modals;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IFilePickerService _filePicker;
    private readonly ILoggingService _log;
    private readonly IConnectivityService _connectivity;

    public DataMenuViewModel(IDataService data, IGameApiService gameApi, IMatchStatsService matchStats, IMatchSnapshotService snapshots,
        IModelUpdateService models, IExcelExportService excel, IArtDownloadService artDownload, IArtService art, IModalService modals,
        INotificationService notifications, ISettingsService settings, IFilePickerService filePicker, ILoggingService log,
        IConnectivityService connectivity)
        : this(data, gameApi, matchStats, snapshots, models, excel, artDownload, art, modals, notifications, settings, filePicker, log, connectivity,
            Scheduler.Default)
    {
    }

    internal DataMenuViewModel(IDataService data, IGameApiService gameApi, IMatchStatsService matchStats, IMatchSnapshotService snapshots,
        IModelUpdateService models, IExcelExportService excel, IArtDownloadService artDownload, IArtService art, IModalService modals,
        INotificationService notifications, ISettingsService settings, IFilePickerService filePicker, ILoggingService log,
        IConnectivityService connectivity, IScheduler clock)
    {
        _clock = clock;
        _log = log;
        _connectivity = connectivity;
        _data = data;
        _gameApi = gameApi;
        _matchStats = matchStats;
        _snapshots = snapshots;
        _models = models;
        _excel = excel;
        _artDownload = artDownload;
        _art = art;
        _modals = modals;
        _notifications = notifications;
        _settings = settings;
        _filePicker = filePicker;

        var idle = this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy);
        SyncNewDataCommand = ReactiveCommand.Create(SyncNewData);
        SyncGameApiCommand = ReactiveCommand.CreateFromTask(SyncGameApiAsync, idle);
        DownloadMatchDataCommand = ReactiveCommand.CreateFromTask(OfferMatchDownloadAsync,
            this.WhenAnyValue(vm => vm.IsBusy, vm => vm.IsDownloadingMatchData, (busy, fetching) => !busy && !fetching));
        ModelHealthCommand = ReactiveCommand.CreateFromTask(ShowModelHealthAsync, idle);
        CheckModelCommand = ReactiveCommand.CreateFromTask(() => CheckModelAsync(manual: true), idle);
        ResetModelCommand = ReactiveCommand.CreateFromTask(ResetModelAsync, idle);
        UndoModelUpdateCommand = ReactiveCommand.Create(OfferModelUndo, idle);
        ModelNotesCommand = ReactiveCommand.Create(ShowModelNotes);
        ReloadCommand = ReactiveCommand.Create(Reload);
        ExportCommand = ReactiveCommand.Create(Export);
        OpenDataFolderCommand = ReactiveCommand.Create(() => OpenFolder(_data.DataDir));
        OpenFolderCommand = ReactiveCommand.Create<string>(OpenFolder);
        // A download writes into the folder it started in, so the folder stays put until they're done.
        ChangeDataFolderCommand = ReactiveCommand.CreateFromTask(ChangeDataFolderAsync,
            this.WhenAnyValue(vm => vm.IsBusy, vm => vm.HasRunningJobs, (busy, running) => !busy && !running));
        DownloadArtCommand = ReactiveCommand.Create(OfferArtDownload,
            this.WhenAnyValue(vm => vm.IsBusy, vm => vm.IsDownloadingArt, (busy, downloading) => !busy && !downloading));

        this.WhenAnyValue(vm => vm.IsDownloadingMatchData, vm => vm.IsDownloadingArt)
            .Skip(1)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasRunningJobs)))
            .DisposeWith(Disposables);
    }

    /// <summary>A job behind a modal, or the model health report, is running; one at a time.</summary>
    [Reactive] public bool IsBusy { get; private set; }

    [Reactive] public bool IsDownloadingMatchData { get; private set; }
    [Reactive] public bool IsDownloadingArt { get; private set; }
    public bool HasRunningJobs => IsDownloadingMatchData || IsDownloadingArt;

    /// <summary>The background downloads the status bar shows: running, or finished with a report not yet opened.</summary>
    public ObservableCollection<BackgroundJobViewModel> Jobs { get; } = [];

    /// <summary>Set when a patch is out that the match data predates.</summary>
    [Reactive] public Patch? NewerPatch { get; private set; }

    public ReactiveCommand<Unit, Unit> SyncNewDataCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncGameApiCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadMatchDataCommand { get; }
    public ReactiveCommand<Unit, Unit> ModelHealthCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckModelCommand { get; }

    /// <summary>Put files that differ from the published model back to it, asking which.</summary>
    public ReactiveCommand<Unit, Unit> ResetModelCommand { get; }

    /// <summary>Put back the files the last formula update or reset replaced.</summary>
    public ReactiveCommand<Unit, Unit> UndoModelUpdateCommand { get; }

    /// <summary>What the installed model's publisher said changed, version by version.</summary>
    public ReactiveCommand<Unit, Unit> ModelNotesCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDataFolderCommand { get; }

    /// <summary>Show a folder in the file manager, or a web page in the browser.</summary>
    public ReactiveCommand<string, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeDataFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadArtCommand { get; }

    public string ExportPath => Path.Combine(_data.DataRoot, ExcelExportService.FileName);

    /// <summary>How often the art is checked against deadlock-api.com's, which costs one "not modified" per file.</summary>
    public static readonly TimeSpan ArtCheckInterval = TimeSpan.FromDays(7);

    /// <summary>How soon after a check a hero it found no art for is asked about again, rather than waiting out <see cref="ArtCheckInterval"/>.</summary>
    public static readonly TimeSpan MissingArtRetryInterval = TimeSpan.FromDays(1);

    private string TopbarDir => Path.Combine(_data.AssetsDir, "topbar");

    /// <summary>
    /// Once the window is up: take a newer published model, if updates are on. On a first run without
    /// art, offer it and the match data in one dialog. Otherwise check the match data against the patch
    /// list in the background, and keep the top-bar art detection matches against current: quietly, about
    /// weekly, and at once when this version cuts portraits differently from the last. Checks that find
    /// no connection are made again by <see cref="OnReconnected"/>.
    /// </summary>
    public void OnStartup()
    {
        StartModelCheck();
        if (NeedsWelcome())
        {
            Launch(OfferWelcomeAsync);
            return;
        }
        StartDataChecks();
    }

    /// <summary>
    /// The connection is back after being down, so the startup checks that couldn't be made are made now. A first
    /// run's offer that never got shown is made as a chip: a dialog would stop Detect from running until it was closed.
    /// </summary>
    public void OnReconnected()
    {
        StartModelCheck();
        if (NeedsWelcome())
            OfferWelcomeAsChip();
        else
            StartDataChecks();
    }

    private bool NeedsWelcome() =>
        !_settings.Current.WelcomeOffered && _art.Count(ArtKind.Hero) == 0 && _art.Count(ArtKind.Item) == 0;

    private void StartModelCheck()
    {
        if (_settings.Current.AutoUpdateModel)
            Launch(() => CheckModelAsync(manual: false));
        else if (_settings.Current.CheckForNewHeroes)
            Launch(OfferNewHeroesAsync);
    }

    private void StartDataChecks()
    {
        Launch(CheckMatchDataAsync);
        var hasTopbarArt = Directory.Exists(TopbarDir)
                           && Directory.EnumerateFiles(TopbarDir).Any(file => ImageFile.Suffixes.Contains(Path.GetExtension(file).ToLowerInvariant()));
        var due = _settings.Current.ArtCheckedAt is not { } checkedAt
                  || _clock.Now - checkedAt >= ArtCheckInterval
                  || _clock.Now - checkedAt >= MissingArtRetryInterval && HeroesMissingArt();
        if (hasTopbarArt && (due || !TopbarDerivation.IsCurrent(TopbarDir)))
            Launch(() => DownloadArtAsync(force: false, quiet: true));
    }

    /// <summary>
    /// Whether a hero has no portrait, for someone who has downloaded art before: one added by a model update
    /// (art can't come with it) or one the API didn't have art for at the last check.
    /// </summary>
    private bool HeroesMissingArt() =>
        _art.Count(ArtKind.Hero) > 0 && _data.Store.Heroes.Keys.Any(heroId => !_art.Has(ArtKind.Hero, heroId));

    /// <summary>Download what art is missing or changed, in the background: what Detect asks for when it has nothing to match.</summary>
    public void DownloadArt() => Launch(() => DownloadArtAsync(force: false));

    /// <summary>
    /// The first run's dialog: art and match data, each with what it costs. The match data comes from the
    /// shared download, else deadlock-api.com; when only the match data can't be had, it offers the art alone.
    /// With no connection there's nothing to offer yet, so it waits for <see cref="OnReconnected"/>, and it's
    /// only counted as offered once it's shown.
    /// </summary>
    private async Task OfferWelcomeAsync()
    {
        if (_connectivity.IsOffline)
            return;
        var shared = await _snapshots.PlanAsync(_data.Store);
        MatchFetchPlan? everyMatch = null;
        MatchFetchPlan? withRanks = null;
        if (shared is null)
        {
            try
            {
                var patches = await _matchStats.PatchesAsync();
                everyMatch = _matchStats.Plan(_data.Store, patches, includeRanks: false);
                withRanks = _matchStats.Plan(_data.Store, patches, includeRanks: true);
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
            }
        }
        if (_connectivity.IsOffline)
            return;
        // A second dialog would be dropped, and the offer with it.
        if (_modals.IsModalOpen)
        {
            OfferWelcomeAsChip();
            return;
        }
        _settings.Update(s => s.WelcomeOffered = true);
        _modals.ShowModal(new WelcomeViewModel(_modals, shared, everyMatch, withRanks, Estimate, _settings.Current.MatchDataIncludeRanks,
            _settings.Current.AutoUpdateMatchData, choice =>
        {
            _settings.Update(s =>
            {
                s.AutoUpdateMatchData = choice.KeepUpToDate;
                s.MatchDataIncludeRanks = choice.Ranks;
            });
            if (choice.Art)
                DownloadArt();
            if (choice.MatchData is { } plan)
                Launch(() => DownloadMatchDataAsync(plan));
        }));
    }

    /// <summary>The first run's offer as a chip in the status bar that opens the dialog, for when a dialog isn't the way to make it.</summary>
    private void OfferWelcomeAsChip()
    {
        var job = new BackgroundJobViewModel(WelcomeChipTitle, _clock);
        job.Succeed("available: art and match data", () => Launch(OfferWelcomeAsync), "Click to choose what to download");
        Show(job);
    }

    /// <summary>
    /// With match data and updates on: the shared download's manifest, or failing that one call to
    /// /v1/patches, either of which says whether a newer patch is out, and a quiet refresh when one is due
    /// (<see cref="MatchDownloadPlan.IsDue"/>). A check that fails says nothing: it isn't worth interrupting
    /// anyone over. Without, just the newer-patch check, if that's on.
    /// </summary>
    private async Task CheckMatchDataAsync()
    {
        var store = _data.Store;
        if (!_settings.Current.AutoUpdateMatchData || store.MatchSegments.Count == 0)
        {
            CheckForNewerPatch();
            return;
        }
        if (await _snapshots.PlanAsync(store) is { } shared)
        {
            Checked(s => s.MatchDataCheckedAt = _clock.Now);
            NewerPatch = MatchStatsMath.NewerPatch(store.MatchMeta, shared.Keep);
            if (shared.IsDue(store.MatchSegments))
                await DownloadMatchDataAsync(shared, quiet: true);
            return;
        }
        IReadOnlyList<Patch> patches;
        try
        {
            patches = await _matchStats.PatchesAsync();
        }
        catch (Exception ex) when (IsNetworkFailure(ex) || ex is IOException)
        {
            return;
        }
        Checked(s => s.MatchDataCheckedAt = _clock.Now);
        NewerPatch = MatchStatsMath.NewerPatch(store.MatchMeta, patches);
        var plan = _matchStats.Plan(store, patches, _settings.Current.MatchDataIncludeRanks);
        if (plan.IsDue(store.MatchSegments))
            await DownloadMatchDataAsync(plan, quiet: true);
    }

    private void CheckForNewerPatch()
    {
        if (_settings.Current.CheckForNewerPatch)
            _ = CheckForNewerPatchAsync();
    }

    /// <summary>One call to /v1/patches. Says nothing unless a newer patch is out: a failed check isn't worth interrupting anyone over.</summary>
    private async Task CheckForNewerPatchAsync()
    {
        try
        {
            NewerPatch = await _matchStats.NewerPatchAsync(_data.Store.MatchMeta);
            Checked(s => s.MatchDataCheckedAt = _clock.Now);
        }
        catch (Exception ex) when (IsNetworkFailure(ex) || ex is IOException)
        {
        }
    }

    // -- sync -----------------------------------------------------------------------

    /// <summary>Backfill score rows after adding heroes, items or categories to the CSVs by hand, and drop rows for ids that are gone.</summary>
    private void SyncNewData()
    {
        var store = _data.Store;
        var added = store.SyncCategories();
        var removed = store.PruneOrphans();
        try
        {
            if (added > 0 || removed > 0)
                store.SaveHeroScores();
            if (removed > 0)
            {
                store.SaveItemCoefficients();
                store.SaveTraitWeights();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.ShowError($"Writing to {_data.DataDir} failed: {ex.Message}", ex);
        }

        var lines = new List<string> { $"Added {added} missing hero × trait row(s), defaulted to 0." };
        if (removed > 0)
            lines.Add($"Dropped {removed} row(s) pointing at ids that no longer exist.");
        var empty = store.EmptyBaseTables();
        if (empty.Count > 0)
            lines.Add($"Nothing was dropped: {string.Join(" and ", empty)} has no rows, which looks like a damaged file; Reload from Disk restores it.");
        var unprofiled = store.UnprofiledHeroes();
        if (unprofiled.Count > 0)
            lines.AddRange(Listed($"\n{unprofiled.Count} hero(es) still have every trait at 0:", unprofiled.Select(id => store.Heroes[id].HeroName)));
        var uncovered = store.UncoveredItems();
        if (uncovered.Count > 0)
            lines.AddRange(Listed($"\n{uncovered.Count} item(s) have no formula rules and will score 0:", uncovered.Select(id => store.Items[id].ItemName)));
        else
            lines.Add("\nEvery item has at least one formula rule.");

        _data.NotifyReplaced();
        ShowMessage("Sync complete", lines);
    }

    private static IEnumerable<string> Listed(string heading, IEnumerable<string> names)
    {
        var all = names.ToList();
        yield return heading;
        foreach (var name in all.Take(12))
            yield return $"  • {name}";
        if (all.Count > 12)
            yield return $"  ...and {all.Count - 12} more";
    }

    private async Task SyncGameApiAsync()
    {
        _data.FlushSaves();
        var progress = new ProgressModalViewModel("Sync from Game API", "Fetching heroes, shop items and hero stats from deadlock-api.com…");
        var report = await RunBehindModalAsync(progress, () => _gameApi.SyncAsync(_data.Store, progress.Token), failure =>
        {
            if (IsNetworkFailure(failure))
            {
                ShowMessage("Sync failed", ["Couldn't reach deadlock-api.com:", "", failure.Message, "", "Nothing was changed."]);
                return;
            }
            // The store took the sync; only writing it out failed.
            _data.NotifyReplaced();
            ShowMessage("Sync failed", [$"Writing to {_data.DataDir} failed:", "", failure.Message]);
        });
        if (report is null)
            return;

        _data.NotifyReplaced();
        var lines = report.Lines();
        var coverage = _data.Store.Coverage();
        lines.Add($"\n{coverage.DerivedRules} coefficient(s) now come from item stats, via {_data.Store.StatRules.Count} line(s) in stat_rules.csv.");
        ShowMessage("Synced from game API", lines);
    }

    // -- match stats ----------------------------------------------------------------

    private MatchFetchEstimate Estimate =>
        _settings.Current is { MatchFetchSecondsPerCall: { } seconds, MatchFetchBytesPerCall: { } bytes }
            ? new MatchFetchEstimate(seconds, bytes)
            : MatchFetchEstimate.Measured;

    private Task OfferMatchDownloadAsync() => OfferMatchDownloadAsync(_settings.Current.MatchDataIncludeRanks);

    /// <summary>The download dialog, starting on the rank groups: what the filters ask for when there are none.</summary>
    public void OfferRankDownload() => Launch(() => OfferMatchDownloadAsync(includeRanks: true));

    /// <summary>
    /// The download dialog: what's stored, what a download would fetch, and about how long it takes. From
    /// the shared download when it's available, with the rank groups; otherwise from deadlock-api.com, with
    /// and without them, which needs the patch list first, one quick call.
    /// </summary>
    /// <param name="includeRanks">Which choice it starts on, for a download from deadlock-api.com.</param>
    internal async Task OfferMatchDownloadAsync(bool includeRanks)
    {
        if (await _snapshots.PlanAsync(_data.Store) is { } shared)
        {
            _modals.ShowModal(new MatchDownloadViewModel(_modals, _data.Store.MatchSegments, shared, includeRanks,
                _settings.Current.AutoUpdateMatchData, StartMatchDownload));
            return;
        }
        IReadOnlyList<Patch> patches;
        try
        {
            patches = await _matchStats.PatchesAsync();
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            _notifications.ShowError(_connectivity.IsOffline
                ? "You're offline. The patch list comes from deadlock-api.com, so the match data waits for a connection."
                : $"Couldn't reach deadlock-api.com for the patch list: {ex.Message}", _toastTime);
            return;
        }
        var store = _data.Store;
        _modals.ShowModal(new MatchDownloadViewModel(_modals, store.MatchSegments,
            _matchStats.Plan(store, patches, includeRanks: false), _matchStats.Plan(store, patches, includeRanks: true),
            Estimate, includeRanks, _settings.Current.AutoUpdateMatchData, StartMatchDownload));
    }

    private void StartMatchDownload(MatchDownloadChoice choice)
    {
        _settings.Update(s =>
        {
            s.MatchDataIncludeRanks = choice.Ranks;
            s.AutoUpdateMatchData = choice.KeepUpToDate;
        });
        Launch(() => DownloadMatchDataAsync(choice.Plan));
    }

    /// <summary>
    /// In the background: seconds from the shared download, a few minutes of calls from deadlock-api.com, with
    /// its phases behind the chip. Each patch's counts are put to use as they arrive, so stopping part-way
    /// keeps what's finished.
    /// </summary>
    /// <param name="quiet">An update nobody asked for: a failure goes without a word.</param>
    internal async Task DownloadMatchDataAsync(MatchDownloadPlan plan, bool quiet = false)
    {
        if (IsDownloadingMatchData)
            return;
        _data.FlushSaves();
        var details = plan is MatchFetchPlan calls ? new MatchDownloadProgressViewModel(calls) : null;
        var source = plan is SnapshotPlan ? "the shared download" : "deadlock-api.com";
        var job = new BackgroundJobViewModel("Match data", _clock, cancel => _modals.Confirm(
            "Stop downloading match data? What has finished is kept and already in use; the rest stays as it was.",
            "Stop", cancel, cancelText: "Keep going")) { Details = details };
        IsDownloadingMatchData = true;
        FetchResult? applied = null;
        string Kept() => applied is null ? "Nothing was changed." : "What finished before that is kept.";
        try
        {
            var started = _clock.Now;
            var done = await RunInBackgroundAsync(job, async () =>
                {
                    void Finished(MatchSegment segment) => applied = ApplySegment(segment, plan);
                    await (plan switch
                    {
                        SnapshotPlan shared => _snapshots.FetchAsync(shared, job, Finished, job.Token),
                        MatchFetchPlan calls => _matchStats.FetchAsync(_data.Store, calls, new BothProgress(details!, job), Finished, job.Token),
                        _ => throw new ArgumentOutOfRangeException(nameof(plan)),
                    });
                    return plan;
                },
                failure =>
                {
                    if (quiet)
                        Remove(job);
                    else
                        Failed(job, "Match data download failed", failure is IOException or UnauthorizedAccessException
                            ? [$"Writing to {_data.DataDir} failed:", "", failure.Message]
                            : [$"Couldn't download match data from {source}:", "", failure.Message, "", Kept()]);
                },
                () => $"Stopped downloading match data. {Kept()}");
            if (done is null)
                return;

            if (details is not null)
            {
                var learned = Estimate.Learn(details.Done, _clock.Now - started, details.Bytes);
                _settings.Update(s =>
                {
                    s.MatchFetchSecondsPerCall = learned.SecondsPerCall;
                    s.MatchFetchBytesPerCall = learned.BytesPerCall;
                });
            }
            var lines = applied?.Lines() ?? ["Every patch's counts were already complete: nothing to download."];
            lines.Add("\nShown beside each recommendation as \"data\" — a second opinion, not part of the score.");
            lines.Add(plan.IncludesRanks || MatchStatsMath.RanksOf(_data.Store.MatchSegments).Count > 0
                ? "The Match page's filters (the funnel) lean it toward a range of ranks, without downloading again."
                : "Download the rank groups too for the Match page's filters to lean it toward your ranks.");
            Succeeded(job, quiet ? "updated" : "downloaded", quiet ? "Match data updated" : "Match data downloaded", lines,
                quiet ? $"Match data updated to patch {plan.Keep[0].Label}." : "Match data downloaded: the recommendations now show it.");
        }
        finally
        {
            IsDownloadingMatchData = false;
        }
    }

    private FetchResult ApplySegment(MatchSegment segment, MatchDownloadPlan plan)
    {
        var result = _matchStats.Apply(_data.Store, segment, plan.Keep);
        NewerPatch = null;
        _data.NotifyReplaced();
        return result;
    }

    /// <summary>The details card follows every phase; the chip shows the whole download.</summary>
    private sealed class BothProgress(IProgress<MatchFetchProgress> details, IProgress<FetchProgress> job) : IProgress<MatchFetchProgress>
    {
        public void Report(MatchFetchProgress value)
        {
            details.Report(value);
            job.Report(value.Overall);
        }
    }
    // -- formulas -------------------------------------------------------------------

    /// <summary>
    /// The published model against this data folder's: files unchanged here update quietly, and ones
    /// changed here are asked about. On startup, a failed check or a version already turned down says nothing.
    /// </summary>
    /// <param name="manual">From the Data menu: says how it went, and asks again about files kept over this version.</param>
    internal async Task CheckModelAsync(bool manual)
    {
        var answer = await _models.PublishedAsync();
        if (answer.Manifest is not { } published)
        {
            if (manual)
                ShowMessage("Formula update", [Unavailable(answer)]);
            return;
        }
        Checked(s => s.ModelCheckedAt = _clock.Now);
        if (!_data.FlushSaves())
            return;
        var dataDir = _data.DataDir;
        var added = await TakeNewHeroesAsync(published, dataDir, askAgain: manual);
        var update = ModelUpdatePlan.For(published, dataDir, askAgain: manual);
        if (update.Edited.Count > 0)
        {
            if (added.Count > 0)
                ShowNewHeroes(update, added, withNews: false);
            _modals.ShowModal(ModelUpdateViewModel.Update(_modals, update, replace => Launch(() => InstallModelAsync(dataDir, update, replace, manual: true))));
        }
        else if (update.Quiet.Count > 0)
        {
            await InstallModelAsync(dataDir, update, new HashSet<string>(), manual);
        }
        else
        {
            WriteModelRecord(dataDir, update);
            if (added.Count > 0)
                ShowNewHeroes(update, added, withNews: true);
            else if (manual)
                ShowMessage("Formula update", [$"The hero ratings and item formulas are up to date: the version published {published.Published}."]);
        }
    }

    /// <summary>
    /// Adds the heroes the published model has and this data folder lacks to the hero files its owner has changed,
    /// which would otherwise ask or be left out (<see cref="ModelUpdateService.AddNewHeroes"/>): a hero the game
    /// releases is never worth a question. A failure to fetch or write changes nothing and says nothing: the update
    /// goes on as it would have.
    /// </summary>
    /// <returns>The heroes added to heroes.csv, as the published model names them.</returns>
    private async Task<IReadOnlyList<string>> TakeNewHeroesAsync(ModelManifest published, string dataDir, bool askAgain)
    {
        var files = ModelUpdateService.HeroFilesToMerge(published, dataDir, askAgain);
        if (files.Count == 0)
            return [];
        HeroMerge merged;
        try
        {
            merged = ModelUpdateService.AddNewHeroes(dataDir, files, await _models.DownloadAsync(published, ModelUpdateService.HeroFiles));
        }
        catch (Exception ex) when (IsNetworkFailure(ex) || ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"Formula update: couldn't add the new heroes\n{ex}");
            return [];
        }
        if (merged.Written.Count == 0)
            return [];
        _data.Reload();
        if (merged.Added.Count > 0 && HeroesMissingArt())
            Launch(() => DownloadArtAsync(force: false, quiet: true));
        return merged.Added;
    }

    /// <summary>
    /// With model updates off: the heroes the published model has and this folder lacks, offered as a chip in the
    /// status bar that adds them, and their art, with a click. A model that can't be reached says nothing, and a
    /// chip that's closed is offered again at the next start.
    /// </summary>
    private async Task OfferNewHeroesAsync()
    {
        if ((await _models.PublishedAsync()).Manifest is not { } published)
            return;
        IReadOnlyDictionary<string, byte[]> files;
        try
        {
            files = await _models.DownloadAsync(published, ModelUpdateService.HeroFiles);
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            return;
        }
        var names = ModelUpdateService.NewHeroNames(_data.DataDir, files);
        if (names.Count == 0)
            return;
        var job = new BackgroundJobViewModel("New heroes", _clock);
        job.Succeed(string.Join(", ", names), () => AddNewHeroes(files), "Click to add them, and their art");
        Show(job);
    }

    private void AddNewHeroes(IReadOnlyDictionary<string, byte[]> published)
    {
        if (!_data.FlushSaves())
            return;
        HeroMerge merged;
        try
        {
            merged = ModelUpdateService.AddNewHeroes(_data.DataDir, ModelUpdateService.HeroFiles, published);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.ShowError($"Writing to {_data.DataDir} failed: {ex.Message}", ex);
            return;
        }
        if (merged.Written.Count == 0)
            return;
        _data.Reload();
        _notifications.ShowSuccess($"Added {string.Join(", ", merged.Added)} from the published heroes.", _toastTime);
        if (HeroesMissingArt())
            Launch(() => DownloadArtAsync(force: false, quiet: true));
    }

    /// <summary>Says heroes were added, when no install is about to say what the update changed.</summary>
    private void ShowNewHeroes(ModelUpdatePlan update, IReadOnlyList<string> added, bool withNews)
    {
        var news = withNews ? update.News : [];
        _notifications.ShowSuccess(
            news.Count > 0
                ? $"Formulas updated: {string.Join(" ", news.Select(note => note.Text))}"
                : $"Added {string.Join(", ", added)} from the published heroes.",
            news.Count > 0 ? _newsToastTime : _toastTime);
    }

    private const string Unreachable = "Couldn't reach GitHub for the published hero ratings and item formulas. Try again later.";

    private const string NeedsNewerApp =
        "The hero ratings and item formulas published on GitHub need a newer version of this app than the one you have. "
        + "Settings → Data checks for a new version; then check for formula updates again.";

    private static string Unavailable(PublishedModel answer) => answer.NeedsNewerApp ? NeedsNewerApp : Unreachable;

    /// <summary>The files that differ from the published model, ticked, to put back to it.</summary>
    private async Task ResetModelAsync()
    {
        var answer = await _models.PublishedAsync();
        if (answer.Manifest is not { } published)
        {
            ShowMessage("Reset formulas", [Unavailable(answer)]);
            return;
        }
        Checked(s => s.ModelCheckedAt = _clock.Now);
        if (!_data.FlushSaves())
            return;
        var dataDir = _data.DataDir;
        var reset = ModelUpdatePlan.Reset(published, dataDir);
        if (!reset.HasWork)
        {
            ShowMessage("Reset formulas", [$"Every file already matches the version published {published.Published}: there's nothing to reset."]);
            return;
        }
        _modals.ShowModal(ModelUpdateViewModel.Reset(_modals, reset,
            replace => Launch(() => InstallModelAsync(dataDir, reset, replace, manual: true, isReset: true))));
    }

    private void OfferModelUndo()
    {
        var undoable = ModelUpdateService.Undoable(_data.DataDir);
        if (undoable.Count == 0)
        {
            ShowMessage("Undo formula update", ["There's no formula update to undo: nothing since the last one has been left as it put it."]);
            return;
        }
        _modals.Confirm(
            $"Put back the {Listing(undoable)} the last formula update or reset replaced?\n\n"
            + "Newer formulas that are published later still ask before replacing them. Every file keeps a backup in data\\.backups.",
            "Undo update", UndoModelUpdate);
    }

    private void UndoModelUpdate()
    {
        if (!_data.FlushSaves())
            return;
        IReadOnlyList<string> restored;
        try
        {
            restored = ModelUpdateService.Undo(_data.DataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.ShowError($"Writing to {_data.DataDir} failed: {ex.Message}", ex);
            return;
        }
        if (restored.Count == 0)
            return;
        _data.Reload();
        _notifications.ShowSuccess($"Put back the {Listing(restored)} from before the last formula update or reset.", _toastTime);
    }

    private void ShowModelNotes()
    {
        var installed = ModelManifest.Installed(_data.DataDir);
        IEnumerable<string> lines = installed is null ? ["This data folder has no record of a published version."]
            : installed.Notes.Count == 0 ? [$"Installed: the version published {installed.Published}. It came without notes."]
            : [$"Installed: the version published {installed.Published}.", "", .. installed.Notes.Select(note => $"{note.Published}: {note.Text}")];
        ShowMessage("What's new in the formulas", lines);
    }

    /// <summary>"item formulas and trait weights".</summary>
    private static string Listing(IReadOnlyList<string> files)
    {
        var titles = files.Select(file => ModelManifest.Title(file).ToLowerInvariant()).ToList();
        return titles.Count == 1 ? titles[0] : $"{string.Join(", ", titles[..^1])} and {titles[^1]}";
    }

    /// <summary>Download the files to replace, then write them in one go, so no edit can land in between.</summary>
    /// <param name="isReset">Putting files back to the published version, rather than taking a newer one.</param>
    private async Task InstallModelAsync(string dataDir, ModelUpdatePlan update, IReadOnlySet<string> replace, bool manual, bool isReset = false)
    {
        IReadOnlyDictionary<string, byte[]> downloaded;
        try
        {
            downloaded = await _models.DownloadAsync(update.Published, update.Quiet.Concat(update.Edited.Where(replace.Contains)));
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            _log.Warning($"Formula update: failed\n{ex}");
            if (manual)
                _notifications.ShowError($"Couldn't download the formula update: {ex.Message}", _toastTime);
            return;
        }
        // The data folder changed, or an edit couldn't be saved: writing now would lose something.
        if (_data.DataDir != dataDir || !_data.FlushSaves())
            return;

        IReadOnlySet<string> written;
        try
        {
            written = ModelUpdateService.Install(dataDir, update, downloaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.ShowError($"Writing to {dataDir} failed: {ex.Message}", ex);
            return;
        }
        if (written.Count == 0)
            return;
        _data.Reload();
        // The startup's art check ran before this update brought the heroes, so a new one would wait for the next.
        if (written.Contains(DataStore.HeroesFile) && HeroesMissingArt())
            Launch(() => DownloadArtAsync(force: false, quiet: true));
        if (isReset)
        {
            _notifications.ShowSuccess($"Reset the {Listing(ModelManifest.ModelFiles.Where(written.Contains).ToList())} to the version published {update.Published.Published}. "
                                       + "Settings → Data can undo it.", _toastTime);
            return;
        }
        // What the publisher said changed comes first, with longer to read it.
        var news = update.News;
        _notifications.ShowSuccess(
            news.Count > 0
                ? $"Formulas updated: {string.Join(" ", news.Select(note => note.Text))} The old files are in data\\.backups."
                : $"Updated to the formulas published {update.Published.Published}: {string.Join(", ", written.Select(ModelManifest.Title))}. "
                  + "The old files are in data\\.backups.",
            news.Count > 0 ? _newsToastTime : _toastTime);
    }

    private void WriteModelRecord(string dataDir, ModelUpdatePlan update)
    {
        try
        {
            ModelUpdateService.Record(dataDir, update.Record(new HashSet<string>()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"Formula update: couldn't write the record\n{ex}");
        }
    }

    /// <summary>Note when a check got its answer, for Settings → Data.</summary>
    private void Checked(Action<AppSettings> stamp) => _settings.Update(stamp);

    // -- model health ---------------------------------------------------------------

    /// <summary>The simulation takes a moment, so it runs off the UI thread; busy meanwhile so no sync swaps the data under it.</summary>
    private async Task ShowModelHealthAsync()
    {
        IsBusy = true;
        ModelHealthReport report;
        try
        {
            var (store, matrix) = (_data.Store, _data.Matrix);
            report = await Task.Run(() => ModelHealth.Build(store, matrix));
        }
        finally
        {
            IsBusy = false;
        }
        ShowMessage("Model health", report.Lines());
    }

    // -- running jobs ---------------------------------------------------------------

    /// <summary>Stop every download without asking, as the window closes.</summary>
    public void CancelJobs()
    {
        foreach (var job in Jobs.ToList())
            job.Cancel();
    }

    /// <summary>
    /// Run a network job behind <paramref name="progress"/>. Null when it was cancelled or failed; a
    /// failure has already been reported through <paramref name="failed"/>.
    /// </summary>
    private async Task<T?> RunBehindModalAsync<T>(ProgressModalViewModel progress, Func<Task<T>> job, Action<Exception> failed)
        where T : class
    {
        IsBusy = true;
        _modals.ShowModal(progress);
        Exception? failure = null;
        T? result;
        try
        {
            result = await RunAsync(progress.Title, job, ex => failure = ex);
        }
        finally
        {
            _modals.CloseModal();
            progress.Dispose();
            IsBusy = false;
        }
        if (failure is not null)
            failed(failure);
        return result;
    }

    /// <summary>
    /// Run a network job in the background, with <paramref name="job"/> showing it in the status bar.
    /// Null when it was cancelled, which takes it out of the status bar and says so, or failed, which
    /// has already been reported through <paramref name="failed"/>.
    /// </summary>
    private async Task<T?> RunInBackgroundAsync<T>(BackgroundJobViewModel job, Func<Task<T>> work, Action<Exception> failed,
        Func<string> cancelledText) where T : class
    {
        Show(job);
        var result = await RunAsync(job.Title, work, failed);
        if (!job.Token.IsCancellationRequested)
            return result;
        // Even an answer that arrived just as it was stopped is dropped, as asked.
        Remove(job);
        _notifications.ShowInformation(cancelledText(), _toastTime);
        return null;
    }

    /// <summary>A job's own work, logged. Null when it was cancelled or failed; a failure is passed to <paramref name="failed"/>.</summary>
    private async Task<T?> RunAsync<T>(string title, Func<Task<T>> job, Action<Exception> failed) where T : class
    {
        _log.Information($"{title}: started");
        try
        {
            var result = await job();
            _log.Information($"{title}: finished");
            return result;
        }
        catch (OperationCanceledException)
        {
            _log.Information($"{title}: cancelled");
        }
        catch (Exception ex) when (IsNetworkFailure(ex) || ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"{title}: failed\n{ex}");
            failed(ex);
        }
        return null;
    }

    /// <summary>Into the status bar, in place of the last run's report if that's still there.</summary>
    private void Show(BackgroundJobViewModel job)
    {
        foreach (var previous in Jobs.Where(previous => previous.Title == job.Title).ToList())
            Remove(previous);
        job.Dismissed.Take(1).Subscribe(_ => Remove(job));
        Jobs.Add(job);
    }

    private void Remove(BackgroundJobViewModel job)
    {
        if (Jobs.Remove(job))
            job.Dispose();
    }

    private void Succeeded(BackgroundJobViewModel job, string status, string title, IReadOnlyList<string> report, string toast)
    {
        job.Succeed(status, () => ShowMessage(title, report));
        _notifications.ShowSuccess(toast, _toastTime);
    }

    private void Failed(BackgroundJobViewModel job, string title, IReadOnlyList<string> report)
    {
        job.Fail(() => ShowMessage(title, report));
        _notifications.ShowError($"{title}. The status bar has the details.", _toastTime);
    }

    /// <summary>Start a job from a modal's button, reporting anything unexpected rather than losing it with the task.</summary>
    private async void Launch(Func<Task> job)
    {
        try
        {
            await job();
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Something went wrong: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// What the downloads throw when the site is down, slow or answering nonsense, the answer has no patch
    /// dates, or a shared file didn't arrive intact.
    /// </summary>
    private static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException or TimeoutException or JsonException or InvalidOperationException or InvalidDataException or FormatException;

    // -- art ------------------------------------------------------------------------

    private void OfferArtDownload() =>
        _modals.Confirm(
            $"Fetch hero portraits, item icons and the top-bar art that Detect from screen matches against, from deadlock-api.com into {_data.AssetsDir}?\n\n"
            + "Files already there are kept, apart from art downloaded here that deadlock-api.com has changed since, unless you re-download everything. "
            + "It downloads in the background, and art shows up as it arrives.",
            "Download new and changed", () => Launch(() => DownloadArtAsync(force: false)),
            "Re-download all", () => Launch(() => DownloadArtAsync(force: true)));

    /// <param name="quiet">A routine check: it only speaks up if something changed.</param>
    internal async Task DownloadArtAsync(bool force, bool quiet = false)
    {
        if (IsDownloadingArt)
            return;
        var job = new BackgroundJobViewModel("Art", _clock);
        var shown = _clock.Now;
        using var showAsItArrives = job.WhenAnyValue(vm => vm.Done)
            .Where(_ => _clock.Now - shown >= _artShowGap)
            .Subscribe(_ =>
            {
                shown = _clock.Now;
                ShowNewArt();
            });
        IsDownloadingArt = true;
        try
        {
            var report = await RunInBackgroundAsync(job,
                () => _artDownload.DownloadAsync(_data.Store, _data.AssetsDir, force, job, job.Token),
                failure =>
                {
                    // Like the patch check, a routine check that can't reach the site isn't worth interrupting anyone over.
                    if (quiet)
                        Remove(job);
                    else
                        Failed(job, "Art download failed",
                            ["Couldn't download the art from deadlock-api.com:", "", failure.Message, "", "Whatever arrived before that is kept."]);
                },
                () => "Stopped downloading art. What arrived is kept.");
            if (report is not null)
            {
                _settings.Update(s => s.ArtCheckedAt = _clock.Now);
                var changes = string.Join(", ", new[] { (report.Downloaded, "downloaded"), (report.Updated, "updated") }
                    .Where(count => count.Item1 > 0)
                    .Select(count => $"{count.Item1} {count.Item2}"));
                var recut = report.Derivation?.Derived ?? [];
                if (quiet && changes.Length == 0 && recut.Count == 0)
                {
                    Remove(job);
                    return;
                }
                var toast = quiet && recut.Count > 0
                    ? $"Updated the art Detect from screen matches against for {Names(recut)}."
                    : $"Art downloaded: {report.Downloaded} new file(s), {report.Updated} updated.";
                Succeeded(job, changes.Length == 0 ? "nothing new" : changes, "Art downloaded", report.Lines(), toast);
            }
        }
        finally
        {
            IsDownloadingArt = false;
            // Whatever arrived before a cancel or failure is on disk too.
            ShowNewArt();
        }
    }

    private string Names(IEnumerable<string> heroIds) =>
        string.Join(", ", heroIds.Select(id => _data.Store.Heroes.TryGetValue(id, out var hero) ? hero.HeroName : id));

    private void ShowNewArt()
    {
        _art.Refresh();
        RequestViewAction(ArtChangedAction);
    }

    // -- files ----------------------------------------------------------------------

    private void Reload()
    {
        try
        {
            _data.Reload();
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Reload failed: {ex.Message}", ex);
        }
    }

    /// <summary>A read-only workbook of the CSVs, for backup or review. Never read back in.</summary>
    private void Export()
    {
        _data.FlushSaves();
        var path = ExportPath;
        IReadOnlyList<(string Sheet, int Rows)> counts;
        try
        {
            counts = _excel.Export(_data.DataDir, path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && ex.HResult == unchecked((int)0x80070020))
        {
            ShowMessage("Export failed", [$"{Path.GetFileName(path)} is open in Excel. Close it and try again."]);
            return;
        }
        catch (Exception ex)
        {
            ShowMessage("Export failed", [ex.Message]);
            return;
        }

        var lines = new List<string> { $"Wrote {Path.GetFileName(path)}:", "" };
        lines.AddRange(counts.Select(count => $"  {count.Sheet}: {count.Rows} rows"));
        lines.AddRange(["", "This is a read-only snapshot. Edits belong in the app —", "the workbook is never read back in."]);
        ShowMessage("Exported", lines);
    }

    /// <summary>
    /// Point the app at another folder holding data/ and assets/, e.g. a synced folder shared between
    /// machines. Nothing locks the files, so don't edit them from two places at once.
    /// </summary>
    private async Task ChangeDataFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync("Choose the data folder (the one holding data/ and assets/)", _data.DataRoot);
        if (folder is null)
            return;

        try
        {
            _data.ChangeDataRoot(folder);
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Couldn't use {folder}: {ex.Message}", ex);
            return;
        }

        _art.SetAssetsDir(_data.AssetsDir);
        RequestViewAction(ArtChangedAction);
        NewerPatch = null;
        CheckForNewerPatch();
        _notifications.ShowSuccess($"Now using the data in {_data.DataRoot}.");
    }

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Couldn't open {path}: {ex.Message}", ex);
        }
    }

    // -- modals ---------------------------------------------------------------------

    private void ShowMessage(string title, IEnumerable<string> lines) => _modals.ShowMessage(title, string.Join("\n", lines));
}
