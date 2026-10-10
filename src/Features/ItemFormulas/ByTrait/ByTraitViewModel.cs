using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.ItemFormulas.ByTrait;

/// <summary>
/// "Which items do I want against a hero who applies slows?" Pick one trait and one relation, then
/// run down every item typing numbers: 156 items in one column, one keystroke each. This is the bulk
/// mode that replaces filling in a spreadsheet.
/// </summary>
public class ByTraitViewModel : ViewModelBase
{
    public const string FocusSearchAction = "FocusSearch";

    public const string Hint =
        "Type a digit to set a coefficient and drop to the next item.\n"
        + "\"-\" first to discourage an item.\nBackspace clears.\n"
        + "B, or a click in its column, toggles Best target.";

    public const string WeightTip =
        "Scales every item on this trait + relation at once -- typed and\n"
        + "from-stats coefficients alike. 1 = as typed, 0.5 = half, 0 = off.";

    public const string StatRulesTip =
        "Coefficients worked out from each item's real numbers, added to the\n"
        + "ones you type. Saved to data/stat_rules.csv. Item stats come from\n"
        + "Data → Model Tools → Sync from Game API.";

    public const string FromStatsTip =
        "What this item gets from its stats via data/stat_rules.csv.\n"
        + "It's added to the coefficient you type, and the trait weight\n"
        + "scales the sum. Hover a cell for the arithmetic.";

    public static readonly IReadOnlyList<string> ColumnNames = ["Item", "Tier", "Shop", "Coefficient", "From stats", "Best target"];
    public const int CoefficientColumn = 3;
    public const int BestTargetColumn = 5;
    private const double _coefficientLimit = 20;

    private readonly IDataService _data;
    private List<CoefficientRow> _allRows = [];
    private List<CoefficientRow> _ordered = [];
    private bool _pendingNegative;
    private bool _syncing;

