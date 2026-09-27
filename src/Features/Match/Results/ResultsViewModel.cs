using System.Reactive;
using System.Reactive.Subjects;
using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Results;

/// <summary>
/// One tab's worth of recommendations: a flat ranked list or collapsible tier sections, trimmed to
/// items within some share of the best one, plus in formula order the items only the match data
/// likes. Rows and headers outlive a refresh; the visible list is just re-ordered.
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

    public const string DataPicksKey = "data";
    public const string DataPicksTitle = "Match data also likes";
    public const string DataPicksNote =
        "Formula score 0 or less, but a standout in real matches for this line-up. Worth a look for a missing rule.";

    private readonly IReadOnlyDictionary<int, string> _tierLabels;
    private readonly string _nothingPickedHint;
    private readonly Dictionary<string, ResultRowViewModel> _rows = [];
    private readonly Dictionary<string, SectionHeaderViewModel> _headers = [];
    private readonly HashSet<string> _collapsed = [];

    private IReadOnlyList<ScoredItem> _scored = [];
    private string _dataTip = "";
    private RankBy _rankBy;
    private bool _byTier;
    private double? _minFraction;

    public ResultsViewModel(string emptyHint, IReadOnlyDictionary<int, string>? tierLabels = null)
    {
        _tierLabels = tierLabels ?? DefaultTierLabels;
        _nothingPickedHint = emptyHint;
        EmptyHint = emptyHint;
        OpenFormulaCommand = ReactiveCommand.Create<string>(_formulaRequested.OnNext);
    }

    /// <summary>Section headers and rows, in display order.</summary>
    public ResettableCollection<ViewModelBase> Entries { get; } = [];

    [Reactive] public string EmptyHint { get; private set; }

    /// <summary>Nothing to list, so the empty hint shows instead.</summary>
    [Reactive] public bool IsEmpty { get; private set; } = true;

    [Reactive] public string Summary { get; private set; } = "";
    [Reactive] public string SummaryTip { get; private set; } = "";
    [Reactive] public string? SelectedItemId { get; private set; }

    /// <summary>
    /// A click picked a row, or cleared the pick (null) by clicking it again: the explanation follows.
    /// Not raised when a refresh drops the selection.
    /// </summary>
    public IObservable<string?> RowClicked => _rowClicked;
    private readonly Subject<string?> _rowClicked = new();

    /// <summary>A row's context menu asked to open that item's rules on the Item Formulas page.</summary>
    public IObservable<string> FormulaRequested => _formulaRequested;
    private readonly Subject<string> _formulaRequested = new();

    public ReactiveCommand<string, Unit> OpenFormulaCommand { get; }

    /// <summary>Every item the tab could list, scored for the line-up (<see cref="ItemScoring.ScoreAll"/>).</summary>
    public void SetResults(IReadOnlyList<ScoredItem> scored, string dataTip)
    {
        _scored = scored;
        _dataTip = dataTip;
        Render();
    }

    /// <summary>
    /// What ranks the list, flat or in tier sections, and the share of the best item's measure an item
    /// needs to be kept: 0 keeps everything above 0, null keeps every item however it scores. The
    /// cutoff is measured against the best item overall either way, so switching layout only
    /// rearranges the same items.
    /// </summary>
    public void SetDisplay(RankBy rankBy, bool byTier, double? minFraction)
    {
        _rankBy = rankBy;
        _byTier = byTier;
        _minFraction = minFraction;
        Render();
    }

    /// <summary>Pick a row, or clear the pick when it's the one already picked.</summary>
    public void Select(ResultRowViewModel row)
    {
        if (row.IsSelected)
        {
            SetSelection(null);
            _rowClicked.OnNext(null);
            return;
        }
        if (_rows.GetValueOrDefault(SelectedItemId ?? "") is { } previous)
            previous.IsSelected = false;
        row.IsSelected = true;
        SelectedItemId = row.ItemId;
        _rowClicked.OnNext(row.ItemId);
    }

    public void ToggleSection(string key)
    {
        if (!_collapsed.Remove(key))
            _collapsed.Add(key);
        Render();
    }

    private void Render()
    {
        var everyItem = _minFraction is null;
        var ranked = Ranked();
        var positive = ranked.Count(entry => entry.Measure > 0);
        var best = ranked.Count > 0 ? ranked[0].Measure : 0.0;
        var cutoff = best > 0 ? best * (_minFraction ?? 0) : 0.0;
        var shown = everyItem ? ranked : ranked.Where(entry => entry.Measure > 0 && entry.Measure >= cutoff).ToList();
        var picks = _rankBy == RankBy.Formula && !everyItem ? ItemScoring.DataOnlyPicks(_scored) : [];

        // With no heroes picked, "every item" would be the whole shop at 0.
        var nothingScored = _scored.All(item => item.Score == 0 && item.Data.Count == 0);
        IsEmpty = nothingScored || (shown.Count == 0 && picks.Count == 0);
        EmptyHint = Hint(nothingScored);
        Summary = IsEmpty ? "" : SummaryText(positive, shown.Count, cutoff, everyItem);
        SummaryTip = RankTip();

        // Drop a selection that no longer appears, so the explanation and the highlighted row can't
        // disagree. A collapsed section still counts as showing its items: folding one shouldn't
        // lose the pick.
        if (!shown.Any(entry => entry.Item.ItemId == SelectedItemId) && !picks.Any(item => item.ItemId == SelectedItemId))
            SetSelection(null);

        var scale = shown.Select(entry => Math.Abs(entry.Measure)).DefaultIfEmpty(0).Max();
        var placed = new List<ViewModelBase>();
        if (_byTier)
        {
            // In ranked order, so each tier stays sorted.
            foreach (var tierGroup in shown.GroupBy(entry => entry.Item.Tier).OrderBy(group => group.Key))
            {
                var header = Header($"tier{tierGroup.Key}", _tierLabels.GetValueOrDefault(tierGroup.Key, $"Tier {tierGroup.Key}"),
                    Palette.TierColor(tierGroup.Key));
                AddSection(placed, header, tierGroup.Select(entry => Row(entry.Item, Share(entry.Measure, scale), showTier: false)).ToList());
            }
        }
        else
        {
            placed.AddRange(shown.Select(entry => Row(entry.Item, Share(entry.Measure, scale), showTier: true)));
        }

        if (picks.Count > 0)
        {
            // Their bars are on the same scale as the list's, so a deep negative reads as one.
            var pickScale = Math.Max(scale, picks.Max(item => Math.Abs(item.Score)));
            var header = Header(DataPicksKey, DataPicksTitle, Palette.Accent, DataPicksNote);
            AddSection(placed, header, picks.Select(item => Row(item, Share(item.Score, pickScale), showTier: true)).ToList());
        }

        Entries.ReplaceAll(placed);
    }

    /// <summary>Every item with the measure the list is ranked by, best first.</summary>
    private List<(ScoredItem Item, double Measure)> Ranked()
    {
        var bestScore = _scored.Select(item => item.Score).DefaultIfEmpty(0).Max();
        var bestData = _scored.Select(item => item.DataStrength).DefaultIfEmpty(0).Max();
        return _scored
            .Select(item => (Item: item, Measure: _rankBy switch
            {
                RankBy.MatchData => item.DataStrength,
                RankBy.Both => Math.Min(item.Score / (bestScore > 0 ? bestScore : 1), item.DataStrength / (bestData > 0 ? bestData : 1)),
                _ => item.Score,
            }))
            .OrderByDescending(entry => entry.Measure)
            .ThenByDescending(entry => entry.Item.Score)
            .ThenBy(entry => entry.Item.ItemName, StringComparer.Ordinal)
            .ToList();
    }

    private static double Share(double measure, double scale) => scale != 0 ? measure / scale : 0.0;

    private void AddSection(List<ViewModelBase> placed, SectionHeaderViewModel header, IReadOnlyList<ResultRowViewModel> rows)
    {
        var collapsed = _collapsed.Contains(header.Key);
        header.SetValues(rows.Count, collapsed);
        placed.Add(header);
        if (!collapsed)
            placed.AddRange(rows);
    }

    private string SummaryText(int positive, int shown, double cutoff, bool everyItem)
    {
        if (everyItem)
            return $"All {shown} item{(shown != 1 ? "s" : "")}  ·  {positive} {RankNoun()}";
        var noun = $"item{(positive != 1 ? "s" : "")} {RankNoun()}";
        return shown < positive
            ? $"{shown} of {positive} {noun}  ·  cutoff {MeasureText(cutoff)}"
            : $"{positive} {noun}";
    }

    private string RankNoun() => _rankBy switch
    {
        RankBy.MatchData => "the match data rates above 0",
        RankBy.Both => "both the formula and the data rate above 0",
        _ => "scoring above 0",
    };

    private string MeasureText(double measure) => _rankBy switch
    {
        RankBy.MatchData => NumberFormat.Fixed(measure, 2),
        RankBy.Both => $"{Math.Round(measure * 100)}%",
        _ => Format.Num(Math.Round(measure)),
    };

    private string RankTip() => _rankBy switch
    {
        RankBy.MatchData =>
            "Ranked by the match data: the enemies lift plus a third of your lift (your hero's lifts run\n"
            + "about three times bigger). The bar shows that; the number on the right is still the formula score.",
        RankBy.Both =>
            "Only items the formula and the match data both rate above 0, each measured as a share of\n"
            + "its best item; an item ranks as high as the less keen of the two.",
        _ => "Ranked by the formula score. The data numbers are a second opinion from real matches.",
    };

    private string Hint(bool nothingScored)
    {
        if (_rankBy == RankBy.Formula || nothingScored)
            return _nothingPickedHint;
        if (_scored.All(item => item.Data.Count == 0))
            return "The match data has nothing on these heroes.\n\nIt covers your enemies (Full Match only) and your own hero, "
                   + "never allies. Data → Fetch Match Stats fetches it.";
        return _rankBy == RankBy.Both
            ? "No item is rated above 0 by both the formula and the match data."
            : "The match data rates no item above 0 for these heroes.";
    }

    private void SetSelection(string? itemId)
    {
        if (itemId is null && _rows.GetValueOrDefault(SelectedItemId ?? "") is { } previous)
            previous.IsSelected = false;
        SelectedItemId = itemId;
    }

    private ResultRowViewModel Row(ScoredItem scored, double fraction, bool showTier)
    {
        if (!_rows.TryGetValue(scored.ItemId, out var row))
        {
            row = new ResultRowViewModel(scored.ItemId, scored.ItemName, scored.ShopCategory, scored.Tier);
            _rows[scored.ItemId] = row;
        }
        row.SetValues(scored, fraction, showTier, _dataTip);
        row.IsSelected = scored.ItemId == SelectedItemId;
        return row;
    }

    private SectionHeaderViewModel Header(string key, string title, Color color, string? note = null)
    {
        if (!_headers.TryGetValue(key, out var header))
        {
            header = new SectionHeaderViewModel(key, title, color, note);
            _headers[key] = header;
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
