using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Shared.Modals.Choice;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;
using DynamicData;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.ItemFormulas.ByItem;

/// <summary>A tier filter pill; re-raised on every click so a click on the lit one can't unlight it.</summary>
public class TierPill(int tier) : ReactiveObject
{
    public int Tier { get; } = tier;
    public string Label => Tier == 0 ? "All" : $"T{Tier}";
    public bool IsChecked { get; private set; }

    public void Set(bool isChecked)
    {
        IsChecked = isChecked;
        this.RaisePropertyChanged(nameof(IsChecked));
    }
}

/// <summary>
/// "What does Spirit Resilience respond to?" Pick an item, see and edit its handful of rules, and
/// watch a live list of which heroes those rules would fire against, so a coefficient you just typed
/// is checkable straight away.
/// </summary>
public class ByItemViewModel : ViewModelBase
{
    public const string FocusSearchAction = "FocusSearch";
    private const int _previewHeroes = 6;

    private readonly IDataService _data;
    private readonly IModalService _modals;
    private readonly ISettingsService _settings;
    private readonly SourceCache<ItemRowViewModel, string> _rows = new(row => row.ItemId);
    private readonly ReadOnlyObservableCollection<ItemRowViewModel> _items;
    private Dictionary<string, Avalonia.Media.Color> _traitColors = [];
    private IReadOnlyList<DerivedRuleEntry> _derivedRules = [];

    public ByItemViewModel(IDataService data, IModalService modals, ISettingsService settings)
    {
        _data = data;
        _modals = modals;
        _settings = settings;
        TierPills = Enumerable.Range(0, 5).Select(tier => new TierPill(tier)).ToList();
        TierPills[0].Set(true);

        // Filtering never rebuilds the rows, and edits don't re-filter: an item you just tagged stays
        // put under "Untagged only" until the filter itself changes. A filter down to one item opens it.
        var filter = this.WhenAnyValue(vm => vm.SearchText, vm => vm.TierFilter, vm => vm.UntaggedOnly)
            .Select(_ => BuildFilter());
        _rows.Connect()
            .Filter(filter)
            .SortAndBind(out _items, Comparer<ItemRowViewModel>.Create((a, b) =>
            {
                var byTier = a.Tier.CompareTo(b.Tier);
                return byTier != 0 ? byTier : string.CompareOrdinal(a.Name, b.Name);
            }))
            .Where(_ => _items.Count == 1)
            .Subscribe(_ => SelectedRow = _items[0])
            .DisposeWith(Disposables);

        SetTierFilterCommand = ReactiveCommand.Create<int>(SetTierFilter);
        AddRuleCommand = ReactiveCommand.Create(AddRule);
        CopyRulesCommand = ReactiveCommand.Create<string?>(CopyRules);
        ClearRulesCommand = ReactiveCommand.Create<string?>(ClearRules);

        // A cleared list selection (the row was filtered away) leaves the detail where it was.
        this.WhenAnyValue(vm => vm.SelectedRow)
            .WhereNotNull()
            .Subscribe(row => ShowItem(row.ItemId))
            .DisposeWith(Disposables);
        data.StoreReplaced.Subscribe(_ => Populate()).DisposeWith(Disposables);

        Populate();
    }

    // -- item list ------------------------------------------------------------------

    public ReadOnlyObservableCollection<ItemRowViewModel> Items => _items;
    public IReadOnlyList<TierPill> TierPills { get; }
    [Reactive] public string SearchText { get; set; } = "";
    [Reactive] public int TierFilter { get; private set; }
    [Reactive] public bool UntaggedOnly { get; set; }
    [Reactive] public ItemRowViewModel? SelectedRow { get; set; }
    [Reactive] public string CoverageText { get; private set; } = "";

    // -- detail ---------------------------------------------------------------------

    /// <summary>The item the detail shows, or null before one is picked.</summary>
    [Reactive] public Item? CurrentItem { get; private set; }
    [Reactive] public string DetailTitle { get; private set; } = "Select an item";
    [Reactive] public string DetailSub { get; private set; } = "";
    public ResettableCollection<RuleCardViewModel> Rules { get; } = [];
    [Reactive] public string? RulesHint { get; private set; }
    public IReadOnlyList<DerivedRuleEntry> DerivedRules
    {
        get => _derivedRules;
        private set
        {
            this.RaiseAndSetIfChanged(ref _derivedRules, value);
            this.RaisePropertyChanged(nameof(HasDerivedRules));
        }
    }

