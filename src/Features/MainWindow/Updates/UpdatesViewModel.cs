using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.BackgroundJobs;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow.Updates;

/// <summary>
/// The status bar's one answer to "is everything up to date?": a chip, and the flyout it opens with a row each for
/// the app, the formulas, the match data and the art. It holds no state of its own: every row is worked out again
/// from the services' own state whenever one of them changes, and the chip is the most pressing of the rows.
/// </summary>
public sealed class UpdatesViewModel : UpdateStatusViewModel
{
    private readonly ISettingsService _settings;
    private readonly IConnectivityService _connectivity;
    private readonly IArtService _art;
    private readonly IDeadlockApi _api;
    private readonly DataMenuViewModel _dataMenu;
    private readonly AppUpdateViewModel _app;
    private readonly DataStatusViewModel _matchData;
    private string? _modelPublished;

    public UpdatesViewModel(IDataService data, ISettingsService settings, IConnectivityService connectivity, IArtService art,
        IDeadlockApi api, DataMenuViewModel dataMenu, AppUpdateViewModel app, DataStatusViewModel matchData)
    {
        _settings = settings;
        _connectivity = connectivity;
        _art = art;
        _api = api;
        _dataMenu = dataMenu;
        _app = app;
        _matchData = matchData;

        Rows =
        [
            new UpdateRowViewModel(UpdateSource.App, "App"),
            new UpdateRowViewModel(UpdateSource.Formulas, "Formulas"),
            new UpdateRowViewModel(UpdateSource.MatchData, "Match data", matchData),
            new UpdateRowViewModel(UpdateSource.Art, "Art"),
        ];
        foreach (var row in Rows)
            row.DisposeWith(Disposables);

        CheckAllCommand = ReactiveCommand.CreateFromTask(CheckAllAsync, dataMenu.WhenAnyValue(menu => menu.IsBusy).Select(busy => !busy));

        data.StoreReplaced
            .Merge(settings.SettingsChanged.Select(s => s.ModelCheckedAt).DistinctUntilChanged().Select(_ => Unit.Default))
            .StartWith(Unit.Default)
            .Subscribe(_ => _modelPublished = ModelManifest.Installed(data.DataDir)?.Published)
            .DisposeWith(Disposables);

        Observable.Merge(
                settings.SettingsChanged.Select(_ => Unit.Default),
                Changes(app.WhenAnyValue(vm => vm.State, vm => vm.Available, vm => vm.Installed, vm => vm.PercentText)),
                Changes(app.WhenAnyValue(vm => vm.IsChecking, vm => vm.CanInstall)),
                Changes(dataMenu.WhenAnyValue(menu => menu.NewerPatch, menu => menu.IsDownloadingMatchData, menu => menu.IsDownloadingArt)),
                Changes(dataMenu.WhenAnyValue(menu => menu.IsCheckingModel, menu => menu.IsCheckingMatchData)),
                Changes(matchData.WhenAnyValue(status => status.Summary)),
                connectivity.States.ObserveOn(RxApp.MainThreadScheduler).Select(_ => Unit.Default),
                data.StoreReplaced,
                dataMenu.ViewInteraction.Where(action => action == DataMenuViewModel.ArtChangedAction).Select(_ => Unit.Default),
                JobChanges(dataMenu.Jobs))
            .Subscribe(_ => Refresh())
            .DisposeWith(Disposables);
        Refresh();
    }

    public IReadOnlyList<UpdateRowViewModel> Rows { get; }

    /// <summary>What the chip says: "Up to date", "Updating match data 40%", "Patch 10-07 is out", "2 updates".</summary>
    [Reactive] public string Headline { get; private set; } = "";

    /// <summary>The flyout's first line, a sentence about the whole.</summary>
    [Reactive] public string Status { get; private set; } = "";

    /// <summary>"Downloaded this session: 3.2 MB", from what the app's requests brought in.</summary>
    [Reactive] public string Downloaded { get; private set; } = "";

    /// <summary>Check all is under way.</summary>
    [Reactive] public bool IsCheckingAll { get; private set; }

    /// <summary>Every check the app makes, now, with the messages a manual one gives.</summary>
    public ReactiveCommand<Unit, Unit> CheckAllCommand { get; }

