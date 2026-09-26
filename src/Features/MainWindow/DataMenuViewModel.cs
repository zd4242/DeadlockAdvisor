using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Modals.Progress;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// The Data menu: syncing with the game, fetching match stats, reloading, exporting, and where the
/// data and art live. Every network job flushes pending edits first and runs behind a progress modal
/// that can cancel it; nothing is written until a job has everything it needs.
/// </summary>
public class DataMenuViewModel : ViewModelBase
{
    public const string ArtChangedAction = "ArtChanged";

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
    {
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
        FetchMatchStatsCommand = ReactiveCommand.CreateFromTask(FetchMatchStatsAsync, idle);
        ModelHealthCommand = ReactiveCommand.CreateFromTask(ShowModelHealthAsync, idle);
        ReloadCommand = ReactiveCommand.Create(Reload);
        ExportCommand = ReactiveCommand.Create(Export);
        OpenDataFolderCommand = ReactiveCommand.Create(() => OpenFolder(_data.DataDir));
        ChangeDataFolderCommand = ReactiveCommand.CreateFromTask(ChangeDataFolderAsync, idle);
        DownloadArtCommand = ReactiveCommand.Create(OfferArtDownload, idle);
    }

    /// <summary>A network job is running; one at a time.</summary>
    [Reactive] public bool IsBusy { get; private set; }

    /// <summary>Set when a patch is out that the match data predates.</summary>
    [Reactive] public Patch? NewerPatch { get; private set; }

    public ReactiveCommand<Unit, Unit> SyncNewDataCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncGameApiCommand { get; }
    public ReactiveCommand<Unit, Unit> FetchMatchStatsCommand { get; }
    public ReactiveCommand<Unit, Unit> ModelHealthCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDataFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeDataFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadArtCommand { get; }

    public string ExportPath => Path.Combine(_data.DataRoot, ExcelExportService.FileName);

    /// <summary>Once the window is up: check for a newer patch in the background, and offer art on a first run without any.</summary>
    public void OnStartup()
    {
        _ = CheckForNewerPatchAsync();
        if (_settings.Current.ArtDownloadOffered || _art.Count(ArtKind.Hero) > 0 || _art.Count(ArtKind.Item) > 0)
            return;
        _settings.Update(s => s.ArtDownloadOffered = true);
        Confirm(
            $"There's no hero or item art in {_data.AssetsDir} yet, so heroes and items show as initials tiles.\n\n"
            + "Download the portraits and icons from deadlock-api.com now? It's about 13 MB, and Data → Download Art… does it any time.",
            "Download", () => Launch(() => DownloadArtAsync(force: false)), cancelText: "Not now");
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
        var report = await RunAsync(progress, () => _gameApi.SyncAsync(_data.Store, progress.Token), failure =>
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

    private async Task FetchMatchStatsAsync()
    {
        _data.FlushSaves();
        var progress = new ProgressModalViewModel("Fetch Match Stats", "Fetching match stats from deadlock-api.com… (a few minutes)");
        var result = await RunAsync(progress, () => _matchStats.FetchAsync(_data.Store, progress, progress.Token),
            failure => ShowMessage("Fetch failed", ["Couldn't fetch match stats from deadlock-api.com:", "", failure.Message, "", "Nothing was changed."]));
        if (result is null)
            return;

        try
        {
            _matchStats.Apply(_data.Store, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Could not save match stats", [$"Writing to {_data.DataDir} failed:", "", ex.Message]);
            return;
        }
        NewerPatch = null;
        _data.NotifyReplaced();
        var lines = result.Lines();
        lines.Add("\nShown beside each recommendation as \"data\" — a second opinion, not part of the score.");
        ShowMessage("Match stats fetched", lines);
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

    /// <summary>
    /// Run a network job behind <paramref name="progress"/>. Null when it was cancelled or failed; a
    /// failure has already been reported through <paramref name="failed"/>.
    /// </summary>
    private async Task<T?> RunAsync<T>(ProgressModalViewModel progress, Func<Task<T>> job, Action<Exception> failed) where T : class
    {
        _log.Information($"{progress.Title}: started");
        IsBusy = true;
        _modals.ShowModal(progress);
        T? result = null;
        Exception? failure = null;
        try
        {
            result = await job();
            _log.Information($"{progress.Title}: finished");
        }
        catch (OperationCanceledException)
        {
            _log.Information($"{progress.Title}: cancelled");
        }
        catch (Exception ex) when (IsNetworkFailure(ex) || ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"{progress.Title}: failed\n{ex}");
            failure = ex;
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
        Confirm(
            $"Fetch hero portraits, item icons and the top-bar art that Detect from screen matches against, from deadlock-api.com into {_data.AssetsDir}?\n\n"
            + "Files already there are kept unless you re-download everything.",
            "Download missing", () => Launch(() => DownloadArtAsync(force: false)),
            "Re-download all", () => Launch(() => DownloadArtAsync(force: true)));

    private async Task DownloadArtAsync(bool force)
    {
        var progress = new ProgressModalViewModel("Download Art", "Downloading art from deadlock-api.com…");
        var report = await RunAsync(progress, () => _artDownload.DownloadAsync(_data.Store, _data.AssetsDir, force, progress, progress.Token),
            failure => ShowMessage("Download failed", ["Couldn't download the art from deadlock-api.com:", "", failure.Message]));
        // Whatever arrived before a cancel or failure is on disk: show it.
        _art.Refresh();
        RequestViewAction(ArtChangedAction);
        if (report is not null)
            ShowMessage("Art downloaded", report.Lines());
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
        _ = CheckForNewerPatchAsync();
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

    private void Confirm(string prompt, string confirmText, Action confirmed, string? secondaryText = null, Action? secondary = null,
        string cancelText = "Cancel")
    {
        _modals.ShowModal(new ConfirmationModalViewModel
        {
            Prompt = prompt,
            ConfirmText = confirmText,
            CancelText = cancelText,
            SecondaryConfirmText = secondaryText,
            ConfirmCommand = ReactiveCommand.Create(() =>
            {
                _modals.CloseModal();
                confirmed();
            }),
            SecondaryConfirmCommand = secondary is null
                ? null
                : ReactiveCommand.Create(() =>
                {
                    _modals.CloseModal();
                    secondary();
                }),
            CancelCommand = ReactiveCommand.Create(_modals.CloseModal),
        });
    }
}
