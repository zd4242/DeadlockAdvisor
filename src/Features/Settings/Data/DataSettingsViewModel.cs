using System.Windows.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.Data;

/// <summary>Where the data, the art and the app's own files live, and how the match data keeps up with patches.</summary>
public class DataSettingsViewModel : SettingsPageViewModel
{
    private readonly IDataService _data;
    private readonly IArtService _art;

    public DataSettingsViewModel(ISettingsService settings, IDataService data, IArtService art, DataMenuViewModel dataMenu,
        ICommand reloadArt) : base(settings)
    {
        _data = data;
        _art = art;
        ChangeDataFolderCommand = dataMenu.ChangeDataFolderCommand;
        DownloadArtCommand = dataMenu.DownloadArtCommand;
        DownloadMatchDataCommand = dataMenu.DownloadMatchDataCommand;
        ReloadArtCommand = reloadArt;
        OpenFolderCommand = dataMenu.OpenFolderCommand;
        Refresh();
    }

    [Reactive] public string DataFolder { get; private set; } = "";
    [Reactive] public string ArtFolder { get; private set; } = "";
    [Reactive] public string ArtSummary { get; private set; } = "";

    /// <summary>Settings and the log.</summary>
    public string AppFolder => JsonSettingsService.AppDataPath;

    /// <summary>"1.2.0 (15b6f95)".</summary>
    public string Version => AppVersion.Text;

    public string ReleasesUrl => AppVersion.ReleasesUrl;

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
        MatchDataSummary = MatchStatsMath.FetchedAt(_data.Store.MatchMeta) is null
            ? "No match data yet."
            : string.Join(", ", patches.Select(patch => $"{patch.Label.ToLowerInvariant()} ({patch.Value})"));
    }
}
