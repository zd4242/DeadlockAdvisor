using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match;

/// <summary>
/// Which ranks the match data covers. The download keeps each rank's totals apart, so a new range is
/// worked out on the spot and becomes the match data the whole app uses, until it's changed again.
/// </summary>
public class DataRanksViewModel : ViewModelBase
{
    private readonly IDataService _data;
    private readonly IMatchStatsService _matchStats;
    private readonly INotificationService _notifications;
    private bool _loading;

    public DataRanksViewModel(IDataService data, IMatchStatsService matchStats, INotificationService notifications)
    {
        _data = data;
        _matchStats = matchStats;
        _notifications = notifications;
        Load();

        this.WhenAnyValue(vm => vm.RankedOnly)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(EveryMatch)))
            .DisposeWith(Disposables);
        // Moving one end past the other drags the other along, so the range is never empty.
        this.WhenAnyValue(vm => vm.From)
            .Subscribe(from =>
            {
                if (from is not null && To is not null && To.Tier < from.Tier)
                    To = from;
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.To)
            .Subscribe(to =>
            {
                if (to is not null && From is not null && From.Tier > to.Tier)
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

    /// <summary>The match data was downloaded split by rank. Data from before that, or none, can't be filtered.</summary>
    [Reactive] public bool CanFilter { get; private set; }

    [Reactive] public IReadOnlyList<RankBucket> Ranks { get; private set; } = [];

    /// <summary>Only ranked matches from <see cref="From"/> to <see cref="To"/>; otherwise every match, ranked or not.</summary>
    [Reactive] public bool RankedOnly { get; set; }

    public bool EveryMatch
    {
        get => !RankedOnly;
        set => RankedOnly = !value;
    }

    [Reactive] public RankBucket? From { get; set; }
    [Reactive] public RankBucket? To { get; set; }

    /// <summary>The button: "Data: Mystic+".</summary>
    [Reactive] public string Label { get; private set; } = "";

    /// <summary>What the current range gave each family, or why a family was left out.</summary>
    [Reactive] public string Status { get; private set; } = "";

    private void Load()
    {
        _loading = true;
        try
        {
            var store = _data.Store;
            var range = MatchStatsMath.RankOf(store.MatchMeta);
            CanFilter = store.MatchCounts is not null;
            Ranks = store.MatchCounts?.Ranks ?? [];
            RankedOnly = range is not null;
            From = Ranks.FirstOrDefault(rank => rank.Tier == range?.Min) ?? Ranks.FirstOrDefault();
            To = Ranks.FirstOrDefault(rank => rank.Tier == range?.Max) ?? Ranks.LastOrDefault();
            Label = $"Data: {MatchStatsMath.RankLabel(store.MatchMeta) ?? "every match"}";
            Status = string.Join("\n", MatchStatsMath.FamilyLines(store.MatchMeta));
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
        var range = RankedOnly ? new RankRange(From!.Tier, To!.Tier) : null;
        var store = _data.Store;
        if (store.MatchCounts is null || range == MatchStatsMath.RankOf(store.MatchMeta))
            return;

        try
        {
            _matchStats.Refilter(store, range);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The store has the new lifts all the same; only writing them out failed.
            _notifications.ShowError($"Writing the match data to {_data.DataDir} failed: {ex.Message}", ex);
        }
        _data.NotifyReplaced();
    }
}
