using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.Data;

/// <summary>
/// Where the data, the art and the app's own files live, and how the match data, the formulas and the app
/// keep up to date: what's installed, and when each was last checked.
/// </summary>
public class DataSettingsViewModel : SettingsPageViewModel
{
    private readonly IDataService _data;
    private readonly IArtService _art;
    private readonly AppUpdateViewModel _appUpdate;

    public DataSettingsViewModel(ISettingsService settings, IDataService data, IArtService art, DataMenuViewModel dataMenu,
        AppUpdateViewModel appUpdate, ICommand reloadArt, ICommand checkForUpdates) : base(settings)
    {
        CheckForUpdatesCommand = checkForUpdates;
        _data = data;
        _art = art;
        _appUpdate = appUpdate;
        CheckModelCommand = dataMenu.CheckModelCommand;
        ResetModelCommand = dataMenu.ResetModelCommand;
        UndoModelUpdateCommand = dataMenu.UndoModelUpdateCommand;
        ModelNotesCommand = dataMenu.ModelNotesCommand;
        CheckAppCommand = appUpdate.CheckNowCommand;
        ChangeDataFolderCommand = dataMenu.ChangeDataFolderCommand;
        DownloadArtCommand = dataMenu.DownloadArtCommand;
        DownloadMatchDataCommand = dataMenu.DownloadMatchDataCommand;
        CheckMatchDataCommand = dataMenu.CheckMatchDataCommand;
        ReloadArtCommand = reloadArt;
        OpenFolderCommand = dataMenu.OpenFolderCommand;
        Refresh();

        // A check finishing, or a newer version turning up, while the page is open.
        settings.SettingsChanged
            .Select(s => (s.MatchDataCheckedAt, s.ModelCheckedAt, s.AppUpdateCheckedAt))
            .DistinctUntilChanged()
            .Skip(1)
            .Select(_ => Unit.Default)
            .Merge(appUpdate.WhenAnyValue(vm => vm.State, vm => vm.Installed).Skip(1).Select(_ => Unit.Default))
            .Subscribe(_ => Refresh())
            .DisposeWith(Disposables);
    }

    [Reactive] public string DataFolder { get; private set; } = "";
    [Reactive] public string ArtFolder { get; private set; } = "";
    [Reactive] public string ArtSummary { get; private set; } = "";

    /// <summary>Settings and the log.</summary>
    public string AppFolder => JsonSettingsService.AppDataPath;

    /// <summary>"0.2.0 (15b6f95) · checked for updates 2h ago", or the newer version that's out.</summary>
    [Reactive] public string Version { get; private set; } = "";

    /// <summary>"The version published 2026-10-09 · checked for updates 2h ago".</summary>
    [Reactive] public string ModelSummary { get; private set; } = "";

    /// <summary>The last formula update or reset replaced files that are still as it left them.</summary>
    [Reactive] public bool CanUndoModelUpdate { get; private set; }

    /// <summary>Data → Check for Updates: everything at once, as the Updates flyout's button.</summary>
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand CheckModelCommand { get; }
    public ICommand ResetModelCommand { get; }
    public ICommand UndoModelUpdateCommand { get; }
    public ICommand ModelNotesCommand { get; }
    public ICommand CheckAppCommand { get; }

    public string ReleasesUrl => AppVersion.ReleasesUrl;

    public IReadOnlyList<UpdateModeOption> MatchDataModes { get; } =
    [
        new(UpdateMode.Automatic, "Automatic",
            "When the app starts and every few hours while it's open, refresh the match data in the background if a newer patch is out "
            + "or the current patch's counts are a day and a half old. Finished patches are never fetched again."),
        new(UpdateMode.TellMe, "Tell me", "Check for a newer patch and say so in the status bar. Nothing downloads until you ask."),
        new(UpdateMode.Off, "Off", "Don't check. Download from here, or use Data → Check for Updates, whenever you like."),
    ];

    public IReadOnlyList<UpdateModeOption> FormulaModes { get; } =
    [
        new(UpdateMode.Automatic, "Automatic",
            "Take the newest published version. Files you haven't changed update quietly, with a backup; for ones you have, it asks."),
        new(UpdateMode.TellMe, "Tell me",
            "Say in the status bar when the published ratings have heroes yours lack, and add them when you click it. "
            + "Everything else waits for Check now."),
        new(UpdateMode.Off, "Off", "Don't check. Press Check now when you want the newest published version."),
    ];

