using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
    Fit,
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

/// <summary>A patch on offer: its counts, by date, ticked while they're in the table.</summary>
public sealed class PatchOption(MatchSegment segment, string label) : ReactiveObject
{
    public MatchSegment Segment { get; } = segment;
    public string Label { get; } = label;
    [Reactive] public bool IsChecked { get; set; }

    public override string ToString() => Label;
}

/// <summary>A column's header, which sorts by it: "Usage ▾" while the table is sorted by it.</summary>
public class ColumnHeader(HeroItemSort column, string title) : ReactiveObject
{
    public HeroItemSort Column { get; } = column;
    public string Title { get; } = title;
    [Reactive] public string Text { get; private set; } = "";
    [Reactive] public bool IsSorted { get; private set; }

    public void Show(HeroItemSort sorted, bool descending)
    {
        IsSorted = Column == sorted;
        Text = IsSorted ? $"{Title} {(descending ? "▾" : "▴")}" : Title;
    }
}

/// <summary>A tier's filter toggle: every tier shows until it's switched off.</summary>
public class TierToggle(int tier) : ReactiveObject
{
    public int Tier { get; } = tier;
    public string Label => $"T{Tier}";
    public string Tip => $"Right-click to show only T{Tier}. Right-click it again to show every tier.";
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

    public const double MaxMinUsagePercent = 50;

    public const string FitHeaderTip =
        "How much more this hero wins with the item than everyone who builds it, next to the hero's other items of the "
        + "same tier, in win-rate points. 0 is typical, and above 0 suits this hero better than its usual picks.\n"
        + "Unlike the win rate, it isn't raised by items being bought late in games already going well, but it says nothing "
        + "about how strong the item is for everyone. A low fit doesn't make an item a bad buy: it only means this hero "
        + "gains less from it than from its usual picks, and a strong item can fit every hero below that.\n"
        + "The Match page's \"you\" match data is the same measure.";

    public const string FiltersTip = "Which matches count, and which columns show";

    public const string ShowChangesTip =
        "Show how each item's win rate and usage moved since the patch before, in percentage points (Win Δ and Usage Δ)";

    public static readonly IReadOnlyList<ModeOption> Modes =
    [
        new(MatchMode.Ranked, "Ranked"),
        new(MatchMode.Unranked, "Unranked"),
        new(MatchMode.All, "Ranked and unranked"),
    ];

    /// <summary>
    /// Where <paramref name="percent"/> sits on the usage slider, 0 to 100. The slider is square-law, so its
    /// low end, where the useful cut-offs are, gets most of its travel: 5% is a third of the way along.
    /// </summary>
    public static double UsageToPosition(double percent) => Math.Sqrt(Math.Clamp(percent, 0, MaxMinUsagePercent) / MaxMinUsagePercent) * 100;

    /// <summary>The whole percent at <paramref name="position"/> on the usage slider.</summary>
    public static double PositionToUsage(double position) => Math.Round(Math.Pow(Math.Clamp(position, 0, 100) / 100, 2) * MaxMinUsagePercent);

    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly IConnectivityService _connectivity;
    private readonly Subject<string> _formulaRequested = new();
    private readonly SerialDisposable _patchChanges = new();
    private string? _offeredSelf;
    private bool _loading;
    private int? _pickedFrom;
    private int? _pickedTo;