    public bool HasDerivedRules => _derivedRules.Count > 0;
    /// <summary>Null with no item picked.</summary>
    [Reactive] public IReadOnlyList<PreviewGroup>? Preview { get; private set; }

    public ReactiveCommand<int, Unit> SetTierFilterCommand { get; }
    public ReactiveCommand<Unit, Unit> AddRuleCommand { get; }

    /// <summary>Onto the item whose id is the parameter (a list row's menu), or the open one without.</summary>
    public ReactiveCommand<string?, Unit> CopyRulesCommand { get; }

    /// <summary>Delete the hand-typed rules of the item whose id is the parameter, or the open one without.</summary>
    public ReactiveCommand<string?, Unit> ClearRulesCommand { get; }

    /// <summary>Where the divider between the rules and the preview sits, as the rules' share of the height.</summary>
    public double? SplitterPosition
    {
        get => _settings.Current.ByItemSplitterPosition;
        set => _settings.Update(s => s.ByItemSplitterPosition = value);
    }

    public void FocusSearch() => RequestViewAction(FocusSearchAction);

    /// <summary>Select an item from outside the list, first clearing the filters if they're hiding it.</summary>
    public void OpenItem(string itemId)
    {
        if (_rows.Lookup(itemId) is not { HasValue: true } row)
            return;
        // The shown list rather than the filter itself: edits don't re-filter, so a row the filter
        // would now reject can still be showing.
        if (!_items.Contains(row.Value))
        {
            SearchText = "";
            SetTierFilter(0);
            UntaggedOnly = false;
        }
        SelectedRow = row.Value;
    }

    private Func<ItemRowViewModel, bool> BuildFilter()
    {
        var needle = SearchText.Trim().ToLowerInvariant();
        var tier = TierFilter;
        var untaggedOnly = UntaggedOnly;
        return row =>
        {
            var store = _data.Store;
            if (needle.Length > 0 && !store.ItemMatches(row.ItemId, needle))
                return false;
            if (tier != 0 && row.Tier != tier)
                return false;
            return !untaggedOnly || (store.RuleCount(row.ItemId) == 0 && store.DerivedRuleCount(row.ItemId) == 0);
        };
    }

    private void SetTierFilter(int tier)
    {
        TierFilter = tier;
        foreach (var pill in TierPills)
            pill.Set(pill.Tier == tier);
    }

    /// <summary>One row per item for the life of the loaded data; a reload starts over on the first item.</summary>
    private void Populate()
    {
        var store = _data.Store;
        var rows = store.ItemsSorted().Select(item => new ItemRowViewModel(item)).ToList();
        foreach (var row in rows)
            row.RefreshRules(store);
        _rows.Edit(cache =>
        {
            cache.Clear();
            cache.AddOrUpdate(rows);
        });
        RefreshCoverage();
        SelectedRow = rows.FirstOrDefault();
        if (SelectedRow is null)
            ShowItem(null);
    }

    /// <summary>The other panel edits the same table, so catch up whenever this one comes back into view.</summary>
    public void RefreshCounts()
    {
        var store = _data.Store;
        foreach (var row in _rows.Items)
            row.RefreshRules(store);
        RefreshCoverage();
        if (CurrentItem is not null)
            RenderDetail();
    }

    private void RefreshCoverage()
    {
        var coverage = _data.Store.Coverage();
        CoverageText = $"{coverage.ItemsTagged}/{coverage.ItemsTotal} tagged · {coverage.Rules} rules + {coverage.DerivedRules} from stats";
    }

    // -- detail ---------------------------------------------------------------------

    private void ShowItem(string? itemId)
    {
        CurrentItem = itemId is not null && _data.Store.Items.TryGetValue(itemId, out var item) ? item : null;
        if (CurrentItem is null)
        {
            DetailTitle = "Select an item";
            DetailSub = "";
            Rules.ReplaceAll([]);
            RulesHint = null;
            DerivedRules = [];
            Preview = null;
            return;
        }
        RenderDetail();
    }

