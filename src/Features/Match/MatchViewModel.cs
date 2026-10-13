using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.HeroTraits;
using DeadlockAdvisor.Features.Match.Board;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Features.Match.Import;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match;

/// <summary>A results cutoff: items scoring below this share of the best one are hidden. A negative percent keeps every item.</summary>
public sealed record CutoffPreset(string Label, int Percent)
{
    public const int EveryItem = -1;

    /// <summary>The share of the best item's measure to keep, or null to keep every item however it scores.</summary>
    public double? MinFraction => Percent < 0 ? null : Percent / 100.0;

    public override string ToString() => Label;
}

/// <summary>A ranking choice. The closed box shows <see cref="Label"/>; the open list shows <see cref="MenuLabel"/>, which says which is recommended.</summary>
public sealed record RankPreset(string Label, RankBy RankBy, bool Recommended = false)
{
    public string MenuLabel => Recommended ? $"{Label} (recommended)" : Label;

    public override string ToString() => Label;
}

/// <summary>
/// The Match tab: the match bar, a hero picker that opens under it for fixing the match by hand, and
/// below them the recommendations beside why the selected one scored what it did.
/// </summary>
public class MatchViewModel : ViewModelBase, ISearchablePage
{
    public const int DefaultCutoffPercent = 40;
    public const string FocusItemSearchAction = "FocusItemSearch";

    public const string HideRarelyBuiltLabel = "Hide items your hero rarely builds";

    public const string HideOwnedLabel = "Hide items you already own";

    public const string HideOwnedTip =
        "Leave out the items marked OWNED: the ones your hero holds, so the list answers \"what next\". Their scores don't change.\n"
        + "Needs your hero picked and the scoreboard read (Settings → Detection → Read the scoreboard while you hold its key).";

    public const string ByOverlapLabel = "Discount what your team already has";

    public const string ByOverlapTip =
        "An effect that lands on other heroes doesn't stack with the same item's or the same build's: a second Heroic Aura, or a Spirit Resist\n"
        + "shred on a build whose shred is already there, counts for what it adds over the first. Items that only share the effect still stack.\n"
        + "Needs the scoreboard read (Settings → Detection → Read the scoreboard while you hold its key).";

    public const string ByBuildsLabel = "Lean toward what heroes have built";

    public const string ByBuildsTip =
        "A hero who has built mostly weapon items counts as more of a bullet-damage hero in this match, and one who has built mostly spirit items\n"
        + "as more of a spirit-damage one, so the items that answer them follow. It is measured against what that hero usually builds, so a hero\n"
        + "who always builds the same barely moves. Needs the scoreboard read (Settings → Detection) and the match results downloaded.\n"
        + "What an item pushes toward comes from its stats and the debuffs it puts on enemies, so a spirit item that lowers Bullet Resist counts toward bullets.";

    public const string FiltersTip ="Filters: which items are listed, how they're laid out, and which matches the results come from";

    public static readonly string HideRarelyBuiltTip =
        $"Leave out the items marked RARELY BUILT: your hero builds them less than 1/{Format.Num(1 / ItemScoring.RareBuildRatio)} as often as the average player.\n"
        + "Needs your hero picked and match results downloaded (Data → Check for Updates).";

    public const string HideDisagreedLabel = "Hide items the advisor rating and results disagree on";

    public const string HideDisagreedTip =
        "Leave out the items marked DISAGREE: the advisor rating rates them well and the match results poorly, or the other way round.\n"
        + "Only when ranking by advisor rating + match results, which puts the two on one scale.";

    // Relative to the best item rather than a fixed count or score, so the cutoff adapts to how many
    // heroes are picked and to a match where one item runs away with it.
    public static readonly IReadOnlyList<CutoffPreset> CutoffPresets =
    [
        new("Every item", CutoffPreset.EveryItem),
        new("All above 0", 0),
        new("≥ 20% of best", 20),
        new("≥ 40% of best", 40),
        new("≥ 60% of best", 60),
    ];

    public static readonly IReadOnlyList<RankPreset> RankPresets =
    [
        new("Advisor rating + match results", RankBy.Both, Recommended: true),
        new("Advisor rating", RankBy.Formula),
        new("Match results", RankBy.MatchData),
    ];

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly DetectAction _detect;
    private readonly Func<double> _now;

