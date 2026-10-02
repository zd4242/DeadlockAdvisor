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
using DeadlockAdvisor.Features.Shared.BackgroundJobs;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Modals.Progress;
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

    // Showing new art re-reads every image on screen, so it's done this often at most while art arrives.
    private static readonly TimeSpan _artShowGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _toastTime = TimeSpan.FromSeconds(5);

    private readonly IScheduler _clock;
    private readonly IDataService _data;
    private readonly IGameApiService _gameApi;
    private readonly IMatchStatsService _matchStats;
    private readonly IExcelExportService _excel;
    private readonly IArtDownloadService _artDownload;
    private readonly IArtService _art;
    private readonly IModalService _modals;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IFilePickerService _filePicker;
    private readonly ILoggingService _log;

    public DataMenuViewModel(IDataService data, IGameApiService gameApi, IMatchStatsService matchStats,
        IExcelExportService excel, IArtDownloadService artDownload, IArtService art, IModalService modals,
        INotificationService notifications, ISettingsService settings, IFilePickerService filePicker, ILoggingService log)
        : this(data, gameApi, matchStats, excel, artDownload, art, modals, notifications, settings, filePicker, log, Scheduler.Default)
    {
    }

    internal DataMenuViewModel(IDataService data, IGameApiService gameApi, IMatchStatsService matchStats,
        IExcelExportService excel, IArtDownloadService artDownload, IArtService art, IModalService modals,
        INotificationService notifications, ISettingsService settings, IFilePickerService filePicker, ILoggingService log,
        IScheduler clock)
    {
        _clock = clock;
        _log = log;
        _data = data;
        _gameApi = gameApi;
        _matchStats = matchStats;
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
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDataFolderCommand { get; }

    /// <summary>Show a folder in Explorer.</summary>
    public ReactiveCommand<string, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeDataFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadArtCommand { get; }

    public string ExportPath => Path.Combine(_data.DataRoot, ExcelExportService.FileName);

    /// <summary>How often the art is checked against deadlock-api.com's, which costs one "not modified" per file.</summary>
    public static readonly TimeSpan ArtCheckInterval = TimeSpan.FromDays(7);

    private string TopbarDir => Path.Combine(_data.AssetsDir, "topbar");

    /// <summary>
    /// Once the window is up: check for a newer patch in the background, offer art on a first run
    /// without any, and otherwise keep the top-bar art detection matches against current: quietly,
    /// about weekly, and at once when this version cuts portraits differently from the last.
    /// </summary>
    public void OnStartup()
    {
        CheckForNewerPatch();
        if (!_settings.Current.ArtDownloadOffered && _art.Count(ArtKind.Hero) == 0 && _art.Count(ArtKind.Item) == 0)
        {
            _settings.Update(s => s.ArtDownloadOffered = true);
            _modals.Confirm(
                $"There's no hero or item art in {_data.AssetsDir} yet, so heroes and items show as initials tiles.\n\n"
                + "Download the portraits and icons from deadlock-api.com now? It's about 22 MB and downloads in the background, "
                + "and Data → Download Art… does it any time.",
                "Download", DownloadArt, cancelText: "Not now");
            return;
        }
        var hasTopbarArt = Directory.Exists(TopbarDir)
                           && Directory.EnumerateFiles(TopbarDir).Any(file => ImageFile.Suffixes.Contains(Path.GetExtension(file).ToLowerInvariant()));
        var due = _settings.Current.ArtCheckedAt is not { } checkedAt || _clock.Now - checkedAt >= ArtCheckInterval;
        if (hasTopbarArt && (due || !TopbarDerivation.IsCurrent(TopbarDir)))
            Launch(() => DownloadArtAsync(force: false, quiet: true));
    }

    /// <summary>Download what art is missing or changed, in the background: what Detect asks for when it has nothing to match.</summary>
    public void DownloadArt() => Launch(() => DownloadArtAsync(force: false));

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
        var progress = new ProgressModalViewModel("Sync from Game API", "Fetching heroes and shop items from deadlock-api.com…");
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
    /// The download dialog: what's stored, what a download would fetch with and without the rank groups,
    /// and about how long each takes. Needs the patch list first, one quick call.
    /// </summary>
    /// <param name="includeRanks">Which choice it starts on.</param>
    internal async Task OfferMatchDownloadAsync(bool includeRanks)
    {
        IReadOnlyList<Patch> patches;
        try
        {
            patches = await _matchStats.PatchesAsync();
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            _notifications.ShowError($"Couldn't reach deadlock-api.com for the patch list: {ex.Message}", _toastTime);
            return;
        }
        var store = _data.Store;
        _modals.ShowModal(new MatchDownloadViewModel(_modals, store.MatchSegments,
            _matchStats.Plan(store, patches, includeRanks: false), _matchStats.Plan(store, patches, includeRanks: true),
            Estimate, includeRanks, (plan, ranks) =>
            {
                _settings.Update(s => s.MatchDataIncludeRanks = ranks);
                Launch(() => DownloadMatchDataAsync(plan));
            }));
    }

    /// <summary>
    /// A few minutes of calls, so it runs in the background, with its phases behind the chip. Each phase's
    /// counts are put to use as they arrive, so stopping part-way keeps what's finished.
    /// </summary>
    internal async Task DownloadMatchDataAsync(MatchFetchPlan plan)
    {
        if (IsDownloadingMatchData)
            return;
        _data.FlushSaves();
        var details = new MatchDownloadProgressViewModel(plan);
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
                    await _matchStats.FetchAsync(_data.Store, plan, new BothProgress(details, job),
                        segment => applied = ApplySegment(segment, plan), job.Token);
                    return plan;
                },
                failure => Failed(job, "Match data download failed", failure is IOException or UnauthorizedAccessException
                    ? [$"Writing to {_data.DataDir} failed:", "", failure.Message]
                    : ["Couldn't download match data from deadlock-api.com:", "", failure.Message, "", Kept()]),
                () => $"Stopped downloading match data. {Kept()}");
            if (done is null)
                return;

            var learned = Estimate.Learn(details.Done, _clock.Now - started, details.Bytes);
            _settings.Update(s =>
            {
                s.MatchFetchSecondsPerCall = learned.SecondsPerCall;
                s.MatchFetchBytesPerCall = learned.BytesPerCall;
            });
            var lines = applied?.Lines() ?? ["Every patch's counts were already complete: nothing to download."];
            lines.Add("\nShown beside each recommendation as \"data\" — a second opinion, not part of the score.");
            lines.Add(plan.IncludesRanks || MatchStatsMath.RanksOf(_data.Store.MatchSegments).Count > 0
                ? "The Match page's filters (the funnel) lean it toward a range of ranks, without downloading again."
                : "Download the rank groups too for the Match page's filters to lean it toward your ranks.");
            Succeeded(job, "downloaded", "Match data downloaded", lines, "Match data downloaded: the recommendations now show it.");
        }
        finally
        {
            IsDownloadingMatchData = false;
        }
    }

    private FetchResult ApplySegment(MatchSegment segment, MatchFetchPlan plan)
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

    /// <summary>What the API calls throw when the site is down, slow or answering nonsense, or the answer has no patch dates.</summary>
    private static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException or TimeoutException or JsonException or InvalidOperationException;

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
    /// Point the app at another folder holding data/ and assets/, e.g. the Python app's repo so both
    /// apps share one set of files. Nothing locks them, so don't edit in both apps at once.
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