    private void RenderDetail()
    {
        var store = _data.Store;
        if (CurrentItem is not { } item || !store.Items.ContainsKey(item.ItemId))
            return;
        var itemId = item.ItemId;
        DetailTitle = item.ItemName;
        var cost = item.Cost != 0 ? $" · {item.Cost} souls" : "";
        DetailSub = $"Tier {item.Tier} · {item.Category}{cost} · id: {itemId}";

        var rules = store.RulesForItem(itemId);
        var derived = store.DerivedRulesForItem(itemId);
        _traitColors = rules.Select(rule => rule.CategoryId)
            .Concat(derived.Select(rule => rule.CategoryId))
            .Distinct()
            .Select((categoryId, index) => (categoryId, index))
            .ToDictionary(pair => pair.categoryId, pair => FormulaText.TraitColors[pair.index % FormulaText.TraitColors.Count]);

        RulesHint = rules.Count > 0 ? null
            : derived.Count == 0
                ? "No rules yet — this item scores 0 in every match.\nAdd a rule for each hero trait that should make you want it."
                : "No hand-typed rules — only its stats count (below).\nAdd a rule for anything the stats don't capture, like an active.";

        // One card per trait + coefficient, with every relation sharing that number checked on it.
        var categories = store.CategoriesOrdered();
        var cards = new OrderedDictionary<(string CategoryId, double Coefficient), List<Relation>>();
        foreach (var (categoryId, relation, coefficient) in rules)
        {
            if (!cards.TryGetValue((categoryId, coefficient), out var relations))
            {
                relations = [];
                cards[(categoryId, coefficient)] = relations;
            }
            relations.Add(relation);
        }
        var oldCards = Rules.ToList();
        Rules.ReplaceAll(cards.Select(card => new RuleCardViewModel(
            store, itemId, categories, new RuleTarget(card.Key.CategoryId, card.Value), card.Key.Coefficient,
            _traitColors[card.Key.CategoryId], OnRuleChanged, OnRuleRemoved)));
        foreach (var card in oldCards)
            card.Dispose();

        DerivedRules = derived.Select(rule => new DerivedRuleEntry(
                store.Categories.TryGetValue(rule.CategoryId, out var category) ? category.CategoryName : rule.CategoryId,
                rule.Relation,
                Format.Num(NumberFormat.Round(DataStore.SumAmounts(rule.Parts), 3)),
                rule.Parts.Select(part => part.Describe()).ToList(),
                _traitColors[rule.CategoryId]))
            .ToList();

        RenderPreview();
    }

    private void RenderPreview()
    {
        if (CurrentItem is not { } item)
        {
            Preview = null;
            return;
        }
        var store = _data.Store;
        var groups = new List<PreviewGroup>();
        foreach (var relation in Relations.All)
        {
            var top = ItemScoring.ItemContributions(store, item.ItemId, relation, _previewHeroes);
            if (top.Count == 0)
                continue;

            // Each trait's share of the sum in its own column, coloured like its rule card: only when
            // there's more than one trait to split.
            var traits = _traitColors.Keys
                .Where(categoryId => top.Any(contribution => contribution.Parts.Any(part => part.CategoryId == categoryId)))
                .ToList();
            if (traits.Count < 2)
                traits = [];

            var best = top[0].Amount;
            var rows = top.Select(contribution =>
            {
                var parts = contribution.Parts.ToDictionary(part => part.CategoryId);
                var pieces = traits
                    .Select(categoryId => parts.TryGetValue(categoryId, out var part)
                        ? new PreviewPiece(part.Amount, _traitColors.GetValueOrDefault(categoryId, Palette.TextFaint), FormulaText.Arithmetic(part))
                        : null)
                    .ToList();
                return new PreviewRow(
                    contribution.HeroId,
                    contribution.HeroName,
                    pieces,
                    FormulaText.Amount(contribution.Amount, signed: true),
                    contribution.Amount == best,
                    string.Join("\n", contribution.Parts.Select(FormulaText.Arithmetic)));
            }).ToList();
            groups.Add(new PreviewGroup(relation.Label().ToUpperInvariant(), Palette.RelationColor(relation), traits.Count, rows));
        }
        Preview = groups;
    }