    public HeroItemsViewModel(IDataService data, ISettingsService settings, IConnectivityService connectivity)
    {
        _data = data;
        _settings = settings;
        _connectivity = connectivity;
        _patchChanges.DisposeWith(Disposables);
        Tiers = Enumerable.Range(1, 4).Select(tier => new TierToggle(tier)).ToList();
        Headers = new Dictionary<HeroItemSort, ColumnHeader>
        {
            [HeroItemSort.Item] = new(HeroItemSort.Item, "ITEM"),
            [HeroItemSort.Cost] = new(HeroItemSort.Cost, "COST"),
            [HeroItemSort.WinRate] = new(HeroItemSort.WinRate, "WIN RATE"),
            [HeroItemSort.Fit] = new(HeroItemSort.Fit, "HERO FIT"),
            [HeroItemSort.WinRateChange] = new(HeroItemSort.WinRateChange, "WIN Δ"),
            [HeroItemSort.Usage] = new(HeroItemSort.Usage, "USAGE"),
            [HeroItemSort.UsageChange] = new(HeroItemSort.UsageChange, "USAGE Δ"),
            [HeroItemSort.Matches] = new(HeroItemSort.Matches, "WON / LOST"),
        };
        ShowSort();
        SortCommand = ReactiveCommand.Create<HeroItemSort>(Sort);
        OpenFormulaCommand = ReactiveCommand.Create<string>(_formulaRequested.OnNext, this.WhenAnyValue(vm => vm.ShowsEditors));
        PickLatestPatchCommand = ReactiveCommand.Create(() => PickPatches(index => index == 0));
        PickAllPatchesCommand = ReactiveCommand.Create(() => PickPatches(_ => true));
        settings.SettingsChanged
            .Select(s => s.ShowModelEditors)
            .DistinctUntilChanged()
            .Subscribe(show => ShowsEditors = show)
            .DisposeWith(Disposables);

        _loading = true;
        SelectedMode = Modes.FirstOrDefault(option => option.Mode == settings.Current.HeroItemsMode) ?? Modes[0];
        MinUsagePercent = Math.Clamp(settings.Current.HeroItemsMinUsagePercent, 0, MaxMinUsagePercent);
        UsagePosition = UsageToPosition(MinUsagePercent);
        ShowChanges = settings.Current.HeroItemsShowChanges;
        Load(settings.Current.HeroItemsHero);
        _loading = false;

        // The slider's position and the percent it sets follow each other, and the position snaps back to its percent's own spot: the thumb only rests on whole percents.
        this.WhenAnyValue(vm => vm.UsagePosition)
            .Subscribe(position =>
            {
                MinUsagePercent = PositionToUsage(position);
                UsagePosition = UsageToPosition(MinUsagePercent);
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.MinUsagePercent)
            .Subscribe(percent => UsagePosition = UsageToPosition(percent))
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.ShowChanges)
            .Skip(1)
            .Subscribe(show => _settings.Update(s => s.HeroItemsShowChanges = show))
            .DisposeWith(Disposables);

        this.WhenAnyValue(vm => vm.SelectedHero, vm => vm.SelectedMode, vm => vm.From, vm => vm.To, vm => vm.MinUsagePercent)
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
            .Skip(1)
            .Subscribe(_ => ShowRanksForMode())
            .DisposeWith(Disposables);
        Tiers.Select(tier => tier.WhenAnyValue(t => t.IsChecked).Skip(1).Select(_ => tier))
            .Merge()
            .Subscribe(OnTierToggled)
            .DisposeWith(Disposables);
        data.StoreReplaced.Subscribe(_ => Reload()).DisposeWith(Disposables);
        // With no match counts, the hint says where to get them, which depends on whether there's a connection to get them over.
        connectivity.States
            .Select(state => state != ConnectivityState.Online)
            .DistinctUntilChanged()
            .Skip(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(offline => !HasMatchData)
            .Subscribe(offline => Refresh())
            .DisposeWith(Disposables);

        Refresh();
    }

    public IReadOnlyList<Hero> Heroes { get; private set; } = [];
    [Reactive] public Hero? SelectedHero { get; set; }

    /// <summary>Newest first. At least one is always ticked; the table adds up the ticked ones' counts.</summary>
    [Reactive] public IReadOnlyList<PatchOption> Patches { get; private set; } = [];

    /// <summary>"Patch 09-29", "Patches 09-16 – 09-29" for neighbours, or "2 patches".</summary>
    [Reactive] public string PatchSummary { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> PickLatestPatchCommand { get; }
    public ReactiveCommand<Unit, Unit> PickAllPatchesCommand { get; }

    /// <summary>What the Change columns are since: the patch before the oldest one ticked.</summary>
    [Reactive] public string ChangeTip { get; private set; } = "";

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

    /// <summary>Where the usage slider sits, 0 to 100: not linear, see <see cref="UsageToPosition"/>.</summary>
    [Reactive] public double UsagePosition { get; set; }

    public string MinUsageText => $"{Math.Round(MinUsagePercent):0}%";

    /// <summary>Whether the Win Δ and Usage Δ columns show. They're off until asked for.</summary>
    [Reactive] public bool ShowChanges { get; set; }

    /// <summary>A filter has the table counting other than ranked matches at every rank.</summary>
    [Reactive] public bool HasActiveFilters { get; private set; }
    [Reactive] public string FiltersButtonTip { get; private set; } = FiltersTip;

    /// <summary>Said by the greyed-out ranks, which every other mode than Ranked leaves at every rank; empty otherwise.</summary>
    [Reactive] public string RanksNote { get; private set; } = "";

    [Reactive] public HeroItemSort SortColumn { get; private set; } = HeroItemSort.Usage;
    [Reactive] public bool SortDescending { get; private set; } = true;
    public ReactiveCommand<HeroItemSort, Unit> SortCommand { get; }

    public IReadOnlyDictionary<HeroItemSort, ColumnHeader> Headers { get; }
    public ColumnHeader ItemHeader => Headers[HeroItemSort.Item];
    public ColumnHeader CostHeader => Headers[HeroItemSort.Cost];
    public ColumnHeader WinRateHeader => Headers[HeroItemSort.WinRate];
    public ColumnHeader FitHeader => Headers[HeroItemSort.Fit];
    public ColumnHeader WinRateChangeHeader => Headers[HeroItemSort.WinRateChange];
    public ColumnHeader UsageHeader => Headers[HeroItemSort.Usage];
    public ColumnHeader UsageChangeHeader => Headers[HeroItemSort.UsageChange];
    public ColumnHeader MatchesHeader => Headers[HeroItemSort.Matches];

    [Reactive] public IReadOnlyList<HeroItemRowViewModel> Rows { get; private set; } = [];

    /// <summary>"22,425 ranked matches · 52.6% won".</summary>
    [Reactive] public string Summary { get; private set; } = "";

    /// <summary>"12 items bought in under 5% of matches are hidden."; empty when none are.</summary>
    [Reactive] public string HiddenText { get; private set; } = "";

    /// <summary>Which ticked patches the counts leave out, and why; empty when none.</summary>
    [Reactive] public string PatchNote { get; private set; } = "";

    /// <summary>Opens an item's formula on the Item Formulas page, for as long as the model editors are shown.</summary>
    public ReactiveCommand<string, Unit> OpenFormulaCommand { get; }

    public IObservable<string> FormulaRequested => _formulaRequested;

    /// <summary>Whether the model editors (the Item Formulas page among them) are shown.</summary>
    [Reactive] public bool ShowsEditors { get; private set; }

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

            var ticked = Patches.Where(option => option.IsChecked).Select(option => option.Segment.Patch.Start).ToHashSet();
            Patches = store.MatchSegments.Select(segment => new PatchOption(segment, $"Patch {segment.Patch.Label}")).ToList();
            foreach (var option in Patches)
                option.IsChecked = ticked.Contains(option.Segment.Patch.Start);
            if (Patches.Count > 0 && !Patches.Any(option => option.IsChecked))
                Patches[0].IsChecked = true;
            _patchChanges.Disposable = Patches
                .Select(option => option.WhenAnyValue(o => o.IsChecked).Skip(1).Select(_ => option))
                .Merge()
                .Subscribe(OnPatchToggled);
            ShowPatches();
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

    /// <summary>The ticked patches' counts, newest first.</summary>
    private List<MatchSegment> PickedSegments => Patches.Where(option => option.IsChecked).Select(option => option.Segment).ToList();

    /// <summary>Moves the usage cut-off by whole percents, for the slider's keys: its own steps are in positions, which at the low end are far smaller than a percent.</summary>
    public void StepUsage(double percents) => MinUsagePercent = Math.Round(Math.Clamp(MinUsagePercent + percents, 0, MaxMinUsagePercent));

    /// <summary>Shows just <paramref name="tier"/>, or every tier again when it's the only one shown.</summary>
    public void ShowOnlyTier(TierToggle tier)
    {
        var alone = tier.IsChecked && Tiers.All(other => other == tier || !other.IsChecked);
        var wasLoading = _loading;
        _loading = true;
        try
        {
            foreach (var other in Tiers)
                other.IsChecked = alone || other == tier;
        }
        finally
        {
            _loading = wasLoading;
        }
        if (!_loading)
            Refresh();
    }

    private void OnTierToggled(TierToggle tier)
    {
        if (_loading)
            return;
        // The last one stays: the table needs a tier to show.
        if (!Tiers.Any(other => other.IsChecked))
        {
            tier.IsChecked = true;
            return;
        }
        Refresh();
    }

    private void OnPatchToggled(PatchOption option)
    {
        if (_loading)
            return;
        // The last one stays: the table needs a patch to count.
        if (!Patches.Any(other => other.IsChecked))
        {
            option.IsChecked = true;
            return;
        }
        ShowPatches();
        Refresh();
    }

    /// <summary>Ticks the patches <paramref name="pick"/> says yes to, by position (0 is the newest), and shows them once.</summary>
    private void PickPatches(Func<int, bool> pick)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            for (var index = 0; index < Patches.Count; index++)
                Patches[index].IsChecked = pick(index);
        }
        finally
        {
            _loading = wasLoading;
        }
        if (_loading)
            return;
        ShowPatches();
        Refresh();
    }

