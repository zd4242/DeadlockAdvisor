using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.HeroItems;

/// <summary>What the table can be ordered by: one per column.</summary>
public enum HeroItemSort
{
    Item,
    Cost,
    WinRate,
    WinRateChange,
    Usage,
    UsageChange,
    Matches,
}

/// <summary>A match mode on offer, by name.</summary>
public sealed record ModeOption(MatchMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A patch on offer: its counts, by date.</summary>
public sealed record PatchOption(MatchSegment Segment, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A column's header, which sorts by it: "Usage ▾" while the table is sorted by it.</summary>
public class ColumnHeader(HeroItemSort column, string title) : ReactiveObject
{
    public HeroItemSort Column { get; } = column;
    public string Title { get; } = title;
    [Reactive] public string Text { get; private set; } = "";

    public void Show(HeroItemSort sorted, bool descending) => Text = Column == sorted ? $"{Title} {(descending ? "▾" : "▴")}" : Title;
}

/// <summary>A tier's filter pill: every tier shows until it's switched off.</summary>
public class TierToggle(int tier) : ReactiveObject
{
    public int Tier { get; } = tier;
    public string Label => $"T{Tier}";
    [Reactive] public bool IsChecked { get; set; } = true;
}

/// <summary>
/// One hero's items in real matches, as sites like tracklock.gg show them: every item its players bought
/// in a patch, with its win rate and how often it's bought, and how both moved since the patch before.
/// Unlike them, items bought in only a few of the hero's matches can be hidden, since their win rates
/// rest on too few matches to mean much.
/// </summary>
public class HeroItemsViewModel : ViewModelBase
{
    public const string MinUsageTip =
        "Hide items bought in fewer of this hero's matches than this.\n"
        + "A rarely bought item's win rate rests on few matches, and on who buys it, so it says little.";

    public const string ChangeTip = "Since the patch before, in percentage points.";

    public const double MaxMinUsagePercent = 50;

    public static readonly IReadOnlyList<ModeOption> Modes =
    [
        new(MatchMode.Ranked, "Ranked"),
        new(MatchMode.Unranked, "Unranked"),
        new(MatchMode.All, "Ranked and unranked"),
    ];

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private string? _offeredSelf;
    private bool _loading;

    public HeroItemsViewModel(IDataService data, ISettingsService settings)
    {
        _data = data;
        _settings = settings;
        Tiers = Enumerable.Range(1, 4).Select(tier => new TierToggle(tier)).ToList();
        Headers = new Dictionary<HeroItemSort, ColumnHeader>
        {
            [HeroItemSort.Item] = new(HeroItemSort.Item, "Item"),
            [HeroItemSort.Cost] = new(HeroItemSort.Cost, "Cost"),
            [HeroItemSort.WinRate] = new(HeroItemSort.WinRate, "Win rate"),
            [HeroItemSort.WinRateChange] = new(HeroItemSort.WinRateChange, "Change"),
            [HeroItemSort.Usage] = new(HeroItemSort.Usage, "Usage"),
            [HeroItemSort.UsageChange] = new(HeroItemSort.UsageChange, "Change"),
            [HeroItemSort.Matches] = new(HeroItemSort.Matches, "Won / lost"),
        };
        ShowSort();
        SortCommand = ReactiveCommand.Create<HeroItemSort>(Sort);

        _loading = true;
        SelectedMode = Modes.FirstOrDefault(option => option.Mode == settings.Current.HeroItemsMode) ?? Modes[0];
        MinUsagePercent = Math.Clamp(settings.Current.HeroItemsMinUsagePercent, 0, MaxMinUsagePercent);
        Load(settings.Current.HeroItemsHero);
        _loading = false;

        this.WhenAnyValue(vm => vm.SelectedHero, vm => vm.SelectedPatch, vm => vm.SelectedMode, vm => vm.From, vm => vm.To,
                vm => vm.MinUsagePercent)
            .Skip(1)
            .Where(_ => !_loading)
            .Subscribe(_ =>
            {
                _settings.Update(s =>
                {
                    s.HeroItemsHero = SelectedHero?.HeroId;
                    s.HeroItemsMode = SelectedMode.Mode;
                    s.HeroItemsMinUsagePercent = (int)Math.Round(MinUsagePercent);
                });
                Refresh();
            })
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
        this.WhenAnyValue(vm => vm.SelectedMode)
            .Subscribe(_ => CanPickRanks = RanksApply)
            .DisposeWith(Disposables);
        Tiers.Select(tier => tier.WhenAnyValue(t => t.IsChecked))
            .Merge()
            .Skip(Tiers.Count)
            .Subscribe(_ => Refresh())
            .DisposeWith(Disposables);
        data.StoreReplaced.Subscribe(_ => Reload()).DisposeWith(Disposables);

        Refresh();
    }

    public IReadOnlyList<Hero> Heroes { get; private set; } = [];
    [Reactive] public Hero? SelectedHero { get; set; }

    [Reactive] public IReadOnlyList<PatchOption> Patches { get; private set; } = [];
    [Reactive] public PatchOption? SelectedPatch { get; set; }

    /// <summary>Any patch's counts at all: without them there's nothing to pick from.</summary>
    [Reactive] public bool HasMatchData { get; private set; }

    [Reactive] public ModeOption SelectedMode { get; set; }

    /// <summary>The rank groups the patch was downloaded with; none without them.</summary>
    [Reactive] public IReadOnlyList<RankBucket> Ranks { get; private set; } = [];
    [Reactive] public RankBucket? From { get; set; }
    [Reactive] public RankBucket? To { get; set; }

    /// <summary>Unranked matches have no rank, so they can't be narrowed to one.</summary>
    [Reactive] public bool CanPickRanks { get; private set; }

    public IReadOnlyList<TierToggle> Tiers { get; }

    /// <summary>Items bought in under this share of the hero's matches are hidden: 0 to <see cref="MaxMinUsagePercent"/>.</summary>
    [Reactive] public double MinUsagePercent { get; set; }

    public string MinUsageText => $"{Math.Round(MinUsagePercent):0}%";

    [Reactive] public HeroItemSort SortColumn { get; private set; } = HeroItemSort.Usage;
    [Reactive] public bool SortDescending { get; private set; } = true;
    public ReactiveCommand<HeroItemSort, Unit> SortCommand { get; }

    public IReadOnlyDictionary<HeroItemSort, ColumnHeader> Headers { get; }
    public ColumnHeader ItemHeader => Headers[HeroItemSort.Item];
    public ColumnHeader CostHeader => Headers[HeroItemSort.Cost];
    public ColumnHeader WinRateHeader => Headers[HeroItemSort.WinRate];
    public ColumnHeader WinRateChangeHeader => Headers[HeroItemSort.WinRateChange];
    public ColumnHeader UsageHeader => Headers[HeroItemSort.Usage];
    public ColumnHeader UsageChangeHeader => Headers[HeroItemSort.UsageChange];
    public ColumnHeader MatchesHeader => Headers[HeroItemSort.Matches];

    [Reactive] public IReadOnlyList<HeroItemRowViewModel> Rows { get; private set; } = [];

    /// <summary>"22,425 ranked matches · 52.6% won".</summary>
    [Reactive] public string Summary { get; private set; } = "";

    /// <summary>"12 items bought in under 5% of matches are hidden."; empty when none are.</summary>
    [Reactive] public string HiddenText { get; private set; } = "";

    /// <summary>Why there's no table, in place of it; null when there is one.</summary>
    [Reactive] public string? EmptyHint { get; private set; }

    public bool IsEmpty => EmptyHint is not null;

    /// <summary>
    /// The page came into view with <paramref name="selfHero"/> as your hero on the Match page: shown when
    /// it's a hero you haven't been shown here yet, so picking another hero here sticks until yours changes.
    /// </summary>
    public void ShowSelf(string? selfHero)
    {
        if (selfHero is null || selfHero == _offeredSelf)
            return;
        _offeredSelf = selfHero;
        if (Heroes.FirstOrDefault(hero => hero.HeroId == selfHero) is { } hero)
            SelectedHero = hero;
    }

    private void Reload() => Load(SelectedHero?.HeroId);

    /// <summary>The heroes, patches and ranks the store has, keeping what was picked where it still exists.</summary>
    private void Load(string? heroId)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var store = _data.Store;
            Heroes = store.HeroesSorted().Where(hero => hero.GameId != 0).ToList();
            this.RaisePropertyChanged(nameof(Heroes));
            SelectedHero = Heroes.FirstOrDefault(hero => hero.HeroId == heroId) ?? Heroes.FirstOrDefault();

            var patch = SelectedPatch?.Segment.Patch.Start;
            Patches = store.MatchSegments.Select(segment => new PatchOption(segment, $"Patch {segment.Patch.Label}")).ToList();
            SelectedPatch = Patches.FirstOrDefault(option => option.Segment.Patch.Start == patch) ?? Patches.FirstOrDefault();
            HasMatchData = Patches.Count > 0;

            var (from, to) = (From?.FirstTier, To?.FirstTier);
            Ranks = MatchStatsMath.RanksOf(store.MatchSegments);
            From = Ranks.FirstOrDefault(rank => rank.FirstTier == from) ?? Ranks.FirstOrDefault();
            To = Ranks.FirstOrDefault(rank => rank.FirstTier == to) ?? Ranks.LastOrDefault();
            CanPickRanks = RanksApply;
        }
        finally
        {
            _loading = wasLoading;
        }
        if (!_loading)
            Refresh();
    }

    private bool RanksApply => Ranks.Count > 0 && SelectedMode.Mode != MatchMode.Unranked;

    /// <summary>Every rank group picked means every match, unranked ones too: a range only narrows.</summary>
    private RankRange? Range =>
        RanksApply && From is not null && To is not null && (From != Ranks[0] || To != Ranks[^1])
            ? new RankRange(From.FirstTier, To.LastTier)
            : null;

    private void Sort(HeroItemSort column)
    {
        // Names read best A to Z; every number, biggest first.
        SortDescending = column == SortColumn ? !SortDescending : column != HeroItemSort.Item;
        SortColumn = column;
        ShowSort();
        Refresh();
    }

    private void ShowSort()
    {
        foreach (var header in Headers.Values)
            header.Show(SortColumn, SortDescending);
    }

    private void Refresh()
    {
        this.RaisePropertyChanged(nameof(MinUsageText));
        var table = SelectedHero is null || SelectedPatch is null
            ? null
            : HeroItemTable.Build(_data.Store.MatchSegments, SelectedPatch.Segment, SelectedHero.HeroId, SelectedMode.Mode, Range,
                _data.Store.Items.Values);
        EmptyHint = !HasMatchData
            ? "No match counts to show yet. Data → Download Match Data brings them, with the items each hero's players buy."
            : table is null
                ? "This patch's match data has no rank groups. Download the match data again with them to narrow it to ranks."
                : table.Matches.Matches == 0
                    ? $"No {SelectedHero!.HeroName} matches in this patch's match data{(Range is null ? "" : " at these ranks")}."
                    : null;
        this.RaisePropertyChanged(nameof(IsEmpty));
        if (table is null || EmptyHint is not null)
        {
            Rows = [];
            Summary = HiddenText = "";
            return;
        }

        var minUsage = Math.Round(MinUsagePercent) / 100;
        var tiers = Tiers.Where(tier => tier.IsChecked).Select(tier => tier.Tier).ToHashSet();
        var inTiers = table.Rows.Where(row => tiers.Contains(row.Item.Tier)).ToList();
        var shown = inTiers.Where(row => row.Usage >= minUsage).ToList();
        var (wins, matches) = table.Matches;
        var average = (double)wins / matches;
        Rows = Sorted(shown).Select(row => new HeroItemRowViewModel(row, average)).ToList();

        var mode = SelectedMode.Mode switch
        {
            MatchMode.Ranked => "ranked ",
            MatchMode.Unranked => "unranked ",
            _ => "",
        };
        Summary = $"{Format.Thousands(matches)} {mode}matches · {HeroItemRowViewModel.Percent(average)} won";
        var hidden = inTiers.Count - shown.Count;
        HiddenText = hidden == 0
            ? ""
            : $"{hidden} item{(hidden == 1 ? "" : "s")} bought in under {MinUsageText} of matches {(hidden == 1 ? "is" : "are")} hidden.";
    }

    private IEnumerable<HeroItemRow> Sorted(IEnumerable<HeroItemRow> rows)
    {
        Func<HeroItemRow, double> key = SortColumn switch
        {
            HeroItemSort.Cost => row => row.Item.Cost,
            HeroItemSort.WinRate => row => row.WinRate,
            // Without a change to show, last whichever way the column runs.
            HeroItemSort.WinRateChange => row => row.WinRateChange ?? (SortDescending ? double.MinValue : double.MaxValue),
            HeroItemSort.UsageChange => row => row.UsageChange ?? (SortDescending ? double.MinValue : double.MaxValue),
            HeroItemSort.Matches => row => row.Matches,
            HeroItemSort.Usage => row => row.Usage,
            _ => _ => 0,
        };
        if (SortColumn == HeroItemSort.Item)
        {
            return SortDescending
                ? rows.OrderByDescending(row => row.Item.ItemName, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(row => row.Item.ItemName, StringComparer.OrdinalIgnoreCase);
        }
        var ordered = SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return ordered.ThenBy(row => row.Item.ItemName, StringComparer.OrdinalIgnoreCase);
    }
}
