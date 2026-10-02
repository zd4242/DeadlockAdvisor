using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Board;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Features.Match.Import;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
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

public sealed record RankPreset(string Label, RankBy RankBy)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Match tab: the match bar, a hero picker that opens under it for fixing the match by hand, and
/// below them the recommendations beside why the selected one scored what it did.
/// </summary>
public class MatchViewModel : ViewModelBase, ISearchablePage
{
    public const int DefaultCutoffPercent = 40;

    public const string HideRarelyBuiltLabel = "Hide items your hero rarely builds";

    public const string FiltersTip = "Filters: which items are listed, how they're laid out, and which matches the data comes from";

    public static readonly string HideRarelyBuiltTip =
        $"Leave out the items marked RARELY BUILT: your hero builds them less than 1/{Format.Num(1 / ItemScoring.RareBuildRatio)} as often as the average player.\n"
        + "Needs your hero picked and match data downloaded (Data → Download Match Data).";

    public const string HideDisagreedLabel = "Hide items the formula and data disagree on";

    public const string HideDisagreedTip =
        "Leave out the items marked DISAGREE: the formula rates them well and the match data poorly, or the other way round.\n"
        + "Only when ranking by formula + match data, which puts the two on one scale.";

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
        new("Formula + match data", RankBy.Both),
        new("Rank by formula", RankBy.Formula),
        new("Rank by match data", RankBy.MatchData),
    ];

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly Func<double> _now;

    /// <summary>The rank option the list was last laid out for, so picking another can start on its best item.</summary>
    private RankPreset? _appliedRank;

    public MatchViewModel(IDataService data, ISettingsService settings, DetectAction detect, ImportMatchAction import, DataRanksViewModel dataRanks)
        : this(data, settings, detect, import, dataRanks, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0)
    {
    }

    internal MatchViewModel(IDataService data, ISettingsService settings, DetectAction detect, ImportMatchAction import, DataRanksViewModel dataRanks,
        Func<double> now)
    {
        _data = data;
        _settings = settings;
        _now = now;
        DataRanks = dataRanks.DisposeWith(Disposables);

        if (settings.Current.ReopenLastMatch)
            Match.LoadSaved(settings.Current.LastMatch, data.Store.Heroes.Keys);
        Board = new MatchBoardViewModel(Match, () => _data.Store, settings);
        Results = new ResultsViewModel("Pick the heroes in your match on the left and recommendations appear here.");

        var savedPercent = settings.Current.ResultsMinPercent;
        SelectedCutoff = CutoffPresets.FirstOrDefault(p => p.Percent == savedPercent)
                         ?? CutoffPresets.First(p => p.Percent == DefaultCutoffPercent);
        SelectedRank = RankPresets.FirstOrDefault(p => p.RankBy == settings.Current.ResultsRankBy) ?? RankPresets[0];
        HasMatchData = data.Store.MatchLift.Count > 0;
        ByTier = settings.Current.ResultsByTier;
        ByNetWorth = settings.Current.ResultsByNetWorth;
        HideRarelyBuilt = settings.Current.ResultsHideRarelyBuilt;
        HideDisagreed = settings.Current.ResultsHideDisagreed;
        ApplyDisplay();

        DetectCommand = ReactiveCommand.CreateFromTask(() => detect.RunAsync(Match, WhenReplaced()));
        Board.DetectCommand = DetectCommand;
        ReviewDetectionCommand = ReactiveCommand.Create(() => detect.ReviewLast(Match, WhenReplaced()), detect.CanReview);
        Board.ReviewDetectionCommand = ReviewDetectionCommand;
        detect.CanReview.Subscribe(can => Board.CanReviewDetection = can).DisposeWith(Disposables);
        // The match it applied is gone, so there's nothing left to review.
        Board.ClearCommand.Subscribe(_ => detect.ForgetLast()).DisposeWith(Disposables);
        ArtWanted = detect.ArtWanted;
        ImportCommand = ReactiveCommand.Create(() => import.Run(Match, WhenReplaced()));
        Board.ImportCommand = ImportCommand;

        Board.MatchChanged.Subscribe(_ => OnMatchChanged()).DisposeWith(Disposables);
        // Runs after the board's own rescore, so the list is already ranked for the new heroes.
        Board.RandomizeCommand.Subscribe(_ => ShowTopPick()).DisposeWith(Disposables);

        Results.RowClicked.Subscribe(ShowExplain).DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => s.ShowModelEditors)
            .DistinctUntilChanged()
            .Subscribe(show => Results.ShowsEditors = Explain.ShowsEditors = show)
            .DisposeWith(Disposables);
        settings.SettingsChanged
            .Select(s => s.ShowExplainMath)
            .DistinctUntilChanged()
            .Subscribe(show => Explain.ShowsMath = show)
            .DisposeWith(Disposables);

        this.WhenAnyValue(vm => vm.SelectedCutoff, vm => vm.ByTier, vm => vm.SelectedRank, vm => vm.HasMatchData, vm => vm.HideRarelyBuilt,
                vm => vm.HideDisagreed)
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
        this.WhenAnyValue(vm => vm.HideRarelyBuilt, vm => vm.HideDisagreed, vm => vm.RanksByBoth, vm => vm.DataRanks.RankedOnly,
                vm => vm.DataRanks.CanFilter, vm => vm.DataRanks.From, vm => vm.DataRanks.To)
            .Select(_ => ActiveFilters())
            .Subscribe(active =>
            {
                HasActiveFilters = active.Count > 0;
                FiltersButtonTip = HasActiveFilters ? $"{FiltersTip}\n\nOn now:\n• {string.Join("\n• ", active)}" : FiltersTip;
            })
            .DisposeWith(Disposables);

        _data.ScoresChanged.Subscribe(_ => Refresh()).DisposeWith(Disposables);
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

    [Reactive] public CutoffPreset SelectedCutoff { get; set; }
    [Reactive] public RankPreset SelectedRank { get; set; }

    /// <summary>The store has match data, so the results can rank by it.</summary>
    [Reactive] public bool HasMatchData { get; private set; }
    [Reactive] public bool ByTier { get; set; }

    /// <summary>Lean scores toward the heroes ahead on net worth, once the match has a reading.</summary>
    [Reactive] public bool ByNetWorth { get; set; }

    /// <summary>Leave out the items your hero rarely builds (<see cref="ScoredItem.RarelyBuilt"/>).</summary>
    [Reactive] public bool HideRarelyBuilt { get; set; }

    /// <summary>Leave out the items the formula and the data disagree on (<see cref="BlendScale.Disagree"/>), ranking by both.</summary>
    [Reactive] public bool HideDisagreed { get; set; }

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
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }

    /// <summary>Look back at a detection that was applied without review.</summary>
    public ReactiveCommand<Unit, Unit> ReviewDetectionCommand { get; }

    public void FocusSearch() => Board.OpenPicker();

    /// <summary>Rescore the recommendations and the explanation from the current data and match.</summary>
    public void Refresh()
    {
        var store = _data.Store;
        var note = MatchStatsMath.DataNote(store.MatchMeta, _now());
        var netWorth = NetWorth();
        HasMatchData = store.MatchLift.Count > 0;
        Results.SetResults(ItemScoring.ScoreAll(store, _data.Matrix, Match, netWorth), note, Scale);
        RefreshExplain();
    }

    private List<string> ActiveFilters()
    {
        var active = new List<string>();
        if (HideRarelyBuilt)
            active.Add("Items your hero rarely builds are hidden");
        if (HideDisagreed && RanksByBoth)
            active.Add("Items the formula and data disagree on are hidden");
        if (DataRanks is { CanFilter: true, RangeLabel: { } range })
            active.Add($"Match data leaning toward {range}");
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
    private Action WhenReplaced()
    {
        var before = LineUp();
        return () =>
        {
            Board.Refresh();
            OnMatchChanged();
            if (!before.SetEquals(LineUp()))
                ShowTopPick();
        };
    }

    private HashSet<(string HeroId, Role Role)> LineUp() =>
        Match.RoleMap.Where(entry => entry.Value != Role.None).Select(entry => (entry.Key, entry.Value)).ToHashSet();

    private void ShowTopPick()
    {
        Results.SelectTop();
        RefreshExplain();
    }

    private void OnMatchChanged()
    {
        Refresh();
        _settings.Update(s => s.LastMatch = Match.ToSaved());
    }

    /// <summary>Adopt a reloaded store: heroes and items may have changed.</summary>
    private void Rebind()
    {
        Match.LoadSaved(Match.ToSaved(), _data.Store.Heroes.Keys);
        Results.Reset();
        Board.Rebind();
        Refresh();
    }

    private void ApplyDisplay()
    {
        _appliedRank = SelectedRank;
        RanksByBoth = EffectiveRankBy == RankBy.Both;
        Results.SetDisplay(EffectiveRankBy, ByTier, SelectedCutoff.MinFraction, HideRarelyBuilt, HideDisagreed);
    }

    private void RefreshExplain() => ShowExplain(Results.SelectedItemId);

    private void ShowExplain(string? itemId) =>
        Explain.ShowItem(_data.Store, Match, itemId, _now(), NetWorth(), EffectiveRankBy, EffectiveRankBy == RankBy.Both ? Scale() : null);
}
