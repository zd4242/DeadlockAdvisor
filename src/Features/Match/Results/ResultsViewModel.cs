using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Results;

/// <summary>
/// The recommendations: a flat ranked list or collapsible tier sections, trimmed to
/// items within some share of the best one, plus in formula order the items only the match data
/// likes. Rows and headers outlive a refresh; the visible list is just re-ordered.
/// </summary>
public class ResultsViewModel : ViewModelBase
{
    public const string DataPicksKey = "data";
    public const string DataPicksTitle = "Match data also likes";
    public const string DataPicksNote = "Formula score 0 or less, but a standout in real matches for this line-up.";
    public const string DataPicksEditorNote = DataPicksNote + " Worth a look for a missing rule.";

    private readonly string _nothingPickedHint;
    private readonly Dictionary<string, ResultRowViewModel> _rows = [];
    private readonly Dictionary<string, SectionHeaderViewModel> _headers = [];
    private readonly HashSet<string> _collapsed = [];

    private IReadOnlyList<ScoredItem> _scored = [];
    private string _dataTip = "";
    private Func<BlendScale> _blendScale = () => BlendScale.One;
    private RankBy _rankBy;
    private bool _byTier;
    private double? _minFraction;
    private bool _hideRarelyBuilt;
    private bool _hideDisagreed;
    private string? _topItemId;

