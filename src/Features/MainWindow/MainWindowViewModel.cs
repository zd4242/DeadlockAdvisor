using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.HeroItems;
using DeadlockAdvisor.Features.HeroTraits;
using DeadlockAdvisor.Features.ItemFormulas;
using DeadlockAdvisor.Features.Match;
using DeadlockAdvisor.Features.Settings;
using DeadlockAdvisor.Features.Settings.Data;
using DeadlockAdvisor.Features.Settings.Detection;
using DeadlockAdvisor.Features.Settings.General;
using DeadlockAdvisor.Features.Settings.Shortcuts;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Features.Shared.Modals.Progress;
using DeadlockAdvisor.Features.Shared.Notifications;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// The window: its pages switched from tabs in the title bar (the model editors' only with them shown),
/// the Settings page over them, the Data / View / Help menus, and the status bar. Edits anywhere write
/// through to the data service, which flushes them to CSV on a short debounce, so there's no save step.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    public const string CloseAction = "Close";
    public const string ArtChangedAction = DataMenuViewModel.ArtChangedAction;
    public const string BringForwardAction = "BringForward";

    public const string HowScoringWorks =
        "Every hero is rated 0-100 on a list of traits (Hero Traits tab).\n"
        + "Every item gets rules saying which traits it responds to, and how\n"
        + "strongly (Item Formulas tab). Both tabs show with Settings →\n"
        + "Edit the scoring model.\n\n"
        + "An item's weight for one hero is:\n"
        + "    sum over traits of\n"
        + "        (hero's trait score − the roster's average) × item's coefficient\n"
        + "so a hero only moves an item's score by how far they differ from\n"
        + "a typical hero. A trait every hero has doesn't make its items look\n"
        + "good in every match.\n\n"
        + "Each coefficient is:\n"
        + "    trait weight × (the number you typed + the part from item stats)\n"
        + "where item stats come from Data → Sync from Game API, and\n"
        + "data/stat_rules.csv says what each point of a stat is worth.\n"
        + "Item stats include the item's share of its shop's investment\n"
        + "bonus (weapon damage, spirit power or base health for its cost).\n\n"
        + "Its score in a match is that weight summed over everyone selected,\n"
        + "using the 'against' coefficients for enemies, 'with' for allies, and\n"
        + "'as' for your own hero. Above 0 means this match wants the item\n"
        + "more than an average match would.\n\n"
        + "An item cast on one hero at a time (Decay, Knockdown, Rescue Beam)\n"
        + "counts its best target in full instead, the next ×0.5, then ×0.25\n"
        + "and so on, less what that comes to for a typical team the same size:\n"
        + "a match with one great target is a good match for it. A rule marked\n"
        + "'best target' on the Item Formulas tab counts the same way, for an\n"
        + "item one hero is enough to trigger, like Reactive Barrier.\n\n"
        + "With 'Lean toward heroes ahead on net worth' on (under Filters),\n"
        + "and net worth read off the top bar by Detect, each hero's share is\n"
        + "scaled by how far ahead or behind the match's average they are:\n"
        + "from ×0.7 to ×1.3. Counters to a fed enemy\n"
        + "and items for a fed ally (or a fed you) count for more.\n\n"
        + "Click an enemy on the match bar to focus on them, such as your lane\n"
        + "opponents: a focused enemy counts 5× as much as each of the others,\n"
        + "×3 against ×0.6 with one focused in a full match. The enemies\n"
        + "together still count as much as before, so the list leans toward\n"
        + "items good against whoever you focused, and the match data's enemy\n"
        + "numbers lean the same way.\n\n"
        + "Click any recommendation to see exactly which hero and which trait\n"
        + "produced its score.\n\n"
        + "The small 'data' numbers are a separate second opinion from real\n"
        + "matches (Data → Download Match Data): win-rate points the item gains\n"
        + "against your enemies, and on your own hero ('hero fit': how much more\n"
        + "your hero wins with it than everyone who builds it). They're never added\n"
        + "into the score, but 'Rank by match data' orders the list by them\n"
        + "instead, and 'Formula + match data' (the default) adds the two, each\n"
        + "measured by how far it typically strays from 0, so an item one of them\n"
        + "has nothing to say about still ranks on the other.\n"
        + "DATA ★ marks an item that stands out in real matches, and 'Match\n"
        + "data also likes' lists standouts the formula scores 0 or below.";

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IModalService _modals;
    private readonly IArtService _art;
    private readonly IAppUpdateService _appUpdates;
    private bool _closeConfirmed;
    /// <summary>The pages opened in order, for the mouse's back and forward buttons, with the one showing at <see cref="_historyIndex"/>.</summary>
    private readonly List<int> _history = [];
    private int _historyIndex;

    public MainWindowViewModel(
        NotificationOverlayViewModel notificationOverlay,
        MatchViewModel match,
        HeroItemsViewModel heroItems,
        HeroTraitsViewModel heroTraits,
        ItemFormulasViewModel itemFormulas,
        DataMenuViewModel dataMenu,
        IDataService data,
        ISettingsService settings,
        INotificationService notifications,
        IModalService modals,
        IArtService art,
        IGlobalHotkeyService hotkey,
        IAppUpdateService appUpdates,
        IConnectivityService connectivity)
    {
        NotificationOverlay = notificationOverlay;
        Match = match;
        HeroItems = heroItems;
        HeroTraits = heroTraits;
        ItemFormulas = itemFormulas;
        DataMenu = dataMenu;
        _data = data;
        _settings = settings;
        _notifications = notifications;
        _modals = modals;
        _art = art;
        _appUpdates = appUpdates;
        Pages = [Match, HeroItems, HeroTraits, ItemFormulas];

        ShowsEditors = settings.Current.ShowModelEditors;
        PageNames = _allPageNames.Take(PageCount).ToList();
        // A model editor's page, hidden since, opens on Match like a page that's gone.
        var lastPage = settings.Current.LastPage;
        CurrentPage = settings.Current.ReopenLastPage && lastPage >= 0 && lastPage < PageCount ? lastPage : 0;
        _history.Add(CurrentPage);
        this.WhenAnyValue(vm => vm.CurrentPage)
            .Skip(1)
            .Subscribe(page => _settings.Update(s => s.LastPage = page))
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.CurrentPage, vm => vm.IsSettingsOpen)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(SelectedTab));
                this.RaisePropertyChanged(nameof(IsMatchPage));
                this.RaisePropertyChanged(nameof(IsHeroItemsPage));
                this.RaisePropertyChanged(nameof(IsHeroTraitsPage));
                this.RaisePropertyChanged(nameof(IsItemFormulasPage));
            })
            .DisposeWith(Disposables);
        // Opened on your hero from the Match page, once it has one.
        this.WhenAnyValue(vm => vm.IsHeroItemsPage)
            .Where(shown => shown)
            .Subscribe(_ => HeroItems.ShowSelf(Match.Match.SelfHero))
            .DisposeWith(Disposables);

        // Hidden from Settings, so a model editor's page underneath goes back to Match for when it closes.
        settings.SettingsChanged
            .Select(s => s.ShowModelEditors)
            .DistinctUntilChanged()
            .Subscribe(show =>
            {
                ShowsEditors = show;
                if (CurrentPage >= PageCount)
                    Visit(0);
                PageNames = _allPageNames.Take(PageCount).ToList();
                this.RaisePropertyChanged(nameof(SelectedTab));
            })
            .DisposeWith(Disposables);

        settings.SettingsChanged
            .Select(s => ZoomLevels.Clamp(s.ZoomIndex))
            .DistinctUntilChanged()
            .Subscribe(index =>
            {
                UiScale = ZoomLevels.Steps[index];
                ZoomText = $"{Math.Round(UiScale * 100):0}%";
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
        DataStatus = new DataStatusViewModel(data, dataMenu, settings).DisposeWith(Disposables);
        AppUpdate = new AppUpdateViewModel(appUpdates, settings, notifications, dataMenu.OpenFolderCommand).DisposeWith(Disposables);
        Connection = new ConnectionViewModel(connectivity, art).DisposeWith(Disposables);
        AppUpdate.RestartRequested.Subscribe(_ => RequestViewAction(CloseAction)).DisposeWith(Disposables);
        dataMenu.ViewInteraction.Subscribe(RequestViewAction).DisposeWith(Disposables);
        match.FormulaRequested.Merge(heroItems.FormulaRequested).Subscribe(ShowFormula).DisposeWith(Disposables);
        match.TraitRequested.Subscribe(ShowTrait).DisposeWith(Disposables);

        var zoom = settings.SettingsChanged.Select(s => ZoomLevels.Clamp(s.ZoomIndex));
        ZoomInCommand = ReactiveCommand.Create(() => SetZoom(_settings.Current.ZoomIndex + 1),
            zoom.Select(index => index < ZoomLevels.Steps.Count - 1));
        ZoomOutCommand = ReactiveCommand.Create(() => SetZoom(_settings.Current.ZoomIndex - 1), zoom.Select(index => index > 0));
        ResetZoomCommand = ReactiveCommand.Create(() => SetZoom(ZoomLevels.DefaultIndex), zoom.Select(index => index != ZoomLevels.DefaultIndex));
        NextPageCommand = ReactiveCommand.Create(() => CyclePage(1));
        PreviousPageCommand = ReactiveCommand.Create(() => CyclePage(-1));
        BackCommand = ReactiveCommand.Create(() => StepHistory(-1));
        ForwardCommand = ReactiveCommand.Create(() => StepHistory(1));
        ShowPageCommand = ReactiveCommand.Create<int>(ShowPage);
        ReloadCommand = ReactiveCommand.CreateFromObservable(() => DataMenu.ReloadCommand.Execute(), this.WhenAnyValue(vm => vm.ShowsEditors));

        ReloadArtCommand = ReactiveCommand.Create(ReloadArt);
        Settings = new SettingsViewModel(
            new GeneralSettingsViewModel(settings, ZoomInCommand, ZoomOutCommand, ResetZoomCommand),
            new ShortcutsSettingsViewModel(settings, hotkey),
            new DetectionSettingsViewModel(settings, hotkey),
            new DataSettingsViewModel(settings, data, art, dataMenu, AppUpdate, ReloadArtCommand)).DisposeWith(Disposables);
        Settings.CloseRequested.Subscribe(_ => IsSettingsOpen = false).DisposeWith(Disposables);
        // A new data folder, or new art, changes what the Data settings show.
        data.StoreReplaced
            .Merge(ViewInteraction.Merge(dataMenu.ViewInteraction).Where(action => action == ArtChangedAction).Select(_ => Unit.Default))
            .Subscribe(_ => Settings.Refresh())
            .DisposeWith(Disposables);
        OpenSettingsCommand = ReactiveCommand.Create(OpenSettings);
        FindCommand = ReactiveCommand.Create(Find);
        HelpCommand = ReactiveCommand.Create(() => _modals.ShowMessage("How scoring works", HowScoringWorks));
        NoticesCommand = ReactiveCommand.Create(ShowNotices);
        QuitCommand = ReactiveCommand.Create(() => RequestViewAction(CloseAction));

        var onMatchPage = this.WhenAnyValue(vm => vm.CurrentPage, vm => vm.IsSettingsOpen, (page, settingsOpen) => page == 0 && !settingsOpen);
        SetModeCommand = ReactiveCommand.Create<Role>(Match.Board.StartAssigning, onMatchPage);
        DetectCommand = ReactiveCommand.CreateFromObservable(() => Match.DetectCommand.Execute(), onMatchPage);
        RandomizeCommand = ReactiveCommand.CreateFromObservable<RandomizeKeep, Unit>(
            Match.Board.RandomizeCommand.Execute,
            onMatchPage.CombineLatest(Match.Board.RandomizeCommand.CanExecute, (onPage, canRandomize) => onPage && canRandomize));
        DetectFromAnywhereCommand = ReactiveCommand.CreateFromTask(DetectFromAnywhereAsync);
        hotkey.Pressed.InvokeCommand(DetectFromAnywhereCommand).DisposeWith(Disposables);
        Match.ArtWanted.Subscribe(_ => DataMenu.DownloadArt()).DisposeWith(Disposables);
        Match.DataRanks.RanksWanted.Subscribe(_ => DataMenu.OfferRankDownload()).DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => ShortcutKeys.Defaults.Keys.Select(action => (action, gesture: s.Gesture(action))).ToEquatableList())
            .DistinctUntilChanged()
            .Subscribe(keys => ShortcutBindings = keys
                .Where(key => key.gesture is not null)
                .Select(key => ShortcutBindingFor(key.action, key.gesture!))
                .ToList())
            .DisposeWith(Disposables);
    }

    public NotificationOverlayViewModel NotificationOverlay { get; }

    public MatchViewModel Match { get; }
    public HeroItemsViewModel HeroItems { get; }
    public HeroTraitsViewModel HeroTraits { get; }
    public ItemFormulasViewModel ItemFormulas { get; }
    public DataMenuViewModel DataMenu { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<ViewModelBase> Pages { get; }

    /// <summary>The model editors' pages come last, so hiding them leaves the other tabs where they were.</summary>
    private static readonly IReadOnlyList<string> _allPageNames = ["Match", "Hero Items", "Hero Traits", "Item Formulas"];

    private const int _heroItemsPage = 1;
    private const int _heroTraitsPage = 2;
    private const int _itemFormulasPage = 3;
    private const int _editorPages = 2;

    /// <summary>The tabs: every page's, but the model editors' only while they're shown.</summary>
    [Reactive] public IReadOnlyList<string> PageNames { get; private set; }

    /// <summary>The page the tabs show, which the Settings page covers while it's open.</summary>
    [Reactive] public int CurrentPage { get; set; }

    [Reactive] public bool IsSettingsOpen { get; private set; }

    /// <summary>The Hero Traits and Item Formulas pages, which a setting hides for anyone not tuning the model.</summary>
    [Reactive] public bool ShowsEditors { get; private set; }

    /// <summary>The highlighted tab: none while Settings is open, and picking one, even the current page's, closes it.</summary>
    public int SelectedTab
    {
        get => IsSettingsOpen ? -1 : CurrentPage;
        set
        {
            if (value >= 0)
                ShowPage(value);
        }
    }

    public bool IsMatchPage => CurrentPage == 0 && !IsSettingsOpen;
    public bool IsHeroItemsPage => CurrentPage == _heroItemsPage && !IsSettingsOpen;
    public bool IsHeroTraitsPage => CurrentPage == _heroTraitsPage && !IsSettingsOpen;
    public bool IsItemFormulasPage => CurrentPage == _itemFormulasPage && !IsSettingsOpen;
    [Reactive] public double UiScale { get; private set; } = 1.0;

    [Reactive] public string ZoomText { get; private set; } = "";
    [Reactive] public string SaveText { get; private set; } = "";

    /// <summary>The status bar's match data chip and its card.</summary>
    public DataStatusViewModel DataStatus { get; }

    /// <summary>The status bar's chip while a newer version of the app is out.</summary>
    public AppUpdateViewModel AppUpdate { get; }

    /// <summary>The status bar's chip while the app can't reach the internet.</summary>
    public ConnectionViewModel Connection { get; }

    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetZoomCommand { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousPageCommand { get; }

    /// <summary>The mouse's back and forward buttons, through the pages opened as in a browser.</summary>
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> ForwardCommand { get; }
    public ReactiveCommand<int, Unit> ShowPageCommand { get; }

    /// <summary>Data → Reload from Disk and Ctrl+R, for picking up CSVs edited by hand: only with the model editors.</summary>
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadArtCommand { get; }
    public ReactiveCommand<Unit, Unit> FindCommand { get; }
    public ReactiveCommand<Unit, Unit> HelpCommand { get; }
    public ReactiveCommand<Unit, Unit> NoticesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> QuitCommand { get; }

    /// <summary>Alt+1/2/3 (which open the hero picker), Random and Detect, the Match page's shortcuts: live while it's showing, wherever focus is.</summary>
    public ReactiveCommand<Role, Unit> SetModeCommand { get; }
    public ReactiveCommand<RandomizeKeep, Unit> RandomizeCommand { get; }
    public ReactiveCommand<Unit, Unit> DetectCommand { get; }

    /// <summary>Detect's key while it's held system-wide, pressed here or in the game, on whichever page is showing.</summary>
    public ReactiveCommand<Unit, Unit> DetectFromAnywhereCommand { get; }

    /// <summary>The rebindable keys (F6–F9 unless Settings → Shortcuts moves them), for the window to bind.</summary>
    [Reactive] public IReadOnlyList<ShortcutBinding> ShortcutBindings { get; private set; } = [];

    /// <summary>The window is up: time for the background checks and the first-run art offer.</summary>
    public void OnOpened()
    {
        DataMenu.OnStartup();
        _ = AppUpdate.OnStartupAsync();
    }

    /// <summary>Write pending edits before the window closes.</summary>
    public void OnClosing() => _data.FlushSaves();

    /// <summary>
    /// True to keep the window open and ask first, because closing would stop a download part-way.
    /// Quitting from that question closes it for good.
    /// </summary>
    public bool HoldCloseForJobs()
    {
        if (_closeConfirmed || _modals.IsModalOpen || !DataMenu.HasRunningJobs)
            return false;
        // Kept open after all: a restart asked for along with this close isn't wanted at the next one.
        _appUpdates.RestartAfterExit(false);

        var running = DataMenu.Jobs.Where(job => job.IsRunning).Select(job => $"  • {job.Title}: {job.StatusText}");
        _modals.Confirm(
            "Still downloading:\n" + string.Join("\n", running)
            + "\n\nQuit anyway? What has arrived is kept, and the next download picks up from there.",
            "Quit",
            () =>
            {
                _closeConfirmed = true;
                DataMenu.CancelJobs();
                RequestViewAction(CloseAction);
            },
            cancelText: "Keep downloading");
        return true;
    }

    private ShortcutBinding ShortcutBindingFor(ShortcutAction action, KeyGesture gesture) => action switch
    {
        ShortcutAction.Detect => new(gesture, DetectCommand, Unit.Default),
        ShortcutAction.Randomize => new(gesture, RandomizeCommand, RandomizeKeep.Nothing),
        ShortcutAction.RandomizeKeepSelf => new(gesture, RandomizeCommand, RandomizeKeep.Self),
        ShortcutAction.RandomizeKeepTeam => new(gesture, RandomizeCommand, RandomizeKeep.OwnTeam),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private void SetZoom(int index) => _settings.Update(s => s.ZoomIndex = ZoomLevels.Clamp(index));

    private int PageCount => ShowsEditors ? Pages.Count : Pages.Count - _editorPages;

    private void CyclePage(int step) => ShowPage(((CurrentPage + step) % PageCount + PageCount) % PageCount);

    private void ShowPage(int page)
    {
        if (page >= PageCount)
            return;
        Visit(page);
        IsSettingsOpen = false;
    }

    /// <summary>Opens a page as the newest history entry, dropping any the back button had stepped over, as a browser does.</summary>
    private void Visit(int page)
    {
        if (page != CurrentPage)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(page);
            _historyIndex = _history.Count - 1;
        }
        CurrentPage = page;
    }

    /// <summary>Steps back or forward through the pages opened, past any the Settings have hidden since.</summary>
    private void StepHistory(int step)
    {
        var index = _historyIndex + step;
        while (index >= 0 && index < _history.Count && _history[index] >= PageCount)
            index += step;
        if (index < 0 || index >= _history.Count)
            return;
        _historyIndex = index;
        CurrentPage = _history[index];
        IsSettingsOpen = false;
    }

    private void OpenSettings()
    {
        if (IsSettingsOpen)
            return;
        Settings.Refresh();
        IsSettingsOpen = true;
    }

    private void ShowFormula(string itemId)
    {
        if (!ShowsEditors)
            return;
        ShowPage(_itemFormulasPage);
        ItemFormulas.OpenItem(itemId);
    }

    private void ShowTrait(TraitCell cell)
    {
        if (!ShowsEditors)
            return;
        ShowPage(_heroTraitsPage);
        HeroTraits.OpenCell(cell);
    }

    /// <summary>The licenses of everything the app ships with: a long text, so it's written beside the settings and opened in the text editor.</summary>
    private void ShowNotices()
    {
        var path = Path.Combine(JsonSettingsService.AppDataPath, ThirdPartyNotices.FileName);
        try
        {
            AtomicFile.Write(path, ThirdPartyNotices.Bytes());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.ShowError($"Couldn't write {path}: {ex.Message}", ex);
            return;
        }
        DataMenu.OpenFolderCommand.Execute(path).Subscribe();
    }

    /// <summary>Ctrl+F belongs to whichever page is open; jumping back to Match would lose your place.</summary>
    private void Find()
    {
        if (Pages[CurrentPage] is not ISearchablePage page)
        {
            ShowPage(0);
            Match.FocusSearch();
            return;
        }
        ShowPage(CurrentPage);
        page.FocusSearch();
    }

    /// <summary>
    /// Detect onto the Match page, bringing the window forward only once there's something to see (the
    /// review or what went wrong): before the capture, it would cover the game.
    /// </summary>
    private async Task DetectFromAnywhereAsync()
    {
        // The review from the last press, or anything else holding detection up, is what you came for.
        if (_modals.IsModalOpen)
        {
            RequestViewAction(BringForwardAction);
            return;
        }

        ShowPage(0);
        // Only a review or a problem needs you here, and Settings can leave even those waiting behind
        // the game; a detection applied without review, or its progress, never pulls you out of it.
        using var surface = _settings.Current.ComeUpForReview
            ? _modals.ShowModalObservable
                .Where(modal => modal is not ProgressModalViewModel)
                .Take(1)
                .Subscribe(_ => RequestViewAction(BringForwardAction))
            : null;
        await Match.DetectCommand.Execute();
    }

    private void ReloadArt()
    {
        _art.Refresh();
        RequestViewAction(ArtChangedAction);
        _notifications.ShowInformation(
            $"Found {_art.Count(ArtKind.Hero)} portrait file(s) in {_art.FolderOf(ArtKind.Hero)}. Heroes without one keep their initials tile.",
            TimeSpan.FromSeconds(5));
    }
}
