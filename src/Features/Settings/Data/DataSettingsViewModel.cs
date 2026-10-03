using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
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
        AppUpdateViewModel appUpdate, ICommand reloadArt) : base(settings)
    {
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

    public ICommand CheckModelCommand { get; }
    public ICommand ResetModelCommand { get; }
    public ICommand UndoModelUpdateCommand { get; }
    public ICommand ModelNotesCommand { get; }
    public ICommand CheckAppCommand { get; }

    public string ReleasesUrl => AppVersion.ReleasesUrl;

    public bool CheckForAppUpdates
    {
        get => Current.CheckForAppUpdates;
        set => Change(s => s.CheckForAppUpdates = value);
    }

    public bool CheckForNewerPatch
    {
        get => Current.CheckForNewerPatch;
        set => Change(s => s.CheckForNewerPatch = value);
    }

    public bool AutoUpdateMatchData
    {
        get => Current.AutoUpdateMatchData;
        set => Change(s => s.AutoUpdateMatchData = value);
    }

    public bool AutoUpdateModel
    {
        get => Current.AutoUpdateModel;
        set => Change(s => s.AutoUpdateModel = value);
    }

    [Reactive] public string MatchDataSummary { get; private set; } = "";

    public ICommand DownloadMatchDataCommand { get; }

    public ICommand ChangeDataFolderCommand { get; }
    public ICommand DownloadArtCommand { get; }
    public ICommand ReloadArtCommand { get; }

    /// <summary>Opens the folder given as its parameter.</summary>
    public ICommand OpenFolderCommand { get; }

    public override void Refresh()
    {
        DataFolder = _data.DataRoot;
        ArtFolder = _art.AssetsDir;
        ArtSummary = $"{_art.Count(ArtKind.Hero)} hero portrait(s) and {_art.Count(ArtKind.Item)} item icon(s) in {ArtFolder}";
        var patches = MatchStatsMath.PatchFacts(_data.Store.MatchMeta);
        MatchDataSummary = (MatchStatsMath.FetchedAt(_data.Store.MatchMeta) is null
            ? "No match data yet"
            : string.Join(", ", patches.Select(patch => $"{patch.Label.ToLowerInvariant()} ({patch.Value})")))
            + $" · {CheckedText(Current.MatchDataCheckedAt)}";

        var installed = ModelManifest.Installed(_data.DataDir);
        ModelSummary = (installed is null ? "No record of a published version" : $"The version published {installed.Published}")
                       + $" · {CheckedText(Current.ModelCheckedAt)}";
        CanUndoModelUpdate = ModelUpdateService.Undoable(_data.DataDir).Count > 0;

        Version = AppVersion.Text + (_appUpdate.Current is null
            ? " · made outside the release workflow, so it isn't compared with releases"
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
