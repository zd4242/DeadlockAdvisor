using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Match.Board;
using DeadlockAdvisor.Features.Match.Detect;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match;

/// <summary>A results cutoff: items scoring below this share of the best one are hidden.</summary>
public sealed record CutoffPreset(string Label, int Percent)
{
    public override string ToString() => Label;
}

/// <summary>The Match tab: the board on the left, recommendations on the right, and why the selected one scored what it did.</summary>
public class MatchViewModel : ViewModelBase, ISearchablePage
{
    public const int DefaultCutoffPercent = 40;

    // Relative to the best item rather than a fixed count or score, so the cutoff adapts to how many
    // heroes are picked and to a match where one item runs away with it.
    public static readonly IReadOnlyList<CutoffPreset> CutoffPresets =
    [
        new("All items", 0),
        new("≥ 20% of best", 20),
        new("≥ 40% of best", 40),
        new("≥ 60% of best", 60),
    ];

    private static readonly IReadOnlyDictionary<int, string> _laneTierLabels = new Dictionary<int, string>
    {
        [1] = "Tier 1 · 800",
        [2] = "Tier 2 · 1600",
    };

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly Func<double> _now;

    public MatchViewModel(IDataService data, ISettingsService settings, DetectAction detect)
        : this(data, settings, detect, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0)
    {
    }

    internal MatchViewModel(IDataService data, ISettingsService settings, DetectAction detect, Func<double> now)
    {
        _data = data;
        _settings = settings;
        _now = now;

        Match.LoadSaved(settings.Current.LastMatch, data.Store.Heroes.Keys);
        Board = new MatchBoardViewModel(Match, () => _data.Store);
        LaneResults = new ResultsViewModel(
            "Lane Phase scores only the heroes you've marked as being in your lane.\n\n"
            + "Set your own hero, then click the ally and the two enemies you're laning against in the match bar.",
            _laneTierLabels);
        FullResults = new ResultsViewModel("Pick the heroes in your match on the left and recommendations appear here.");
        FormulaRequested = LaneResults.FormulaRequested.Merge(FullResults.FormulaRequested);

        var savedPercent = settings.Current.ResultsMinPercent;
        SelectedCutoff = CutoffPresets.FirstOrDefault(p => p.Percent == savedPercent)
                         ?? CutoffPresets.First(p => p.Percent == DefaultCutoffPercent);
        ByTier = settings.Current.ResultsByTier;
        ApplyDisplay();

        DetectCommand = ReactiveCommand.CreateFromTask(() => detect.RunAsync(Match, () =>
        {
            Board.Refresh();
            OnMatchChanged();
        }));
        Board.DetectCommand = DetectCommand;

        Board.MatchChanged.Subscribe(_ => OnMatchChanged()).DisposeWith(Disposables);

        // Each view explains with its own hero restriction rather than the active tab's, so an
        // explanation can never describe a different scoping than the list the row was clicked in.
        LaneResults.RowClicked.Subscribe(itemId => ShowExplain(itemId, laneScoped: true)).DisposeWith(Disposables);
        FullResults.RowClicked.Subscribe(itemId => ShowExplain(itemId, laneScoped: false)).DisposeWith(Disposables);

        this.WhenAnyValue(vm => vm.ResultsTab)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsLaneTab));
                this.RaisePropertyChanged(nameof(IsFullTab));
                RefreshExplain();
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.SelectedCutoff, vm => vm.ByTier)
            .Skip(1)
            .Subscribe(_ =>
            {
                _settings.Update(s =>
                {
                    s.ResultsMinPercent = SelectedCutoff.Percent;
                    s.ResultsByTier = ByTier;
                });
                ApplyDisplay();
                // Trimming can drop the selected item, which clears the explanation.
                RefreshExplain();
            })
            .DisposeWith(Disposables);

        _data.ScoresChanged.Subscribe(_ => Refresh()).DisposeWith(Disposables);
        _data.StoreReplaced.Subscribe(_ => Rebind()).DisposeWith(Disposables);

        Refresh();
    }

    public MatchState Match { get; } = new();
    public MatchBoardViewModel Board { get; }
    public ResultsViewModel LaneResults { get; }
    public ResultsViewModel FullResults { get; }
    public ExplainViewModel Explain { get; } = new();

    /// <summary>An item whose rules a recommendation's context menu asked to open, from either tab.</summary>
    public IObservable<string> FormulaRequested { get; }

    /// <summary>0: Lane Phase, 1: Full Match.</summary>
    [Reactive] public int ResultsTab { get; set; }
    public bool IsLaneTab => ResultsTab == 0;
    public bool IsFullTab => ResultsTab == 1;
    [Reactive] public CutoffPreset SelectedCutoff { get; set; }
    [Reactive] public bool ByTier { get; set; }

    public IReadOnlyList<CutoffPreset> Cutoffs => CutoffPresets;

    public ReactiveCommand<Unit, Unit> DetectCommand { get; }

    public void FocusSearch() => RequestViewAction(MatchBoardViewModel.FocusSearchAction);

    /// <summary>Rescore both views and the explanation from the current data and match.</summary>
    public void Refresh()
    {
        var store = _data.Store;
        var note = MatchStatsMath.DataNote(store.MatchMeta, _now());
        LaneResults.SetResults(ItemScoring.LanePhaseResults(store, _data.Matrix, Match), note);
        FullResults.SetResults(ItemScoring.FullMatchResults(store, _data.Matrix, Match), note);
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
        LaneResults.Reset();
        FullResults.Reset();
        Board.Rebind();
        Refresh();
    }

    private void ApplyDisplay()
    {
        var fraction = SelectedCutoff.Percent / 100.0;
        LaneResults.SetDisplay(ByTier, fraction);
        FullResults.SetDisplay(ByTier, fraction);
    }

    private void RefreshExplain()
    {
        var laneScoped = ResultsTab == 0;
        ShowExplain((laneScoped ? LaneResults : FullResults).SelectedItemId, laneScoped);
    }

    private void ShowExplain(string? itemId, bool laneScoped)
    {
        var store = _data.Store;
        var restrictTo = laneScoped ? Match.LaneHeroes : null;
        IReadOnlyList<ScoredItem> picks = [];
        if (itemId is null)
        {
            var tiers = laneScoped ? ItemScoring.LaneTiers : ItemScoring.FullTiers;
            picks = ItemScoring.DataOnlyPicks(store, _data.Matrix, Match, tiers, restrictTo);
        }
        Explain.ShowItem(store, Match, itemId, restrictTo, picks, _now());
    }
}