    // -- edits ----------------------------------------------------------------------

    private void OnRuleChanged(RuleTarget old, RuleTarget now, double coefficient)
    {
        if (CurrentItem is not { } item)
            return;
        var store = _data.Store;
        var retargeted = !old.Equals(now);
        if (retargeted)
        {
            foreach (var relation in old.Relations)
                store.SetCoefficient(item.ItemId, old.CategoryId, relation, 0);
        }
        foreach (var relation in now.Relations)
            store.SetCoefficient(item.ItemId, now.CategoryId, relation, coefficient);

        if (retargeted)
            // Retargeting moves the card in the sorted list, so the cards are rebuilt: deferred,
            // since we're inside a change coming from the card that rebuild replaces.
            RxApp.MainThreadScheduler.Schedule(() => AfterEdit(rerenderRules: true));
        else
            AfterEdit(rerenderRules: false);
    }

    private void OnRuleRemoved(RuleTarget target)
    {
        if (CurrentItem is not { } item)
            return;
        foreach (var relation in target.Relations)
            _data.Store.SetCoefficient(item.ItemId, target.CategoryId, relation, 0);
        AfterEdit(rerenderRules: true);
    }

    /// <summary>"+ Add rule": the first trait without an against rule, at a mild 2.</summary>
    private void AddRule()
    {
        if (CurrentItem is not { } item)
            return;
        var store = _data.Store;
        var used = store.RulesForItem(item.ItemId).Select(rule => (rule.CategoryId, rule.Relation)).ToHashSet();
        var category = store.CategoriesOrdered().FirstOrDefault(c => !used.Contains((c.CategoryId, Relation.Against)));
        if (category is null)
            return;
        store.SetCoefficient(item.ItemId, category.CategoryId, Relation.Against, 2.0);
        AfterEdit(rerenderRules: true);
    }

    private void CopyRules(string? itemId)
    {
        if (OpenTarget(itemId) is not { } target)
            return;
        var store = _data.Store;
        var sources = store.ItemsSorted().Where(item => item.ItemId != target.ItemId && store.RuleCount(item.ItemId) > 0).ToList();
        if (sources.Count == 0)
        {
            DetailSub = "No other item has rules yet — nothing to copy.";
            return;
        }

        _modals.ShowModal(new ChoiceModalViewModel(
            _modals,
            "Copy rules",
            $"Replace {target.ItemName}'s rules with those from:",
            sources.Select(item => new Choice(item.ItemName, $"T{item.Tier} · {FormulaText.RulesBadge(store.RuleCount(item.ItemId), 0).Text}",
                ArtKind.Item, item.ItemId, Palette.ShopColor(item.Category))).ToList(),
            index =>
            {
                if (_data.Store.CopyItemRules(sources[index].ItemId, target.ItemId))
                    AfterEdit(rerenderRules: true);
            }));
    }

    private void ClearRules(string? itemId)
    {
        if (OpenTarget(itemId) is not { } item)
            return;
        var count = _data.Store.RuleCount(item.ItemId);
        if (count == 0)
            return;
        var stats = _data.Store.DerivedRuleCount(item.ItemId) > 0 ? " The rules from its stats stay." : "";
        _modals.Confirm(
            $"Delete {(count == 1 ? "the rule" : $"all {count} rules")} on {item.ItemName}?{stats}\n\nThis can't be undone.",
            "Clear rules",
            () =>
            {
                if (_data.Store.ClearItemRules(item.ItemId))
                    AfterEdit(rerenderRules: true);
            },
            destructive: true);
    }

    /// <summary>Open the item a list row's menu names, so the action shows where it lands; without one, the open item.</summary>
    private Item? OpenTarget(string? itemId)
    {
        if (itemId is not null)
            OpenItem(itemId);
        return CurrentItem;
    }

    private void AfterEdit(bool rerenderRules)
    {
        if (CurrentItem is { } item && _rows.Lookup(item.ItemId) is { HasValue: true } row)
            row.Value.RefreshRules(_data.Store);
        RefreshCoverage();
        if (rerenderRules)
            RenderDetail();
        else
            RenderPreview();
        _data.MarkEdited(DataFiles.ItemCoefficients);
    }
}