    private void ShowPatches()
    {
        var picked = Enumerable.Range(0, Patches.Count).Where(index => Patches[index].IsChecked).ToList();
        ChangeTip = picked.Count > 1
            ? "Since the patch before the oldest one picked, in percentage points."
            : "Since the patch before, in percentage points.";
        if (picked.Count == 0)
        {
            PatchSummary = "";
            return;
        }
        var (newest, oldest) = (Patches[picked[0]], Patches[picked[^1]]);
        var neighbours = picked[^1] - picked[0] == picked.Count - 1;
        PatchSummary = picked.Count == 1
            ? newest.Label
            : neighbours
                ? $"Patches {oldest.Segment.Patch.Label} – {newest.Segment.Patch.Label}"
                : $"{picked.Count} patches";
    }

    /// <summary>Only ranked matches have ranks, so a range can only be picked for them.</summary>
    private bool RanksApply => Ranks.Count > 0 && SelectedMode.Mode == MatchMode.Ranked;

    /// <summary>
    /// Another mode counts every rank, so its range shows as every rank, greyed out, and the one picked for
    /// Ranked comes back when that's chosen again.
    /// </summary>
    private void ShowRanksForMode()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            if (Ranks.Count > 0 && SelectedMode.Mode == MatchMode.Ranked)
            {
                From = Ranks.FirstOrDefault(rank => rank.FirstTier == _pickedFrom) ?? From;
                To = Ranks.FirstOrDefault(rank => rank.FirstTier == _pickedTo) ?? To;
                (_pickedFrom, _pickedTo) = (null, null);
            }
            else if (Ranks.Count > 0)
            {
                _pickedFrom ??= From?.FirstTier;
                _pickedTo ??= To?.FirstTier;
                From = Ranks[0];
                To = Ranks[^1];
            }
            CanPickRanks = RanksApply;
        }
        finally
        {
            _loading = wasLoading;
        }
        if (!_loading)
            Refresh();
    }

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

    private void ShowFilters()
    {
        var active = new List<string>();
        if (SelectedMode.Mode != MatchMode.Ranked)
            active.Add($"{SelectedMode.Label} matches");
        if (Range is not null)
            active.Add($"Ranks {From!.FirstName} to {To!.LastName}");
        HasActiveFilters = active.Count > 0;
        FiltersButtonTip = HasActiveFilters ? $"{FiltersTip}\n\nOn now:\n• {string.Join("\n• ", active)}" : FiltersTip;
        RanksNote = Ranks.Count > 0 && !RanksApply ? "Unranked matches have no rank, so every rank counts." : "";
    }

    private void Refresh()
    {
        this.RaisePropertyChanged(nameof(MinUsageText));
        ShowFilters();
        var picked = PickedSegments;
        var table = SelectedHero is null || picked.Count == 0
            ? null
            : HeroItemTable.Build(_data.Store.MatchSegments, picked, SelectedHero.HeroId, SelectedMode.Mode, Range, _data.Store.Items.Values);
        var theirs = picked.Count == 1 ? "this patch's" : "these patches'";
        EmptyHint = !HasMatchData
            ? _connectivity.IsOffline
                ? "No match counts to show yet, and you're offline. Data → Download Match Data brings them, with the items each hero's players buy, "
                  + "once you're connected."
                : "No match counts to show yet. Data → Download Match Data brings them, with the items each hero's players buy."
            : table is null
                ? $"{(picked.Count == 1 ? "This patch's" : "These patches'")} match data has no rank groups. "
                  + "Download the match data again with them to narrow it to ranks."
                : table.Matches.Matches == 0
                    ? $"No {SelectedHero!.HeroName} matches in {theirs} match data{(Range is null ? "" : " at these ranks")}."
                    : null;
        this.RaisePropertyChanged(nameof(IsEmpty));
        if (table is null || EmptyHint is not null)
        {
            Rows = [];
            Summary = HiddenText = PatchNote = "";
            return;
        }

        var minUsage = Math.Round(MinUsagePercent) / 100;
        var tiers = Tiers.Where(tier => tier.IsChecked).Select(tier => tier.Tier).ToHashSet();
        var inTiers = table.Rows.Where(row => tiers.Contains(row.Item.Tier)).ToList();
        var shown = inTiers.Where(row => row.Usage >= minUsage).ToList();
        var (wins, matches) = table.Matches;
        var average = (double)wins / matches;
        Rows = Sorted(shown).Select(row => new HeroItemRowViewModel(row, average, SelectedHero!.HeroName)).ToList();

        var mode = SelectedMode.Mode switch
        {
            MatchMode.Unranked => "unranked ",
            MatchMode.Ranked => "ranked ",
            _ => "",
        };
        var over = table.PatchCount > 1 ? $" over {table.PatchCount} patches" : "";
        Summary = $"{Format.Thousands(matches)} {mode}matches{over} · {HeroItemRowViewModel.Percent(average)} won";
        var left = picked.Count - table.PatchCount;
        PatchNote = left == 0
            ? ""
            : $"{left} of the {picked.Count} patches picked {(left == 1 ? "has" : "have")} no rank groups, so {(left == 1 ? "its" : "their")} counts are left out.";
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
            // Without a fit or a change to show, last whichever way the column runs.
            HeroItemSort.Fit => row => row.Fit?.Shown ?? (SortDescending ? double.MinValue : double.MaxValue),
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