    public ResultsViewModel(string emptyHint)
    {
        _nothingPickedHint = emptyHint;
        EmptyHint = emptyHint;
        OpenFormulaCommand = ReactiveCommand.Create<string>(_formulaRequested.OnNext, this.WhenAnyValue(vm => vm.ShowsEditors));

        // A header keeps its note, so the data picks' one is rebuilt for its new wording.
        this.WhenAnyValue(vm => vm.ShowsEditors)
            .Skip(1)
            .Subscribe(_ =>
            {
                _headers.Remove(DataPicksKey);
                Render();
            })
            .DisposeWith(Disposables);
        this.WhenAnyValue(vm => vm.ShowsTiers)
            .Skip(1)
            .Subscribe(_ => Render())
            .DisposeWith(Disposables);
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

    /// <summary>
    /// The model editors are shown: rows offer Go to Item Formula on right-click, and the data picks
    /// suggest a rule might be missing.
    /// </summary>
    [Reactive] public bool ShowsEditors { get; set; }

    /// <summary>Rows name their tier and price, unless a tier header above them already does.</summary>
    [Reactive] public bool ShowsTiers { get; set; }

    /// <summary>Every item the tab could list, scored for the line-up (<see cref="ItemScoring.ScoreAll"/>).</summary>
    /// <param name="blendScale">
    /// The units the formula-and-data ranking adds the two in, for this line-up; asked for only when
    /// ranking that way, since measuring a new shape takes a moment.
    /// </param>
    public void SetResults(IReadOnlyList<ScoredItem> scored, string dataTip, Func<BlendScale>? blendScale = null)
    {
        _scored = scored;
        _dataTip = dataTip;
        _blendScale = blendScale ?? (() => BlendScale.One);
        Render();
    }

    /// <summary>
    /// What ranks the list, flat or in tier sections, and the share of the best item's measure an item
    /// needs to be kept: 0 keeps everything above 0, null keeps every item however it scores. The flat
    /// list measures the cutoff against the best item overall; the tier sections against each tier's
    /// best, since a cheap tier's items rarely score as high as a late one's and would all be cut.
    /// Hidden rarely built items, and ranking by both, hidden items the two disagree on are left out
    /// before any of that, so the cutoff and the counts are over what can be listed.
    /// </summary>
    public void SetDisplay(RankBy rankBy, bool byTier, double? minFraction, bool hideRarelyBuilt = false, bool hideDisagreed = false)
    {
        _rankBy = rankBy;
        _byTier = byTier;
        _minFraction = minFraction;
        _hideRarelyBuilt = hideRarelyBuilt;
        _hideDisagreed = hideDisagreed;
        Render();
    }

    /// <summary>Pick a row, or clear the pick when it's the one already picked.</summary>
    public void Select(ResultRowViewModel row)
    {
        var itemId = row.IsSelected ? null : row.ItemId;
        SetSelection(itemId);
        _rowClicked.OnNext(itemId);
    }

    /// <summary>
    /// Pick the best-ranked item listed, or clear the pick when nothing is. Like a refresh dropping
    /// the pick, this doesn't raise <see cref="RowClicked"/>.
    /// </summary>
    public void SelectTop() => SetSelection(_topItemId);

    public void ToggleSection(string key)
    {
        if (!_collapsed.Remove(key))
            _collapsed.Add(key);
        Render();
    }

    private void Render()
    {
        var everyItem = _minFraction is null;
        var blend = _rankBy == RankBy.Both ? _blendScale() : BlendScale.One;
        var ranked = Ranked(blend);
        var positive = ranked.Count(entry => entry.Measure > 0);
        var tierBest = ranked.GroupBy(entry => entry.Item.Tier).ToDictionary(group => group.Key, group => group.Max(entry => entry.Measure));
        var overallCutoff = Math.Max(0, (ranked.Count > 0 ? ranked[0].Measure : 0.0) * (_minFraction ?? 0));
        double Cutoff(int tier) => _byTier ? Math.Max(0, tierBest[tier] * (_minFraction ?? 0)) : overallCutoff;
        var shown = everyItem ? ranked : ranked.Where(entry => entry.Measure > 0 && entry.Measure >= Cutoff(entry.Item.Tier)).ToList();
        var picks = _rankBy == RankBy.Formula && !everyItem ? ItemScoring.DataOnlyPicks(Listable(blend)) : [];

        // With no heroes picked, "every item" would be the whole shop at 0.
        var nothingScored = _scored.All(item => item.Score == 0 && item.Data.Count == 0);
        IsEmpty = nothingScored || (shown.Count == 0 && picks.Count == 0);
        EmptyHint = Hint(nothingScored);
        _topItemId = IsEmpty || shown.Count == 0 ? null : shown[0].Item.ItemId;
        Summary = IsEmpty ? "" : SummaryText(positive, shown.Count, overallCutoff, everyItem);
        SummaryTip = RankTip();

        // Drop a selection that no longer appears, so the explanation and the highlighted row can't
        // disagree; the empty hint lists nothing, even when every item is kept. A collapsed section
        // still counts as showing its items: folding one shouldn't lose the pick.
        if (IsEmpty || (!shown.Any(entry => entry.Item.ItemId == SelectedItemId) && !picks.Any(item => item.ItemId == SelectedItemId)))
            SetSelection(null);

        // Blending, both bars share one scale, the largest part on screen, so they compare directly.
        var scale = _rankBy == RankBy.Both
            ? shown.Select(entry => Math.Max(Math.Abs(blend.FormulaUnits(entry.Item)), Math.Abs(blend.DataUnits(entry.Item)))).DefaultIfEmpty(0).Max()
            : shown.Select(entry => Math.Abs(entry.Measure)).DefaultIfEmpty(0).Max();
        var placed = new List<ViewModelBase>();
        if (_byTier)
        {
            // In ranked order, so each tier stays sorted.
            foreach (var tierGroup in shown.GroupBy(entry => entry.Item.Tier).OrderBy(group => group.Key))
            {
                var header = Header($"tier{tierGroup.Key}", TierLabel(tierGroup.Key), Palette.TierColor(tierGroup.Key));
                AddSection(placed, header, tierGroup.Select(entry => RankedRow(entry.Item, entry.Measure, scale, blend)).ToList());
            }
        }
        else
        {
            placed.AddRange(shown.Select(entry => RankedRow(entry.Item, entry.Measure, scale, blend)));
        }

        if (picks.Count > 0)
        {
            // Their bars are on the same scale as the list's, so a deep negative reads as one.
            var pickScale = Math.Max(scale, picks.Max(item => Math.Abs(item.Score)));
            var header = Header(DataPicksKey, DataPicksTitle, Palette.Data, ShowsEditors ? DataPicksEditorNote : DataPicksNote);
            AddSection(placed, header, picks.Select(item => Row(item, new Bars(Share(item.Score, pickScale)), showsTier: true)).ToList());
        }

        Entries.ReplaceAll(placed);
    }

    /// <summary>Every item with the measure the list is ranked by, best first.</summary>
    private List<(ScoredItem Item, double Measure)> Ranked(BlendScale blend) =>
        Listable(blend)
            .Select(item => (Item: item, Measure: _rankBy switch
            {
                RankBy.MatchData => item.DataStrength,
                RankBy.Both => blend.Blend(item),
                _ => item.Score,
            }))
            .OrderByDescending(entry => entry.Measure)
            .ThenByDescending(entry => entry.Item.Score)
            .ThenBy(entry => entry.Item.ItemName, StringComparer.Ordinal)
            .ToList();

    /// <summary>The items the filters leave in. Only blending puts the two opinions on one scale, so only then can they disagree.</summary>
    private IEnumerable<ScoredItem> Listable(BlendScale blend) =>
        _scored.Where(item => !(_hideRarelyBuilt && item.RarelyBuilt) && !(_hideDisagreed && _rankBy == RankBy.Both && blend.Disagree(item)));

    private static double Share(double measure, double scale) => scale != 0 ? measure / scale : 0.0;

    /// <summary>A listed row, showing the measure it's ranked by; blending, also whether the two opinions disagree.</summary>
    private ResultRowViewModel RankedRow(ScoredItem item, double measure, double scale, BlendScale blend) => _rankBy switch
    {
        RankBy.Both => Row(item, BlendBars(item, measure, scale, blend), shown: measure, disagrees: blend.Disagree(item)),
        RankBy.MatchData => Row(item, DataBars(item, measure, scale), shown: measure),
        _ => Row(item, new Bars(Share(measure, scale))),
    };

    /// <summary>The data strength against the largest on screen, in the data's colour.</summary>
    private static Bars DataBars(ScoredItem item, double measure, double scale) =>
        new(Share(measure, scale),
            Tip: $"Data strength {NumberFormat.Fixed(item.DataStrength, 2)}: {DataWorking(item)}\n\n{DataStrengthNote}",
            Color: Palette.Data);

    /// <summary>The formula's part and the data's, on one scale and below 0 when negative, so it shows which one carries the item.</summary>
    private static Bars BlendBars(ScoredItem item, double measure, double scale, BlendScale blend)
    {
        var formula = blend.FormulaUnits(item);
        var data = blend.DataUnits(item);
        return new Bars(Share(formula, scale), Share(data, scale),
            $"Formula {Format.SignedFixed(formula, 1)} (score {Format.Tenths(item.Score)})\n"
            + $"Data {Format.SignedFixed(data, 1)} ({DataWorking(item)})\n"
            + $"Ranked by the sum: {Format.SignedFixed(measure, 1)}\n\n"
            + ExplainText.BlendScaleNote);
    }

    private const string DataStrengthNote =
        "Win-rate gains in real matches: against these enemies, plus a third of the gain on your hero\n"
        + "(those usually run about three times bigger).";

    /// <summary>"enemies 0.4 + you 1.0 ÷ 3".</summary>
    private static string DataWorking(ScoredItem item)
    {
        var enemies = NumberFormat.Fixed(item.Data.GetValueOrDefault("against"), 2);
        var mine = NumberFormat.Fixed(item.Data.GetValueOrDefault("as"), 2);
        return $"enemies {enemies} + you {mine} ÷ {Format.Num(ItemScoring.PickMinAs / ItemScoring.PickMinAgainst)}";
    }

    private void AddSection(List<ViewModelBase> placed, SectionHeaderViewModel header, IReadOnlyList<ResultRowViewModel> rows)
    {
        var collapsed = _collapsed.Contains(header.Key);
        header.SetValues(rows.Count, collapsed);
        placed.Add(header);
        if (!collapsed)
            placed.AddRange(rows);
    }

    /// <summary>
    /// Counts only: the rank picker beside it names what they're rated by. The editors see the cutoff's
    /// number, or its share when each tier has its own; players see what it means, an item above 0
    /// being one this match wants more than most.
    /// </summary>
    private string SummaryText(int positive, int shown, double cutoff, bool everyItem)
    {
        var above = ShowsEditors ? "above 0" : "suit this match";
        if (everyItem)
            return $"All {shown} item{(shown != 1 ? "s" : "")}  ·  {positive} {above}";
        if (shown >= positive)
            return $"{positive} item{(positive != 1 ? "s" : "")} {above}";
        if (!ShowsEditors)
            return $"Best {shown} of {positive} that suit this match";
        var cutoffText = _byTier ? $"{Math.Round((_minFraction ?? 0) * 100):0}% of each tier's best" : MeasureText(cutoff);
        return $"{shown} of {positive} above 0  ·  cutoff {cutoffText}";
    }

    /// <summary>"Tier 2 · 1,600", the price from the items themselves.</summary>
    private string TierLabel(int tier)
    {
        var cost = _scored.Where(item => item.Tier == tier && item.Cost > 0).Select(item => item.Cost).DefaultIfEmpty(0).Min();
        return cost > 0 ? $"Tier {tier} · {Format.Thousands(cost)}" : $"Tier {tier}";
    }

    private string MeasureText(double measure) => _rankBy switch
    {
        RankBy.MatchData => NumberFormat.Fixed(measure, 2),
        RankBy.Both => Format.SignedFixed(measure, 1),
        _ => Format.Num(Math.Round(measure)),
    };

    private string RankTip() => _rankBy switch
    {
        RankBy.MatchData =>
            "Ranked by the match data: how much more often players win with each item in real matches.\n"
            + DataStrengthNote + "\n"
            + "The bar and the number on the right show that; click an item to see its formula score.",
        RankBy.Both =>
            "Ranked by the formula and the match data added together.\n"
            + ExplainText.BlendScaleNote + "\n"
            + "The number on the right is that sum; the top bar is the formula's part and the lower bar the data's.\n"
            + "An item only one of them has an opinion on ranks on that one alone.\n"
            + "DISAGREE marks an item one rates well and the other poorly.",
        _ => "Ranked by the formula score: the app's own rating, from the heroes' traits and what the item does.\n"
             + "The data numbers are a second opinion from real matches.",
    };

    private string Hint(bool nothingScored)
    {
        if (_rankBy == RankBy.Formula || nothingScored)
            return _nothingPickedHint;
        if (_scored.All(item => item.Data.Count == 0))
            return "The match data has nothing on these heroes.\n\nIt covers your enemies and your own hero, "
                   + "never allies. Data → Download Match Data fetches it.";
        return _rankBy == RankBy.Both
            ? "The formula and the match data together rate no item above 0 for these heroes."
            : "The match data rates no item above 0 for these heroes.";
    }

    private void SetSelection(string? itemId)
    {
        if (_rows.GetValueOrDefault(SelectedItemId ?? "") is { } previous)
            previous.IsSelected = false;
        if (_rows.GetValueOrDefault(itemId ?? "") is { } row)
            row.IsSelected = true;
        SelectedItemId = itemId;
    }

    /// <param name="shown">The number on the right, when it isn't the formula score.</param>
    /// <param name="showsTier">
    /// Whether the row names its tier, when <see cref="ShowsTiers"/> asks for it: always, unless a tier header above it already does.
    /// </param>
    private ResultRowViewModel Row(ScoredItem scored, Bars bars, double? shown = null, bool disagrees = false, bool? showsTier = null)
    {
        if (!_rows.TryGetValue(scored.ItemId, out var row))
        {
            row = new ResultRowViewModel(scored.ItemId, scored.ItemName, scored.ShopCategory, scored.Tier, scored.Cost);
            _rows[scored.ItemId] = row;
        }
        row.SetValues(scored, bars, _dataTip, shown, disagrees);
        row.IsSelected = scored.ItemId == SelectedItemId;
        row.ShowsTier = ShowsTiers && (showsTier ?? !_byTier);
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