    /// <summary>The rank option the list was last laid out for, so picking another can start on its best item.</summary>
    private RankPreset? _appliedRank;

    private readonly MatchHistory _history;
    private bool _shown = true;
    private bool _stale;

    public MatchViewModel(IDataService data, ISettingsService settings, DetectAction detect, ImportMatchAction import, DataRanksViewModel dataRanks)
        : this(data, settings, detect, import, dataRanks, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0)
    {
    }

    internal MatchViewModel(IDataService data, ISettingsService settings, DetectAction detect, ImportMatchAction import, DataRanksViewModel dataRanks,
        Func<double> now)
    {
        _data = data;
        _settings = settings;
        _detect = detect;
        _now = now;
        DataRanks = dataRanks.DisposeWith(Disposables);

        if (settings.Current.ReopenLastMatch)
            Match.LoadSaved(settings.Current.LastMatch, data.Store.Heroes.Keys, data.Store.Items.Keys);
        _history = new MatchHistory(Match.Picks());
        Board = new MatchBoardViewModel(Match, () => _data.Store, settings, () => DateTimeOffset.FromUnixTimeMilliseconds((long)(_now() * 1000)));
        Results = new ResultsViewModel("Pick the heroes in your match above and recommendations appear here.",
            (item, needle) => _data.Store.ItemMatches(item.ItemId, needle));

        var savedPercent = settings.Current.ResultsMinPercent;
        SelectedCutoff = CutoffPresets.FirstOrDefault(p => p.Percent == savedPercent)
                         ?? CutoffPresets.First(p => p.Percent == DefaultCutoffPercent);
        SelectedRank = RankPresets.FirstOrDefault(p => p.RankBy == settings.Current.ResultsRankBy) ?? RankPresets[0];
        HasMatchData = data.Store.MatchLift.Count > 0;
        ByTier = settings.Current.ResultsByTier;
        ByNetWorth = settings.Current.ResultsByNetWorth;
        ByOverlap = settings.Current.ResultsByOverlap;
        ByBuilds = settings.Current.ResultsByBuilds;
        ShowTiers = Results.ShowsTiers = settings.Current.ResultsShowTiers;
        HideRarelyBuilt = settings.Current.ResultsHideRarelyBuilt;
        HideDisagreed = settings.Current.ResultsHideDisagreed;
        HideOwned = settings.Current.ResultsHideOwned;
        ApplyDisplay();

        DetectCommand = ReactiveCommand.CreateFromTask(() => detect.RunAsync(Match, WhenReplaced()));
        Board.DetectCommand = DetectCommand;
        ReviewDetectionCommand = ReactiveCommand.Create(() => detect.ReviewLast(Match, WhenReplaced()), detect.CanReview);
        Board.ReviewDetectionCommand = ReviewDetectionCommand;
        detect.CanReview.Subscribe(can => Board.CanReviewDetection = can).DisposeWith(Disposables);
        // The match it applied is gone, so there's nothing left to review.
        Board.ClearCommand.Subscribe(_ =>
        {
            detect.ForgetLast();
            Results.ResetSearch();
        }).DisposeWith(Disposables);
        ArtWanted = detect.ArtWanted;
        DetectFinished = detect.Finished;
        ImportCommand = ReactiveCommand.Create(() => import.Run(Match, WhenReplaced()));
        Board.ImportCommand = ImportCommand;
        UndoCommand = ReactiveCommand.Create(() => StepHistory(_history.Undo), this.WhenAnyValue(vm => vm.CanUndo));
        RedoCommand = ReactiveCommand.Create(() => StepHistory(_history.Redo), this.WhenAnyValue(vm => vm.CanRedo));
        Board.UndoCommand = UndoCommand;
        Board.RedoCommand = RedoCommand;
        PickHeroesCommand = ReactiveCommand.Create(Board.OpenPicker);
        ToggleItemSearchCommand = ReactiveCommand.Create(() =>
        {
            if (Results.IsSearchOpen)
                Results.ResetSearch();
            else
                OpenItemSearch();
        });

        Board.MatchChanged.Subscribe(_ => OnMatchChanged()).DisposeWith(Disposables);
        // Runs after the board's own rescore, so the list is already ranked for the new heroes.
        Board.RandomizeCommand.Subscribe(_ => StartOnBestItem()).DisposeWith(Disposables);

        Results.RowClicked.Subscribe(ShowExplain).DisposeWith(Disposables);
        Results.HideDisagreedRequested.Subscribe(_ => HideDisagreed = true).DisposeWith(Disposables);
        Results.HideRarelyBuiltRequested.Subscribe(_ => HideRarelyBuilt = true).DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => s.ShowModelEditors)
            .DistinctUntilChanged()
            .Subscribe(show => Results.ShowsEditors = Explain.ShowsEditors = show)
            .DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => s.ReadBuildsOnTab)
            .DistinctUntilChanged()
            .Skip(1)
            .Subscribe(_ => RefreshWhenShown())
            .DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => s.ShowExplainMath)
            .DistinctUntilChanged()
            .Subscribe(show => Explain.ShowsMath = show)
            .DisposeWith(Disposables);

        this.WhenAnyValue(vm => vm.SelectedCutoff, vm => vm.ByTier, vm => vm.SelectedRank, vm => vm.HasMatchData, vm => vm.HideRarelyBuilt,
                vm => vm.HideDisagreed, vm => vm.HideOwned)
            .Skip(1)
            .Subscribe(_ =>
            {
                _settings.Update(s =>
                {
                    s.ResultsMinPercent = SelectedCutoff.Percent;
                    s.ResultsByTier = ByTier;
                    s.ResultsRankBy = SelectedRank.RankBy;
                    s.ResultsHideRarelyBuilt = HideRarelyBuilt;
                    s.ResultsHideDisagreed = HideDisagreed;
                    s.ResultsHideOwned = HideOwned;
                });
                var reranked = SelectedRank != _appliedRank;
                ApplyDisplay();
                // A new ranking starts on its best item. Otherwise trimming can drop the selected item, which clears the explanation.
                if (reranked)
                    ShowTopPick();
                else
                    RefreshExplain();
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.ByNetWorth)
            .Skip(1)
            .Subscribe(_ =>
            {
                _settings.Update(s => s.ResultsByNetWorth = ByNetWorth);
                Refresh();
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.ByOverlap)
            .Skip(1)
            .Subscribe(_ =>
            {
                _settings.Update(s => s.ResultsByOverlap = ByOverlap);
                Refresh();
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.ByBuilds)
            .Skip(1)
            .Subscribe(_ =>
            {
                _settings.Update(s => s.ResultsByBuilds = ByBuilds);
                Refresh();
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.ShowTiers)
            .Skip(1)
            .Subscribe(show =>
            {
                _settings.Update(s => s.ResultsShowTiers = show);
                Results.ShowsTiers = show;
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.HideRarelyBuilt, vm => vm.HideDisagreed, vm => vm.HideOwned, vm => vm.RanksByBoth)
            .Select(_ => Unit.Default)
            .Merge(this.WhenAnyValue(vm => vm.DataRanks.RankedOnly, vm => vm.DataRanks.CanFilter, vm => vm.DataRanks.From, vm => vm.DataRanks.To)
                .Select(_ => Unit.Default))
            .Select(_ => ActiveFilters())
            .Subscribe(active =>
            {
                HasActiveFilters = active.Count > 0;
                FiltersButtonTip = HasActiveFilters ? $"{FiltersTip}\n\nOn now:\n• {string.Join("\n• ", active)}" : FiltersTip;
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(vm => vm.IsMatchEmpty, vm => vm.Board.IsPickerOpen)
            .Subscribe(_ => ShowsQuickStart = IsMatchEmpty && !Board.IsPickerOpen)
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.ShowsQuickStart, vm => vm.NeedsSelf)
            .Subscribe(_ => ShowsResults = !ShowsQuickStart && !NeedsSelf)
            .DisposeWith(Disposables);

        _data.ScoresChanged.Subscribe(_ => RefreshWhenShown()).DisposeWith(Disposables);
        _data.StoreReplaced.Subscribe(_ => Rebind()).DisposeWith(Disposables);

        Refresh();
    }

    public MatchState Match { get; } = new();
    public MatchBoardViewModel Board { get; }
    public ResultsViewModel Results { get; }
    public ExplainViewModel Explain { get; } = new();
    public DataRanksViewModel DataRanks { get; }

    /// <summary>An item whose rules a recommendation's or the explanation's context menu asked to open.</summary>
    public IObservable<string> FormulaRequested => Results.FormulaRequested.Merge(Explain.FormulaRequested);

    /// <summary>A hero's trait whose cell on the Hero Traits page the explanation's context menu asked to open.</summary>
    public IObservable<TraitCell> TraitRequested => Explain.TraitRequested;

    [Reactive] public CutoffPreset SelectedCutoff { get; set; }
    [Reactive] public RankPreset SelectedRank { get; set; }

    /// <summary>The store has match data, so the results can rank by it.</summary>
    [Reactive] public bool HasMatchData { get; private set; }
    [Reactive] public bool ByTier { get; set; }

    /// <summary>Each recommendation names its shop tier and price (<see cref="ResultsViewModel.ShowsTiers"/>).</summary>
    [Reactive] public bool ShowTiers { get; set; }

    /// <summary>Lean scores toward the heroes ahead on net worth, once the match has a reading.</summary>
    [Reactive] public bool ByNetWorth { get; set; }

    /// <summary>Count an item's debuff or aura for what your team doesn't already have of it (<see cref="TeamOverlap"/>).</summary>
    [Reactive] public bool ByOverlap { get; set; }

    /// <summary>The scoreboard has shown what some hero holds, so there is something to discount by.</summary>
    [Reactive] public bool HasBuilds { get; private set; }

    /// <summary>Move a hero's bullet and spirit damage by what they have built against what they usually build (<see cref="BuildShifts"/>).</summary>
    [Reactive] public bool ByBuilds { get; set; }

    /// <summary>Builds have been read and the downloaded counts give the heroes' usual builds, so <see cref="ByBuilds"/> has something to work with.</summary>
    [Reactive] public bool CanLeanOnBuilds { get; private set; }

    /// <summary>
    /// Two teammates hold items whose effect doesn't add up, newly seen in a scoreboard read: one sentence for each, for the
    /// window to say aloud. Not raised for the first look at a match the app was opened with.
    /// </summary>
    public IObservable<string> OverlapNotices => _overlapNotices;
    private readonly Subject<string> _overlapNotices = new();
    private HashSet<string> _toldOverlaps = [];

    /// <summary>Leave out the items your hero rarely builds (<see cref="ScoredItem.RarelyBuilt"/>).</summary>
    [Reactive] public bool HideRarelyBuilt { get; set; }

    /// <summary>Leave out the items the formula and the data disagree on (<see cref="BlendScale.Disagree"/>), ranking by both.</summary>
    [Reactive] public bool HideDisagreed { get; set; }

    /// <summary>Leave out the items your hero already holds, from the last scoreboard read. Scores are untouched.</summary>
    [Reactive] public bool HideOwned { get; set; }

    /// <summary>Your hero's items have been read, so there is something to mark and to hide.</summary>
    [Reactive] public bool HasOwnedItems { get; private set; }

    /// <summary>The list adds the formula and the data together, the only ranking that can tell when they disagree.</summary>
    [Reactive] public bool RanksByBoth { get; private set; }

    /// <summary>
    /// A filter is changing the list in a way it doesn't show, so the filters button lights up and its
    /// tip says which. The cutoff shows in the summary line and the tiers in the list, so they don't count.
    /// </summary>
    [Reactive] public bool HasActiveFilters { get; private set; }
    [Reactive] public string FiltersButtonTip { get; private set; } = FiltersTip;

    public IReadOnlyList<CutoffPreset> Cutoffs => CutoffPresets;
    public IReadOnlyList<RankPreset> Ranks => RankPresets;

    public ReactiveCommand<Unit, Unit> DetectCommand { get; }

    /// <summary>Detect found no art to match against and was asked to download it.</summary>
    public IObservable<Unit> ArtWanted { get; }

    /// <summary>How each Detect run ended.</summary>
    public IObservable<DetectOutcome> DetectFinished { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }

    /// <summary>Step back through the line-ups the match has been through, however they were made, and forward again.</summary>
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    [Reactive] public bool CanUndo { get; private set; }
    [Reactive] public bool CanRedo { get; private set; }

    /// <summary>Open the hero picker, from the empty match's quick start.</summary>
    public ReactiveCommand<Unit, Unit> PickHeroesCommand { get; }

    /// <summary>No hero is in the match yet.</summary>
    [Reactive] public bool IsMatchEmpty { get; private set; }

    /// <summary>
    /// The results panel offers the ways to fill the match in place of an empty list: nothing is picked,
    /// and the picker isn't already open for it.
    /// </summary>
    [Reactive] public bool ShowsQuickStart { get; private set; }

    /// <summary>
    /// Every hero is placed from the top bar but the teams aren't split: the results panel asks which hero is
    /// you instead of listing items nobody could score yet.
    /// </summary>
    [Reactive] public bool NeedsSelf { get; private set; }

    /// <summary>The results list is what the panel shows: neither the quick start nor the question of who you are.</summary>
    [Reactive] public bool ShowsResults { get; private set; }

    /// <summary>There is a line-up to search the items of.</summary>
    [Reactive] public bool CanSearchItems { get; private set; }

    /// <summary>Look back at a detection that was applied without review.</summary>
    public ReactiveCommand<Unit, Unit> ReviewDetectionCommand { get; }

    /// <summary>Show the item search, or put it away (clearing it), from its button.</summary>
    public ReactiveCommand<Unit, Unit> ToggleItemSearchCommand { get; }

    /// <summary>
    /// Ctrl+F searches the items once there's a match to rank them for; before that, or while the hero
    /// picker is already open, it goes to the heroes.
    /// </summary>
    public void FocusSearch()
    {
        if (NeedsSelf)
            Board.StartAssigning(Role.Self);
        else if (IsMatchEmpty || Board.IsPickerOpen)
            Board.OpenPicker();
        else
            OpenItemSearch();
    }

    private void OpenItemSearch()
    {
        Results.IsSearchOpen = true;
        RequestViewAction(FocusItemSearchAction);
    }

    /// <summary>
    /// Whether the page is on screen. While it isn't, a rescore of the formulas only notes that the list is stale
    /// and the next time it shows it is rescored once; a changed match or store still refreshes at once.
    /// </summary>
    public void SetShown(bool shown)
    {
        _shown = shown;
        if (shown && _stale)
            Refresh();
    }

    private void RefreshWhenShown()
    {
        if (_shown)
            Refresh();
        else
            _stale = true;
    }

    /// <summary>Rescore the recommendations and the explanation from the current data and match.</summary>
    public void Refresh()
    {
        _stale = false;
        var store = _data.Store;
        var note = MatchStatsMath.DataNote(store.MatchMeta, _now());
        var netWorth = NetWorth();
        HasMatchData = store.MatchLift.Count > 0;
        IsMatchEmpty = Match.IsEmpty;
        NeedsSelf = Match.HasUnsided;
        CanSearchItems = !IsMatchEmpty && !NeedsSelf;
        var owned = OwnedItems();
        HasOwnedItems = owned.Count > 0;
        HasBuilds = _settings.Current.ReadBuildsOnTab && !Match.Builds.IsEmpty;
        var team = _settings.Current.ReadBuildsOnTab ? TeamOverlap.For(store, Match) : TeamOverlap.None;
        _overlap = ByOverlap ? team : TeamOverlap.None;
        CanLeanOnBuilds = HasBuilds && store.TypicalBuilds.Count > 0;
        _shifts = ByBuilds && CanLeanOnBuilds ? BuildShifts.For(store, Match) : BuildShifts.None;
        Results.SetResults(ItemScoring.ScoreAll(store, _data.Matrix, Match, netWorth, _overlap, _shifts), note, Scale, owned);
        RefreshExplain();
        TellOverlaps(store, team);
    }

    /// <summary>What the team already has of the effects items put on others, for scoring and the explanation; none while the filter is off.</summary>
    private TeamOverlap _overlap = TeamOverlap.None;

    /// <summary>What the heroes' builds did to their damage mix, for scoring and the explanation; none while the filter is off.</summary>
    private BuildShifts _shifts = BuildShifts.None;

    private void TellOverlaps(DataStore store, TeamOverlap team)
    {
        var shared = team.Shared();
        var told = _toldOverlaps;
        _toldOverlaps = shared.Select(effect => effect.Key).ToHashSet();
        foreach (var effect in shared.Where(effect => !told.Contains(effect.Key)))
            _overlapNotices.OnNext(ExplainText.SharedNotice(store, Match, effect));
    }

    /// <summary>What your hero holds, as far as the scoreboard has shown it; empty without a read or a hero, and while reading the scoreboard is off.</summary>
    private HashSet<string> OwnedItems() =>
        _settings.Current.ReadBuildsOnTab ? Match.BuildOf(Match.SelfHero)?.ItemIds.ToHashSet() ?? [] : [];

    private List<string> ActiveFilters()
    {
        var active = new List<string>();
        if (HideRarelyBuilt)
            active.Add("Items your hero rarely builds are hidden");
        if (HideOwned)
            active.Add("Items you already own are hidden");
        if (HideDisagreed && RanksByBoth)
            active.Add("Items the advisor rating and results disagree on are hidden");
        if (DataRanks is { CanFilter: true, RangeLabel: { } range })
            active.Add($"Match results leaning toward {range}");
        return active;
    }

    private NetWorthWeights NetWorth() => ByNetWorth ? NetWorthWeights.For(Match) : NetWorthWeights.None;

    /// <summary>What the lists rank by: without match data, ranking by it would empty them.</summary>
    private RankBy EffectiveRankBy => HasMatchData ? SelectedRank.RankBy : RankBy.Formula;

    /// <summary>The formula-and-data ranking's units for the current line-up.</summary>
    private BlendScale Scale() => _data.Scales.For(LineUpShape.Of(ItemScoring.RelevantHeroes(Match)));

    /// <summary>
    /// What to do once a detection or an import writes the match from outside the board. A new
    /// line-up starts on its best item; re-reading the same one, say for net worth, keeps your pick.
    /// </summary>
    /// <summary>The match changed outside the page (the scoreboard read added a net worth reading): show it.</summary>
    public void MatchUpdated() => WhenReplaced()();

    private Action WhenReplaced()
    {
        var before = LineUp();
        return () =>
        {
            Board.Refresh();
            OnMatchChanged();
            if (!before.SetEquals(LineUp()))
                StartOnBestItem();
        };
    }

    private HashSet<(string HeroId, Role Role)> LineUp() =>
        Match.RoleMap.Where(entry => entry.Value != Role.None).Select(entry => (entry.Key, entry.Value)).ToHashSet();

    /// <summary>A whole new match starts on its best item, unless a search is on: it stays, and so does the pick.</summary>
    private void StartOnBestItem()
    {
        if (!Results.IsSearching)
            ShowTopPick();
    }

    private void ShowTopPick()
    {
        Results.SelectTop();
        RefreshExplain();
    }

    private void StepHistory(Func<MatchPicks?> step)
    {
        var replaced = WhenReplaced();
        if (step() is not { } picks)
            return;
        Match.Restore(picks, _data.Store.Heroes.Keys);
        replaced();
    }

    private void OnMatchChanged()
    {
        _history.Record(Match.Picks());
        (CanUndo, CanRedo) = (_history.CanUndo, _history.CanRedo);
        var pickedSelf = NeedsSelf && !Match.HasUnsided;
        Refresh();
        // Picking yourself builds the line-up, so it opens on its best item like any new one.
        if (pickedSelf)
            StartOnBestItem();
        _detect.LabelSelf(Match);
        _settings.Update(s => s.LastMatch = Match.ToSaved());
    }

    /// <summary>Adopt a reloaded store: heroes and items may have changed.</summary>
    private void Rebind()
    {
        Match.LoadSaved(Match.ToSaved(), _data.Store.Heroes.Keys, _data.Store.Items.Keys);
        _history.Reset(Match.Picks());
        (CanUndo, CanRedo) = (false, false);
        Results.Reset();
        Board.Rebind();
        Refresh();
    }

    private void ApplyDisplay()
    {
        _appliedRank = SelectedRank;
        RanksByBoth = EffectiveRankBy == RankBy.Both;
        Results.SetDisplay(EffectiveRankBy, ByTier, SelectedCutoff.MinFraction, HideRarelyBuilt, HideDisagreed, HideOwned);
    }

    private void RefreshExplain() => ShowExplain(Results.SelectedItemId);

    private void ShowExplain(string? itemId) =>
        Explain.ShowItem(_data.Store, Match, itemId, _now(), NetWorth(), EffectiveRankBy, EffectiveRankBy == RankBy.Both ? Scale() : null,
            owned: itemId is not null && OwnedItems().Contains(itemId), overlap: _overlap, shifts: _shifts);
}