    public ByTraitViewModel(IDataService data)
    {
        _data = data;

        SetRelationCommand = ReactiveCommand.Create<Relation>(relation =>
        {
            Relation = relation;
            RaiseRelationFlags();
        });
        AddStatRuleCommand = ReactiveCommand.Create(AddStatRule);
        SortCommand = ReactiveCommand.Create<int>(CycleSort);

        Reload();

        this.WhenAnyValue(vm => vm.SelectedCategory, vm => vm.Relation).Skip(1).Subscribe(_ => SyncTarget()).DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.SearchText, vm => vm.TaggedOnly).Skip(1).Subscribe(_ => ApplyFilter()).DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.Weight).Skip(1).Subscribe(OnWeightChanged).DisposeWith(Disposables);
        data.StoreReplaced.Subscribe(_ => Reload()).DisposeWith(Disposables);
    }

    // -- target -------------------------------------------------------------------

    [Reactive] public IReadOnlyList<Category> Categories { get; private set; } = [];
    [Reactive] public Category? SelectedCategory { get; set; }
    [Reactive] public Relation Relation { get; private set; } = Relation.Against;
    public bool IsAgainst => Relation == Relation.Against;
    public bool IsWith => Relation == Relation.With;
    public bool IsAs => Relation == Relation.As;

    /// <summary>The trait weight for the target, 0-5; 1 leaves the coefficients as typed.</summary>
    [Reactive] public decimal? Weight { get; set; } = 1;

    [Reactive] public IReadOnlyList<TextSpan> Description { get; private set; } = [];

    // -- stat rules ---------------------------------------------------------------

    public ResettableCollection<StatRuleRowViewModel> StatRules { get; } = [];
    [Reactive] public bool HasStatRules { get; private set; }
    [Reactive] public bool CanAddStatRule { get; private set; }
    [Reactive] public string AddStatRuleTip { get; private set; } = "";

    // -- grid ---------------------------------------------------------------------

    [Reactive] public string SearchText { get; set; } = "";
    [Reactive] public bool TaggedOnly { get; set; }

    /// <summary>The rows the filter lets through, in the sorted order.</summary>
    [Reactive] public IReadOnlyList<CoefficientRow> Rows { get; private set; } = [];

    [Reactive] public CoefficientRow? CurrentRow { get; set; }

    /// <summary>The header sort: a column, or -1 for tier-then-name order.</summary>
    [Reactive] public int SortColumn { get; private set; } = -1;
    [Reactive] public bool SortDescending { get; private set; }

    /// <summary>Bumped when row values change, so the grid repaints.</summary>
    [Reactive] public int Revision { get; private set; }

    [Reactive] public string Summary { get; private set; } = "";

    /// <summary>How many rows a Page Up/Down moves; the view keeps this in step with the grid's height.</summary>
    public int PageRows { get; set; } = 10;

    public ReactiveCommand<Relation, Unit> SetRelationCommand { get; }
    public ReactiveCommand<Unit, Unit> AddStatRuleCommand { get; }
    public ReactiveCommand<int, Unit> SortCommand { get; }

    public void FocusSearch() => RequestViewAction(FocusSearchAction);

    private string? CategoryId => SelectedCategory?.CategoryId;

    /// <summary>Rows for every item and the trait list, keeping whichever trait was picked if it still exists.</summary>
    private void Reload()
    {
        var store = _data.Store;
        _allRows = store.ItemsSorted().Select(item => new CoefficientRow(item)).ToList();
        var previous = CategoryId;
        _syncing = true;
        Categories = store.CategoriesOrdered();
        SelectedCategory = Categories.FirstOrDefault(category => category.CategoryId == previous) ?? Categories.FirstOrDefault();
        _syncing = false;
        SyncTarget();
    }

    private void SyncTarget()
    {
        if (_syncing)
            return;
        var store = _data.Store;
        foreach (var row in _allRows)
            row.Refresh(store, CategoryId, Relation);
        Reorder();

        _syncing = true;
        Weight = (decimal)(CategoryId is { } categoryId ? store.TraitWeight(categoryId, Relation) : 1.0);
        _syncing = false;

        RenderStatRules();
        Description = SelectedCategory is { } category
            ? [new TextSpan(Relation.Help(), Bold: true), TextSpan.LineBreak, new TextSpan(category.Description, Palette.TextDim)]
            : [];
        ApplyFilter();
        RefreshSummary();
        CurrentRow = Rows.FirstOrDefault();
    }

    private void RaiseRelationFlags()
    {
        this.RaisePropertyChanged(nameof(IsAgainst));
        this.RaisePropertyChanged(nameof(IsWith));
        this.RaisePropertyChanged(nameof(IsAs));
    }

    private void OnWeightChanged(decimal? value)
    {
        if (_syncing || value is null || CategoryId is not { } categoryId)
            return;
        if (!_data.Store.SetTraitWeight(categoryId, Relation, NumberFormat.Round((double)value, 2)))
            return;
        RefreshSummary();
        _data.MarkEdited(DataFiles.TraitWeights);
    }

    // -- sorting and filtering ----------------------------------------------------

    /// <summary>Ascending, descending, then a third click on the same header clears back to tier order.</summary>
    private void CycleSort(int column)
    {
        if (column != SortColumn)
            (SortColumn, SortDescending) = (column, false);
        else if (!SortDescending)
            SortDescending = true;
        else
            (SortColumn, SortDescending) = (-1, false);
        Reorder();
        ApplyFilter();
    }

    /// <summary>
    /// The order only changes on a header click or a new trait. Edits don't re-sort: a row that jumped
    /// away mid-pass would break typing down the list.
    /// </summary>
    private void Reorder()
    {
        IEnumerable<CoefficientRow> rows = _allRows;
        if (SortColumn >= 0)
        {
            rows = SortColumn switch
            {
                0 => Sort(rows, row => row.Name.ToLowerInvariant(), StringComparer.Ordinal),
                1 => Sort(rows, row => row.Item.Tier, Comparer<int>.Default),
                2 => Sort(rows, row => row.Shop.ToLowerInvariant(), StringComparer.Ordinal),
                3 => Sort(rows, row => row.Coefficient, Comparer<double>.Default),
                4 => Sort(rows, row => row.FromStats, Comparer<double>.Default),
                _ => Sort(rows, row => row.BestTarget, Comparer<bool>.Default),
            };
        }
        _ordered = rows.ToList();
        for (var index = 0; index < _ordered.Count; index++)
            _ordered[index].OrderIndex = index;
    }

    // Both directions are stable sorts, so ties keep tier/name order.
    private IEnumerable<CoefficientRow> Sort<TKey>(IEnumerable<CoefficientRow> rows, Func<CoefficientRow, TKey> key, IComparer<TKey> comparer) =>
        SortDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    private void ApplyFilter()
    {
        var needle = SearchText.Trim().ToLowerInvariant();
        var store = _data.Store;
        Rows = _ordered
            .Where(row => (needle.Length == 0 || store.ItemMatches(row.ItemId, needle)) && (!TaggedOnly || row.IsTagged))
            .ToList();
    }

    /// <summary>The other panel edits the same table, so catch up whenever this one comes back into view.</summary>
    public void Resync()
    {
        var store = _data.Store;
        foreach (var row in _allRows)
            row.Refresh(store, CategoryId, Relation);
        Revision++;
        RefreshSummary();
        ApplyFilter();
    }

    // -- entry --------------------------------------------------------------------

    /// <summary>Write one coefficient, clamped to ±20, as typing or the spin box sets it.</summary>
    public void SetCoefficient(CoefficientRow row, double value)
    {
        if (CategoryId is not { } categoryId)
            return;
        var number = Math.Max(-_coefficientLimit, Math.Min(_coefficientLimit, value));
        if (!_data.Store.SetCoefficient(row.ItemId, categoryId, Relation, number))
            return;
        row.Refresh(_data.Store, categoryId, Relation);
        Revision++;
        RefreshSummary();
        _data.MarkEdited(DataFiles.ItemCoefficients);
    }

    /// <summary>Flip whether this item's line counts its best targets; a line the item's cast already ranks stays as it is.</summary>
    public void ToggleBestTarget(CoefficientRow row)
    {
        if (CategoryId is not { } categoryId || !row.BestTargetApplies || row.BestTargetFromCast)
            return;
        if (!_data.Store.SetBestTarget(row.ItemId, categoryId, Relation, !row.BestTarget))
            return;
        row.Refresh(_data.Store, categoryId, Relation);
        Revision++;
        RefreshSummary();
        _data.MarkEdited(DataFiles.ItemCoefficients);
    }

    /// <summary>
    /// A digit sets the current item's coefficient and drops to the next; "-" first makes it negative.
    /// "b" toggles Best target and stays put.
    /// </summary>
    public bool HandleText(string text)
    {
        if (CurrentRow is null)
            return false;
        var handled = false;
        foreach (var character in text)
        {
            if (character == '-')
            {
                _pendingNegative = true;
                handled = true;
            }
            else if (char.IsAsciiDigit(character) && CurrentRow is { } row)
            {
                double value = character - '0';
                if (_pendingNegative)
                {
                    value = -value;
                    _pendingNegative = false;
                }
                SetCoefficient(row, value);
                Advance(row);
                handled = true;
            }
            else if (character is 'b' or 'B' && CurrentRow is { } current)
            {
                ToggleBestTarget(current);
                handled = true;
            }
        }
        return handled;
    }

    /// <summary>Backspace clears and moves on; the arrows and page keys move through the visible rows.</summary>
    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (CurrentRow is not { } row)
            return false;
        var position = IndexOf(row);
        switch (key)
        {
            case Key.Delete or Key.Back:
                SetCoefficient(row, 0);
                Advance(row);
                return true;
            case Key.Up:
                MoveTo(position - 1);
                return true;
            case Key.Down:
                MoveTo(position + 1);
                return true;
            case Key.PageUp:
                MoveTo(position - PageRows);
                return true;
            case Key.PageDown:
                MoveTo(position + PageRows);
                return true;
            case Key.Home when modifiers.HasFlag(KeyModifiers.Control):
                MoveTo(0);
                return true;
            case Key.End when modifiers.HasFlag(KeyModifiers.Control):
                MoveTo(Rows.Count - 1);
                return true;
            default:
                return false;
        }
    }

    private int IndexOf(CoefficientRow row)
    {
        for (var index = 0; index < Rows.Count; index++)
        {
            if (ReferenceEquals(Rows[index], row))
                return index;
        }
        return -1;
    }

    private void MoveTo(int position)
    {
        if (Rows.Count > 0)
            CurrentRow = Rows[Math.Clamp(position, 0, Rows.Count - 1)];
    }

    /// <summary>The next row the filter shows, if there is one.</summary>
    private void Advance(CoefficientRow row)
    {
        var position = IndexOf(row);
        if (position >= 0 && position + 1 < Rows.Count)
            CurrentRow = Rows[position + 1];
        else if (position < 0)
            CurrentRow = Rows.FirstOrDefault(candidate => candidate.OrderIndex > row.OrderIndex) ?? CurrentRow;
    }

    // -- stat rules ---------------------------------------------------------------

    /// <summary>stat → "Spirit Resist (22 items)", minus stats already used by another rule on this trait (a stat can only have one).</summary>
    private List<StatChoice> StatChoices(IReadOnlySet<string> exclude) =>
        _data.Store.StatCatalog()
            .Where(entry => !exclude.Contains(entry.Key))
            .Select(entry => new StatChoice(entry.Key, $"{entry.Value.Label} ({entry.Value.ItemCount} item{(entry.Value.ItemCount != 1 ? "s" : "")})"))
            .ToList();

    private void RenderStatRules()
    {
        var rules = CategoryId is { } categoryId ? _data.Store.StatRulesFor(categoryId, Relation) : [];
        var used = rules.Select(rule => rule.Stat).ToHashSet();
        var spare = StatChoices(used);
        CanAddStatRule = CategoryId is not null && spare.Count > 0;
        AddStatRuleTip = spare.Count > 0
            ? "Pick a stat; the rate starts where a typical item scores 2 (mild)"
            : "Every stat already has a rule on this trait";
        HasStatRules = rules.Count > 0;

        var old = StatRules.ToList();
        StatRules.ReplaceAll(rules.Select(rule =>
        {
            var choices = StatChoices(used.Where(stat => stat != rule.Stat).ToHashSet());
            // A rule typed into the CSV for a stat no current item has.
            if (choices.All(choice => choice.Stat != rule.Stat))
                choices.Insert(0, new StatChoice(rule.Stat, $"{rule.Stat} (0 items)"));
            return new StatRuleRowViewModel(rule, choices, OnStatRuleEdited, OnStatRuleRemoved);
        }));
        foreach (var row in old)
            row.Dispose();
    }

    private void AddStatRule()
    {
        if (CategoryId is not { } categoryId)
            return;
        var store = _data.Store;
        var used = store.StatRulesFor(categoryId, Relation).Select(rule => rule.Stat).ToHashSet();
        var spare = StatChoices(used);
        if (spare.Count == 0)
            return;
        var stat = spare[0].Stat;
        if (!store.SetStatRule(new StatRule(stat, categoryId, Relation, store.SuggestPerUnit(stat), 0.5)))
            return;
        RenderStatRules();
        AfterStatRulesChanged();
    }

    private void OnStatRuleEdited(StatRule rule, string? replaced)
    {
        var store = _data.Store;
        if (!store.SetStatRule(rule, replaced))
            return;
        if (!string.IsNullOrEmpty(replaced))
        {
            // A new stat changes what the other rows may pick, and deserves a fresh suggested rate.
            if (replaced != rule.Stat)
                store.SetStatRule(rule with { PerUnit = store.SuggestPerUnit(rule.Stat) });
            // Deferred: we're inside a change coming from a row the re-render replaces.
            RxApp.MainThreadScheduler.Schedule(RenderStatRules);
        }
        AfterStatRulesChanged();
    }

    private void OnStatRuleRemoved(string stat)
    {
        if (CategoryId is not { } categoryId || !_data.Store.RemoveStatRule(stat, categoryId, Relation))
            return;
        RxApp.MainThreadScheduler.Schedule(RenderStatRules);
        AfterStatRulesChanged();
    }

    /// <summary>
    /// The From stats column updates as you type, so a rate is checkable immediately. Like typed edits
    /// this doesn't re-sort, so rows don't jump away mid-tweak.
    /// </summary>
    private void AfterStatRulesChanged()
    {
        var store = _data.Store;
        foreach (var row in _allRows)
            row.Refresh(store, CategoryId, Relation);
        Revision++;
        ApplyFilter();
        RefreshSummary();
        _data.MarkEdited(DataFiles.StatRules);
    }

    private void RefreshSummary()
    {
        if (CategoryId is not { } categoryId)
        {
            Summary = "";
            return;
        }
        var store = _data.Store;
        var tagged = store.ItemsForCategory(categoryId, Relation).Count;
        var derived = store.DerivedItemsForCategory(categoryId, Relation).Count;
        var weight = store.TraitWeight(categoryId, Relation);
        var bestTargets = store.BestTargetLines.Count(key => key.CategoryId == categoryId && key.Relation == Relation);
        Summary = $"{tagged} item(s) typed + {derived} from stats for this trait/relation"
                  + (bestTargets > 0 ? $" · {bestTargets} on best targets" : "")
                  + (weight != 1 ? $" · all scaled × {Format.Num(weight)}" : "")
                  + $" · {store.ItemCoefficients.Count} rules total";
    }
}