    public IReadOnlyList<UpdateModeOption> AppModes { get; } =
    [
        new(UpdateMode.TellMe, "Tell me",
            "Ask GitHub for the newest release when the app starts and every few hours while it's open, and say so in the status bar when there's a newer one."),
        new(UpdateMode.Off, "Off", "Don't check. Press Check now, or look at the releases yourself."),
    ];

    public UpdateModeOption MatchDataMode
    {
        get => OptionFor(MatchDataModes, UpdateModes.MatchData(Current));
        set => ChangeMode(value, UpdateModes.SetMatchData, nameof(MatchDataModeDescription));
    }

    public UpdateModeOption FormulaMode
    {
        get => OptionFor(FormulaModes, UpdateModes.Formulas(Current));
        set => ChangeMode(value, UpdateModes.SetFormulas, nameof(FormulaModeDescription));
    }

    public UpdateModeOption AppMode
    {
        get => OptionFor(AppModes, UpdateModes.App(Current));
        set => ChangeMode(value, UpdateModes.SetApp, nameof(AppModeDescription));
    }

    public string MatchDataModeDescription => MatchDataMode.Description;
    public string FormulaModeDescription => FormulaMode.Description;
    public string AppModeDescription => AppMode.Description;

    [Reactive] public string MatchDataSummary { get; private set; } = "";

    public ICommand DownloadMatchDataCommand { get; }
    public ICommand CheckMatchDataCommand { get; }

    public ICommand ChangeDataFolderCommand { get; }
    public ICommand DownloadArtCommand { get; }
    public ICommand ReloadArtCommand { get; }

    /// <summary>Opens the folder given as its parameter.</summary>
    public ICommand OpenFolderCommand { get; }

    private static UpdateModeOption OptionFor(IReadOnlyList<UpdateModeOption> options, UpdateMode mode) =>
        options.FirstOrDefault(option => option.Mode == mode) ?? options[0];

    private void ChangeMode(UpdateModeOption? option, Action<AppSettings, UpdateMode> write, string description,
        [CallerMemberName] string? property = null)
    {
        if (option is null)
            return;
        Change(settings => write(settings, option.Mode), property);
        this.RaisePropertyChanged(description);
    }

    public override void Refresh()
    {
        // The first-run offer and the download dialog change these too.
        this.RaisePropertyChanged(nameof(MatchDataMode));
        this.RaisePropertyChanged(nameof(MatchDataModeDescription));
        this.RaisePropertyChanged(nameof(FormulaMode));
        this.RaisePropertyChanged(nameof(FormulaModeDescription));
        this.RaisePropertyChanged(nameof(AppMode));
        this.RaisePropertyChanged(nameof(AppModeDescription));

        DataFolder = _data.DataRoot;
        ArtFolder = _art.AssetsDir;
        ArtSummary = $"{_art.Count(ArtKind.Hero)} hero portrait(s) and {_art.Count(ArtKind.Item)} item icon(s) in {ArtFolder}";
        var patches = MatchStatsMath.PatchFacts(_data.Store.MatchMeta);
        MatchDataSummary = (MatchStatsMath.FetchedAt(_data.Store.MatchMeta) is null
            ? "No match data yet"
            : string.Join(", ", patches.Select(patch => $"{patch.Label.ToLowerInvariant()} ({patch.Value})")))
            + $" · {CheckedText(Current.MatchDataCheckedAt)}";

        var installed = ModelManifest.Installed(_data.DataDir);
        ModelSummary = (installed is null ? "Published version not recorded yet" : $"The version published {installed.Published}")
                       + $" · {CheckedText(Current.ModelCheckedAt)}";
        CanUndoModelUpdate = ModelUpdateService.Undoable(_data.DataDir).Count > 0;

        Version = AppVersion.Text + (_appUpdate.Current is null
            ? " · not a release build, so it isn't compared with the releases"
            : _appUpdate.Installed is { } update
                ? $" · {update.Version} is downloaded, and installed when the app closes"
                : _appUpdate.Available is { } newer
                    ? $" · {newer.Version} is out"
                    : $" · {CheckedText(Current.AppUpdateCheckedAt)}");
    }

    /// <summary>"checked for updates 2h ago".</summary>
    private static string CheckedText(DateTimeOffset? at) =>
        at is { } when ? $"checked for updates {MatchStatsMath.Age((DateTimeOffset.Now - when).TotalSeconds)}" : "not checked for updates yet";
}
