using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.HeroTraits;
using DeadlockAdvisor.Features.ItemFormulas;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Notifications;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// The window: three pages switched from tabs in the title bar, the Data / View / Help menus, and
/// the status bar. Edits anywhere write through to the data service, which flushes them to CSV on
/// a short debounce, so there's no save step.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    public const string CloseAction = "Close";
    public const string ArtChangedAction = "ArtChanged";

    public const string HowScoringWorks =
        "Every hero is rated 0-100 on a list of traits (Hero Traits tab).\n"
        + "Every item gets rules saying which traits it responds to, and how\n"
        + "strongly (Item Formulas tab).\n\n"
        + "An item's weight for one hero is:\n"
        + "    sum over traits of (hero's trait score × item's coefficient)\n\n"
        + "Each coefficient is:\n"
        + "    trait weight × (the number you typed + the part from item stats)\n"
        + "where item stats come from Data → Sync from Game API, and\n"
        + "data/stat_rules.csv says what each point of a stat is worth.\n\n"
        + "Its score in a match is that weight summed over everyone selected,\n"
        + "using the 'against' coefficients for enemies, 'with' for allies, and\n"
        + "'as' for your own hero.\n\n"
        + "Click any recommendation to see exactly which hero and which trait\n"
        + "produced its score.\n\n"
        + "The small 'data' numbers are a separate second opinion from real\n"
        + "matches (Data → Fetch Match Stats): win-rate points the item gains\n"
        + "against your enemies, and on your own hero. They never change the\n"
        + "ranking.";

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IModalService _modals;
    private readonly IArtService _art;
    private readonly IFilePickerService _filePicker;

    public MainWindowViewModel(
        NotificationOverlayViewModel notificationOverlay,
        MatchViewModel match,
        HeroTraitsViewModel heroTraits,
        ItemFormulasViewModel itemFormulas,
        IDataService data,
        ISettingsService settings,
        INotificationService notifications,
        IModalService modals,
        IArtService art,
        IFilePickerService filePicker)
    {
        NotificationOverlay = notificationOverlay;
        Match = match;
        _data = data;
        _settings = settings;
        _notifications = notifications;
        _modals = modals;
        _art = art;
        _filePicker = filePicker;

        HeroTraits = heroTraits;
        ItemFormulas = itemFormulas;
        Pages = [Match, HeroTraits, ItemFormulas];

        CurrentPage = Math.Clamp(settings.Current.LastPage, 0, Pages.Count - 1);
        this.WhenAnyValue(vm => vm.CurrentPage)
            .Skip(1)
            .Subscribe(page =>
            {
                this.RaisePropertyChanged(nameof(IsMatchPage));
                this.RaisePropertyChanged(nameof(IsHeroTraitsPage));
                this.RaisePropertyChanged(nameof(IsItemFormulasPage));
                _settings.Update(s => s.LastPage = page);
            })
            .DisposeWith(Disposables);

        settings.SettingsChanged
            .Select(s => ZoomLevels.Clamp(s.ZoomIndex))
            .DistinctUntilChanged()
            .Subscribe(index =>
            {
                UiScale = ZoomLevels.Steps[index];
                ZoomText = $"{Math.Round(UiScale * 100):0}%  ";
            })
            .DisposeWith(Disposables);

        data.SaveStates
            .Subscribe(state => SaveText = state switch
            {
                SaveState.Saving => "saving...",
                SaveState.Saved => "saved",
                SaveState.Failed => "save failed",
                _ => "",
            })
            .DisposeWith(Disposables);
        data.StoreReplaced.Merge(data.ScoresChanged).Subscribe(_ => RefreshStatus()).DisposeWith(Disposables);

        ZoomInCommand = ReactiveCommand.Create(() => SetZoom(_settings.Current.ZoomIndex + 1));
        ZoomOutCommand = ReactiveCommand.Create(() => SetZoom(_settings.Current.ZoomIndex - 1));
        ResetZoomCommand = ReactiveCommand.Create(() => SetZoom(ZoomLevels.DefaultIndex));
        NextPageCommand = ReactiveCommand.Create(() => CyclePage(1));
        PreviousPageCommand = ReactiveCommand.Create(() => CyclePage(-1));
        ShowPageCommand = ReactiveCommand.Create<int>(page => CurrentPage = page);

        ReloadCommand = ReactiveCommand.Create(Reload);
        OpenDataFolderCommand = ReactiveCommand.Create(() => OpenFolder(_data.DataDir));
        ChangeDataFolderCommand = ReactiveCommand.CreateFromTask(ChangeDataFolderAsync);
        ReloadArtCommand = ReactiveCommand.Create(ReloadArt);
        FindCommand = ReactiveCommand.Create(Find);
        HelpCommand = ReactiveCommand.Create(() => ShowMessage("How scoring works", HowScoringWorks));
        QuitCommand = ReactiveCommand.Create(() => RequestViewAction(CloseAction));
        NotYetCommand = ReactiveCommand.Create<string>(name =>
            _notifications.ShowInformation($"{name} arrives with the Data menu port (phase 4)."));

        RefreshStatus();
    }

    public NotificationOverlayViewModel NotificationOverlay { get; }

    public MatchViewModel Match { get; }
    public HeroTraitsViewModel HeroTraits { get; }
    public ItemFormulasViewModel ItemFormulas { get; }
    public IReadOnlyList<ViewModelBase> Pages { get; }
    public IReadOnlyList<string> PageNames { get; } = ["Match", "Hero Traits", "Item Formulas"];

    [Reactive] public int CurrentPage { get; set; }
    public bool IsMatchPage => CurrentPage == 0;
    public bool IsHeroTraitsPage => CurrentPage == 1;
    public bool IsItemFormulasPage => CurrentPage == 2;
    [Reactive] public double UiScale { get; private set; } = 1.0;

    [Reactive] public string ZoomText { get; private set; } = "";
    [Reactive] public string CoverageText { get; private set; } = "";
    [Reactive] public string DataStatusText { get; private set; } = "";
    [Reactive] public bool DataStatusAlert { get; private set; }
    [Reactive] public string SaveText { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetZoomCommand { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousPageCommand { get; }
    public ReactiveCommand<int, Unit> ShowPageCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDataFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeDataFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadArtCommand { get; }
    public ReactiveCommand<Unit, Unit> FindCommand { get; }
    public ReactiveCommand<Unit, Unit> HelpCommand { get; }
    public ReactiveCommand<Unit, Unit> QuitCommand { get; }
    public ReactiveCommand<string, Unit> NotYetCommand { get; }

    /// <summary>Write pending edits before the window closes.</summary>
    public void OnClosing() => _data.FlushSaves();

    private void SetZoom(int index) => _settings.Update(s => s.ZoomIndex = ZoomLevels.Clamp(index));

    private void CyclePage(int step) => CurrentPage = ((CurrentPage + step) % Pages.Count + Pages.Count) % Pages.Count;

    /// <summary>Ctrl+F belongs to whichever page is open; jumping back to Match would lose your place.</summary>
    private void Find()
    {
        if (Pages[CurrentPage] is ISearchablePage page)
        {
            page.FocusSearch();
            return;
        }
        CurrentPage = 0;
        Match.FocusSearch();
    }

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
        _notifications.ShowSuccess($"Now using the data in {_data.DataRoot}.");
    }

    private void ReloadArt()
    {
        _art.Refresh();
        RequestViewAction(ArtChangedAction);
        _notifications.ShowInformation(
            $"Found {_art.Count(ArtKind.Hero)} portrait file(s) in {_art.FolderOf(ArtKind.Hero)}. Heroes without one keep their initials tile.",
            TimeSpan.FromSeconds(5));
    }

    private void ShowMessage(string title, string body) =>
        _modals.ShowModal(new MessageModalViewModel(title, body, ReactiveCommand.Create(_modals.CloseModal)));

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

    private void RefreshStatus()
    {
        var coverage = _data.Store.Coverage();
        CoverageText = $"  {coverage.ScoresFilled}/{coverage.ScoresTotal} hero traits rated"
                       + $"   ·   {coverage.ItemsTagged}/{coverage.ItemsTotal} items tagged"
                       + $"   ·   {coverage.Rules} formula rules + {coverage.DerivedRules} from stats";

        var summary = MatchStatsMath.Summary(_data.Store.MatchMeta, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        DataStatusText = summary.Length == 0
            ? "   ·   no match data (Data → Fetch Match Stats)"
            : $"   ·   match data: {summary}";
        DataStatusAlert = false;
    }
}
