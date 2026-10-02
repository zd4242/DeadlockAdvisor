using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match;

/// <summary>
/// Which ranks the match data leans toward. The download keeps each rank group's totals apart, so a new
/// range is worked out on the spot and becomes the match data the whole app uses, until it's changed again.
/// </summary>
public class DataRanksViewModel : ViewModelBase
{
    public const string Info =
        "The numbers stay every match's, and move toward the ranks you pick only where those ranks play detectably\n"
        + "differently, so a thin range can't empty them: Phantom+ has under a tenth of the matches.\n"
        + "A match's rank is both teams' average, in groups of two ranks; the last takes Ascendant and Eternus.";

    private readonly IDataService _data;
    private readonly IMatchStatsService _matchStats;
    private readonly INotificationService _notifications;
    private readonly Subject<Unit> _ranksWanted = new();
    private bool _loading;

    public DataRanksViewModel(IDataService data, IMatchStatsService matchStats, INotificationService notifications)
    {
        _data = data;
        _matchStats = matchStats;
        _notifications = notifications;
        DownloadRanksCommand = ReactiveCommand.Create(() => _ranksWanted.OnNext(Unit.Default));
        Load();

        this.WhenAnyValue(vm => vm.RankedOnly)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(EveryMatch)))
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.RankedOnly, vm => vm.From, vm => vm.To)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(RangeLabel)))
            .DisposeWith(Disposables);
        // Moving one end past the other drags the other along, so the range is never empty.
        this.WhenAnyValue(vm => vm.From)
            .Subscribe(from =>
            {
                if (from is not null && To is not null && To.FirstTier < from.FirstTier)
                    To = from;
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.To)
            .Subscribe(to =>
            {
                if (to is not null && From is not null && From.FirstTier > to.FirstTier)
                    From = to;
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.RankedOnly, vm => vm.From, vm => vm.To)
            .Skip(1)
            .Where(_ => !_loading)
            .Subscribe(_ => Apply())
            .DisposeWith(Disposables);
        data.StoreReplaced.Subscribe(_ => Load()).DisposeWith(Disposables);
    }

    /// <summary>The match data was downloaded with its rank groups. Data from before that, or none, can't lean.</summary>
    [Reactive] public bool CanFilter { get; private set; }

    /// <summary>Asks for a download with the rank groups, which leaning needs.</summary>
    public ReactiveCommand<Unit, Unit> DownloadRanksCommand { get; }

    public IObservable<Unit> RanksWanted => _ranksWanted.AsObservable();

    [Reactive] public IReadOnlyList<RankBucket> Ranks { get; private set; } = [];

    /// <summary>Lean toward the ranks from <see cref="From"/> to <see cref="To"/>; otherwise every match's numbers as they are.</summary>
    [Reactive] public bool RankedOnly { get; set; }

    public bool EveryMatch
    {
        get => !RankedOnly;
        set => RankedOnly = !value;
    }

    [Reactive] public RankBucket? From { get; set; }
    [Reactive] public RankBucket? To { get; set; }

    /// <summary>"Mystic+": the range leaned toward; null over every match.</summary>
    public string? RangeLabel => RankedOnly && From is not null && To is not null
        ? MatchStatsMath.DescribeRange(Ranks, new RankRange(From.FirstTier, To.LastTier))
        : null;

    /// <summary>What each family kept, or why it was left out, and how far the range moved it.</summary>
    [Reactive] public string Status { get; private set; } = "";

    private void Load()
    {
        _loading = true;
        try
        {
            var store = _data.Store;
            var range = MatchStatsMath.RankOf(store.MatchMeta);
            Ranks = MatchStatsMath.RanksOf(store.MatchSegments);
            CanFilter = Ranks.Count > 0;
            RankedOnly = range is not null;
            From = Ranks.FirstOrDefault(rank => range is not null && rank.Overlaps(range)) ?? Ranks.FirstOrDefault();
            To = Ranks.LastOrDefault(rank => range is not null && rank.Overlaps(range)) ?? Ranks.LastOrDefault();
            Status = string.Join("\n", [.. MatchStatsMath.FamilyLines(store.MatchMeta), .. MatchStatsMath.LeanLines(store.MatchMeta)]);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply()
    {
        if (RankedOnly && (From is null || To is null))
            return;
        var range = RankedOnly ? new RankRange(From!.FirstTier, To!.LastTier) : null;
        var store = _data.Store;
        if (!CanFilter || range == MatchStatsMath.RankOf(store.MatchMeta))
            return;

        try
        {
            _matchStats.Reanalyse(store, range);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The store has the new lifts all the same; only writing them out failed.
            _notifications.ShowError($"Writing the match data to {_data.DataDir} failed: {ex.Message}", ex);
        }
        _data.NotifyReplaced();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _ranksWanted.Dispose();
        base.Dispose(disposing);
    }
}