    /// <summary>Works everything out again, as the flyout does on opening so the ages are current.</summary>
    public void Refresh()
    {
        Rows[0].Apply(AppRow());
        Rows[1].Apply(FormulasRow());
        Rows[2].Apply(MatchDataRow());
        Rows[3].Apply(ArtRow());

        var offer = Offer(DataMenuViewModel.WelcomeChipTitle) is not null;
        var states = Rows.Select(row => row.State).ToList();
        if (offer)
            states.Add(UpdateState.Available);
        State = states.MaxBy(Rank);
        Headline = HeadlineFor(State, offer);
        Status = StatusFor(State, states.Count(state => state == UpdateState.Available));
        Downloaded = _api.BytesReceived > 0
            ? $"Downloaded this session: {MatchFetchEstimate.DescribeBytes(_api.BytesReceived)}"
            : "Nothing downloaded this session";
    }

    private async Task CheckAllAsync()
    {
        IsCheckingAll = true;
        try
        {
            await Task.WhenAll(_dataMenu.CheckAllAsync(manual: true), _app.CheckAsync(manual: true));
        }
        finally
        {
            IsCheckingAll = false;
        }
    }

    // -- rows -----------------------------------------------------------------------

    private UpdateRowInfo AppRow()
    {
        var settings = _settings.Current;
        if (_app.Current is not { } running)
            return new(UpdateState.Off, "Built outside the release workflow, so there's no version to compare with releases", "");

        var version = $"Version {running}";
        switch (_app.State)
        {
            case AppUpdateState.Downloading:
                var percent = _app.PercentText;
                return new(UpdateState.Updating,
                    $"Downloading version {_app.Available?.Version}" + (percent.Length > 0 ? $" · {percent}" : ""),
                    $"Updating app {percent}".TrimEnd(), "Stop", _app.DismissCommand, _app.DismissTip,
                    Percent: percent.Length > 0 ? _app.Percent : null);
            case AppUpdateState.Ready:
                return new(UpdateState.Available, $"Version {_app.Installed?.Version} is downloaded: it's installed when you close the app",
                    $"Version {_app.Installed?.Version} is ready", "Restart now", _app.RestartCommand,
                    "Close the app, install the new version and start it", [new("Hide", _app.DismissCommand, _app.DismissTip)]);
            case AppUpdateState.Available:
                return new(UpdateState.Available, $"Version {_app.Available?.Version} is out · you have {running}",
                    $"Version {_app.Available?.Version} is out", "Update", _app.UpdateCommand, _app.UpdateTip,
                    [
                        new("What's new", _app.OpenCommand, "The release on GitHub: what changed, and the download"),
                        new("Skip this version", _app.DismissCommand, _app.DismissTip),
                    ]);
        }

        var checkedAt = settings.AppUpdateCheckedAt;
        var (state, summary, headline) = !settings.CheckForAppUpdates ? (UpdateState.Off, $"{version} · automatic checks are off", "")
            : _connectivity.IsOffline ? (UpdateState.Offline, $"{version} · {CheckedText(checkedAt)}", "Offline")
            : _app.IsChecking ? (UpdateState.Checking, $"{version} · checking GitHub…", "")
            : checkedAt is null ? (UpdateState.NotChecked, $"{version} · not checked yet", "")
            : (UpdateState.UpToDate, $"{version} · {CheckedText(checkedAt)}", "");
        return new(state, summary, headline, "Check", _app.CheckNowCommand, "Look on GitHub for a newer version");
    }

