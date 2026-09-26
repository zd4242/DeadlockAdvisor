using System.Reactive;
using System.Reactive.Subjects;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Scoring;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Results;

/// <summary>
/// One tab's worth of recommendations: a flat ranked list or collapsible tier sections, trimmed to
/// items within some share of the best score. Rows and headers outlive a refresh; the visible list
/// is just re-ordered.
/// </summary>
public class ResultsViewModel : ViewModelBase
{
    public static readonly IReadOnlyDictionary<int, string> DefaultTierLabels = new Dictionary<int, string>
    {
        [1] = "Tier 1 · 800",
        [2] = "Tier 2 · 1600",
        [3] = "Tier 3 · 3200",
        [4] = "Tier 4 · 6400",
    };

    private readonly IReadOnlyDictionary<int, string> _tierLabels;
    private readonly Dictionary<string, ResultRowViewModel> _rows = [];
    private readonly Dictionary<int, TierHeaderViewModel> _headers = [];
    private readonly HashSet<int> _collapsed = [];

    private OrderedDictionary<int, List<ScoredItem>> _grouped = [];
    private string _dataTip = "";
    private bool _byTier;
    private double _minFraction;

    public ResultsViewModel(string emptyHint, IReadOnlyDictionary<int, string>? tierLabels = null)
    {
        _tierLabels = tierLabels ?? DefaultTierLabels;
        EmptyHint = emptyHint;
        OpenFormulaCommand = ReactiveCommand.Create<string>(_formulaRequested.OnNext);
    }

    /// <summary>Tier headers and rows, in display order.</summary>
    public ResettableCollection<ViewModelBase> Entries { get; } = [];

    public string EmptyHint { get; }

    /// <summary>Nothing scores above 0, so the empty hint shows instead of a list.</summary>
    [Reactive] public bool IsEmpty { get; private set; } = true;

    [Reactive] public string Summary { get; private set; } = "";
    [Reactive] public string? SelectedItemId { get; private set; }

    /// <summary>A click picked a row: the explanation follows it. Not raised when a refresh drops the selection.</summary>
    public IObservable<string> RowClicked => _rowClicked;
    private readonly Subject<string> _rowClicked = new();

    /// <summary>A row's context menu asked to open that item's rules on the Item Formulas page.</summary>
    public IObservable<string> FormulaRequested => _formulaRequested;
    private readonly Subject<string> _formulaRequested = new();

    public ReactiveCommand<string, Unit> OpenFormulaCommand { get; }

    public void SetResults(OrderedDictionary<int, List<ScoredItem>> grouped, string dataTip)
    {
        _grouped = grouped;
        _dataTip = dataTip;
        Render();
    }

    /// <summary>
    /// Flat list or tier sections, and the share of the best item's score an item needs to be kept
    /// (0 keeps all). The cutoff is measured against the best item overall either way, so switching
    /// layout only rearranges the same items.
    /// </summary>
    public void SetDisplay(bool byTier, double minFraction)
    {
        _byTier = byTier;
        _minFraction = minFraction;
        Render();
    }

    public void Select(ResultRowViewModel row)
    {
        if (_rows.GetValueOrDefault(SelectedItemId ?? "") is { } previous)
            previous.IsSelected = false;
        row.IsSelected = true;
        SelectedItemId = row.ItemId;
        _rowClicked.OnNext(row.ItemId);
    }

    public void ToggleTier(int tier)
    {
        if (!_collapsed.Remove(tier))
            _collapsed.Add(tier);
        Render();
    }

    private void Render()
    {
        var ranked = _grouped.Values.SelectMany(items => items).OrderByDescending(item => item.Score).ToList();
        var placed = new List<ViewModelBase>();

        IsEmpty = ranked.Count == 0;
        if (IsEmpty)
        {
            Summary = "";
            SetSelection(null);
            Entries.ReplaceAll(placed);
            return;
        }

        var best = ranked[0].Score;
        var cutoff = best * _minFraction;
        var shown = ranked.Where(item => item.Score >= cutoff).ToList();
        var noun = $"item{(ranked.Count != 1 ? "s" : "")} scoring above 0";
        Summary = shown.Count < ranked.Count
            ? $"{shown.Count} of {ranked.Count} {noun}  ·  cutoff {Format.Num(Math.Round(cutoff))}"
            : $"{ranked.Count} {noun}";

        // Drop a selection that no longer appears, so the explanation and the highlighted row can't
        // disagree. A collapsed tier still counts as showing its items: folding a section shouldn't
        // lose the pick.
        if (!shown.Any(item => item.ItemId == SelectedItemId))
            SetSelection(null);

        if (_byTier)
        {
            // In ranked order, so each tier stays sorted.
            foreach (var tierGroup in shown.GroupBy(item => item.Tier).OrderBy(group => group.Key))
            {
                var collapsed = _collapsed.Contains(tierGroup.Key);
                var header = Header(tierGroup.Key);
                header.SetValues(tierGroup.Count(), collapsed);
                placed.Add(header);
                if (!collapsed)
                    placed.AddRange(tierGroup.Select(item => Row(item, best, showTier: false)));
            }
        }
        else
        {
            placed.AddRange(shown.Select(item => Row(item, best, showTier: true)));
        }

        Entries.ReplaceAll(placed);
    }

    private void SetSelection(string? itemId)
    {
        if (itemId is null && _rows.GetValueOrDefault(SelectedItemId ?? "") is { } previous)
            previous.IsSelected = false;
        SelectedItemId = itemId;
    }

    private ResultRowViewModel Row(ScoredItem scored, double best, bool showTier)
    {
        if (!_rows.TryGetValue(scored.ItemId, out var row))
        {
            row = new ResultRowViewModel(scored.ItemId, scored.ItemName, scored.ShopCategory, scored.Tier);
            _rows[scored.ItemId] = row;
        }
        row.SetValues(scored.Score, best != 0 ? scored.Score / best : 0.0, showTier, scored.Data, _dataTip);
        row.IsSelected = scored.ItemId == SelectedItemId;
        return row;
    }

    private TierHeaderViewModel Header(int tier)
    {
        if (!_headers.TryGetValue(tier, out var header))
        {
            header = new TierHeaderViewModel(tier, _tierLabels.GetValueOrDefault(tier, $"Tier {tier}"));
            _headers[tier] = header;
        }
        return header;
    }

    /// <summary>Forget every row, e.g. after a reload renamed or re-tiered items.</summary>
    public void Reset()
    {
        _rows.Clear();
        _headers.Clear();
        SetSelection(null);
    }
}
