using System.Windows.Input;
using DeadlockAdvisor.Features.MainWindow;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.Data;

/// <summary>Where the data, the art and the app's own files live, and whether to ask the API about new patches.</summary>
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
        ReloadArtCommand = reloadArt;
        OpenFolderCommand = dataMenu.OpenFolderCommand;
        Refresh();
    }

    [Reactive] public string DataFolder { get; private set; } = "";
    [Reactive] public string ArtFolder { get; private set; } = "";
    [Reactive] public string ArtSummary { get; private set; } = "";

    /// <summary>Settings and the log.</summary>
    public string AppFolder => JsonSettingsService.AppDataPath;

    public bool CheckForNewerPatch
    {
        get => Current.CheckForNewerPatch;
        set => Change(s => s.CheckForNewerPatch = value);
    }

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
    }
}