    private UpdateRowInfo FormulasRow()
    {
        var settings = _settings.Current;
        var published = _modelPublished is { } date ? $"Published {date}" : "No record of a published version";
        var check = "Take the newest published hero ratings and item formulas, asking about any you've changed";
        IReadOnlyList<UpdateLink> links = [new("What's new", _dataMenu.ModelNotesCommand, "What the publisher said changed, version by version")];

        if (Offer(DataMenuViewModel.ModelChipTitle) is { } update)
            return new(UpdateState.Available, "A newer version of the formulas is ready", "New formulas", "Apply", update.OpenCommand, update.ToolTipText, links);
        if (Offer(DataMenuViewModel.NewHeroesChipTitle) is { } heroes)
            return new(UpdateState.Available, $"New heroes: {heroes.StatusText}", $"New heroes: {heroes.StatusText}", "Add", heroes.OpenCommand,
                heroes.ToolTipText, links);

        var checkedAt = settings.ModelCheckedAt;
        var (state, summary) = !settings.AutoUpdateModel ? (UpdateState.Off, $"{published} · automatic updates are off")
            : _connectivity.IsOffline ? (UpdateState.Offline, $"{published} · {CheckedText(checkedAt)}")
            : _dataMenu.IsCheckingModel ? (UpdateState.Checking, $"{published} · checking GitHub…")
            : checkedAt is null ? (UpdateState.NotChecked, $"{published} · not checked yet")
            : (UpdateState.UpToDate, $"{published} · {CheckedText(checkedAt)}");
        return new(state, summary, state == UpdateState.Offline ? "Offline" : "", "Check", _dataMenu.CheckModelCommand, check, links);
    }

    private UpdateRowInfo MatchDataRow()
    {
        var settings = _settings.Current;
        var job = Job(DataMenuViewModel.MatchDataJobTitle);
        if (_dataMenu.IsDownloadingMatchData)
            return Downloading(job, "match data");
        if (job is { HasFailed: true })
        {
            return new(UpdateState.Failed, "The last download didn't finish", "Match data download failed", "Try again", _dataMenu.DownloadMatchDataCommand,
                null, [new("What went wrong", job.OpenCommand)]);
        }

        var summary = _matchData.Summary;
        var download = "Pull item win rates from real matches (seconds from the shared download, a few minutes from deadlock-api.com; in the background)";
        if (_dataMenu.NewerPatch is { } newer)
        {
            return new(UpdateState.Available, summary, $"Patch {newer.Label} is out", "Update…", _dataMenu.DownloadMatchDataCommand, download);
        }
        if (!_matchData.HasData)
        {
            return new(UpdateState.Off, "None yet · real win rates add a second opinion to the recommendations", "", "Download…",
                _dataMenu.DownloadMatchDataCommand, download);
        }

        var checkedAt = settings.MatchDataCheckedAt;
        var (state, text) = !settings.AutoUpdateMatchData && !settings.CheckForNewerPatch ? (UpdateState.Off, $"{summary} · automatic updates are off")
            : _connectivity.IsOffline ? (UpdateState.Offline, $"{summary} · {CheckedText(checkedAt)}")
            : _dataMenu.IsCheckingMatchData ? (UpdateState.Checking, $"{summary} · checking for a new patch…")
            : checkedAt is null ? (UpdateState.NotChecked, $"{summary} · not checked yet")
            : (UpdateState.UpToDate, $"{summary} · {CheckedText(checkedAt)}");
        return new(state, text, state == UpdateState.Offline ? "Offline" : "", "Download again…", _dataMenu.DownloadMatchDataCommand, download);
    }

    private UpdateRowInfo ArtRow()
    {
        var job = Job(DataMenuViewModel.ArtJobTitle);
        if (_dataMenu.IsDownloadingArt)
            return Downloading(job, "art");
        if (job is { HasFailed: true })
        {
            return new(UpdateState.Failed, "The last download didn't finish", "Art download failed", "Try again", _dataMenu.DownloadArtCommand,
                null, [new("What went wrong", job.OpenCommand)]);
        }

        var download = "Fetch hero portraits, item icons and top-bar art from deadlock-api.com";
        var heroes = _art.Count(ArtKind.Hero);
        var items = _art.Count(ArtKind.Item);
        if (heroes == 0 && items == 0)
        {
            return new(UpdateState.Off, "None yet · portraits, icons and the art Detect from screen reads", "", "Download…",
                _dataMenu.DownloadArtCommand, download);
        }

        var checkedAt = _settings.Current.ArtCheckedAt;
        var summary = $"{heroes} portraits, {items} icons" + (checkedAt is null ? "" : $" · {CheckedText(checkedAt)}");
        return new(_connectivity.IsOffline ? UpdateState.Offline : UpdateState.UpToDate, summary, _connectivity.IsOffline ? "Offline" : "",
            "Download…", _dataMenu.DownloadArtCommand, download);
    }

    /// <summary>A download under way, as far as its chip says.</summary>
    private static UpdateRowInfo Downloading(BackgroundJobViewModel? job, string noun)
    {
        double? percent = job is null || job.IsIndeterminate ? null : Math.Floor(job.Done * 100 / job.Total);
        var headline = percent is { } known ? $"Updating {noun} {known:0}%" : $"Updating {noun}";
        return new(UpdateState.Updating, job is null ? "Starting…" : $"Downloading · {job.StatusText}", headline, "Stop",
            job?.CancelCommand, "Stop downloading: what has finished is kept", Percent: percent);
    }

    // -- the whole ------------------------------------------------------------------

    /// <summary>The most pressing state first: a failure, then a download, something waiting, no connection, and so on down to current.</summary>
    private static int Rank(UpdateState state) => state switch
    {
        UpdateState.Failed => 7,
        UpdateState.Updating => 6,
        UpdateState.Available => 5,
        UpdateState.Offline => 4,
        UpdateState.Checking => 3,
        UpdateState.NotChecked => 2,
        UpdateState.UpToDate => 1,
        _ => 0,
    };

    private string HeadlineFor(UpdateState state, bool offer)
    {
        var rows = Rows.Where(row => row.State == state).ToList();
        switch (state)
        {
            case UpdateState.Failed:
                return rows is [var failed] ? failed.Headline : $"{rows.Count} downloads failed";
            case UpdateState.Updating:
                return rows is [var updating] ? updating.Headline : $"Updating {Listed(rows.Select(row => row.Title.ToLowerInvariant()).ToList())}";
            case UpdateState.Available:
                var waiting = rows.Count + (offer ? 1 : 0);
                return waiting > 1 ? $"{waiting} updates" : rows is [var one] ? one.Headline : "Downloads available";
            case UpdateState.Offline:
                return "Offline";
            case UpdateState.Checking:
                return "Checking for updates…";
            case UpdateState.NotChecked:
                return "Not checked yet";
            case UpdateState.Off:
                return "Updates are off";
            default:
                return "Up to date";
        }
    }

    private static string StatusFor(UpdateState state, int waiting) => state switch
    {
        UpdateState.Failed => "A download didn't finish.",
        UpdateState.Updating => "Downloading in the background.",
        UpdateState.Available => waiting > 1 ? $"{waiting} updates are waiting." : "An update is waiting.",
        UpdateState.Offline => "No internet connection: everything works from what's saved.",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.NotChecked => "Some of it hasn't been checked yet.",
        UpdateState.Off => "Updates are off.",
        _ => "Everything is up to date.",
    };

    private static string Listed(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    // -- what the services hold -----------------------------------------------------

    /// <summary>A chip the Data menu put in the status bar to be clicked: the job that waits there, not one that failed or is running.</summary>
    private BackgroundJobViewModel? Offer(string title) =>
        Job(title) is { IsFinished: true, HasFailed: false } job ? job : null;

    private BackgroundJobViewModel? Job(string title) => _dataMenu.Jobs.FirstOrDefault(job => job.Title == title);

    /// <summary>"checked 2h ago".</summary>
    private static string CheckedText(DateTimeOffset? at) =>
        at is { } when ? $"checked {MatchStatsMath.Age((DateTimeOffset.Now - when).TotalSeconds)}" : "not checked yet";

    private static IObservable<Unit> Changes<T>(IObservable<T> source) => source.Select(_ => Unit.Default);

    /// <summary>The jobs coming and going, and each one's progress while it's there.</summary>
    private static IObservable<Unit> JobChanges(System.Collections.ObjectModel.ObservableCollection<BackgroundJobViewModel> jobs) =>
        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => jobs.CollectionChanged += handler, handler => jobs.CollectionChanged -= handler)
            .Select(_ => Unit.Default)
            .StartWith(Unit.Default)
            .Select(_ => jobs.Select(job => Changes(job.WhenAnyValue(vm => vm.State, vm => vm.StatusText))).Merge().StartWith(Unit.Default))
            .Switch();
}
