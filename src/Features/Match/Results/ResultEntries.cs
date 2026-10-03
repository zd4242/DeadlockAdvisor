using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Match.Explain;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Theme;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Results;

/// <summary>A row's bars: fractions of a full bar, below 0 for a negative one.</summary>
/// <param name="Data">A second bar, the data's part, when the ranking adds both opinions.</param>
/// <param name="Tip">How the bars were worked out, when that isn't just the score.</param>
/// <param name="Color">The first bar's colour, when it isn't the formula's.</param>
public readonly record struct Bars(double Fraction, double? Data = null, string? Tip = null, Color? Color = null);

/// <summary>
/// One recommendation. Built once per item and updated in place: the list is re-sorted and
/// re-filtered on every change to the match, and rebuilding rows each time would make it lag.
/// </summary>
public class ResultRowViewModel : ViewModelBase
{
    public static readonly string StandoutTip =
        "Stands out in real matches: players win noticeably more often with it against these enemies, or on your hero.\n"
        + $"Its win-rate gain against the enemies, plus a third of the gain on your hero, comes to {Format.Num(ItemScoring.PickMinAgainst)} point or more\n"
        + "(gains on your own hero usually run about three times bigger).";

    public const string HideRarelyBuiltHint =
        $"Filters → \"{MatchViewModel.HideRarelyBuiltLabel}\" leaves these items out of the list.";

    /// <param name="formulaLikes">Whether the formula is the one rating the item well.</param>
    public static string DisagreeText(bool formulaLikes)
    {
        var (likes, dislikes) = formulaLikes ? ("formula", "match data") : ("match data", "formula");
        return $"The formula and the match data clearly disagree: the {likes} rates this item well, the {dislikes} poorly.\n"
            + "Click it to see why each thinks what it does: a rule may be missing, or the data may reflect\n"
            + "who buys the item more than what it does.\n"
            + $"Filters → \"{MatchViewModel.HideDisagreedLabel}\" leaves these items out of the list.";
    }

    public ResultRowViewModel(string itemId, string name, string shopCategory, int tier, int cost = 0)
    {
        ItemId = itemId;
        Name = name;
        ShopCategory = shopCategory;
        Tier = tier;
        TierText = cost > 0 ? $"T{tier} · {Format.Compact(cost)}" : $"T{tier}";
    }

    public string ItemId { get; }
    public string Name { get; }
    public string ShopCategory { get; }
    public int Tier { get; }

    public Color ShopColor => Palette.ShopColor(ShopCategory);

    /// <summary>"T2 · 1.6k": the tier and price, on rows that aren't already under their tier's header.</summary>
    public string TierText { get; }
    public Color TierColor => Palette.TierColor(Tier);
    [Reactive] public bool ShowsTier { get; set; }

    /// <summary>The number on the right: what the list is ranked by, the formula score, the data strength or the blend.</summary>
    [Reactive] public DisplayAmount Score { get; private set; }

    /// <summary>As printed, so a score that rounds to 0.0 reads as zero rather than a red ▼0.0.</summary>
    [Reactive] public bool IsNegative { get; private set; }
    [Reactive] public bool IsZero { get; private set; }

    /// <summary>The bar: the ranking's measure over the largest one on screen (the formula's part, ranking by both), below 0 for a negative one.</summary>
    [Reactive] public double Fraction { get; private set; }

    /// <summary>The data's colour when the bar is the data strength, so it matches the data bar ranking by both.</summary>
    [Reactive] public Color BarColor { get; private set; } = Palette.Formula;

    /// <summary>A second bar under it, the data's part, when ranking by formula and data together.</summary>
    [Reactive] public double DataFraction { get; private set; }
    [Reactive] public bool HasDataBar { get; private set; }
    [Reactive] public string? BarTip { get; private set; }

    [Reactive] public bool IsStandout { get; private set; }

    /// <summary>Ranking by both, the formula and the data point clearly opposite ways (<see cref="BlendScale.Disagree"/>).</summary>
    [Reactive] public bool Disagrees { get; private set; }
    [Reactive] public string? DisagreeTip { get; private set; }
    [Reactive] public OrderedDictionary<string, double>? Data { get; private set; }
    [Reactive] public bool HasData { get; private set; }

    /// <summary>Your hero rarely builds the item, so its gains against the enemies count for less.</summary>
    [Reactive] public bool IsRarelyBuilt { get; private set; }
    [Reactive] public string? RarelyBuiltTip { get; private set; }
    [Reactive] public string? DataTip { get; private set; }
    [Reactive] public bool IsSelected { get; set; }

    /// <param name="shown">The number on the right, when it isn't the formula score.</param>
    public void SetValues(ScoredItem scored, Bars bars, string dataTip, double? shown = null, bool disagrees = false)
    {
        Score = new DisplayAmount(shown ?? scored.Score);
        Disagrees = disagrees;
        DisagreeTip = disagrees ? DisagreeText(formulaLikes: scored.Score > 0) : null;
        IsNegative = Score.Shown < 0;
        IsZero = Score.Shown == 0;
        Fraction = bars.Fraction;
        BarColor = bars.Color ?? Palette.Formula;
        DataFraction = bars.Data ?? 0;
        HasDataBar = bars.Data is not null;
        BarTip = bars.Tip;
        IsStandout = scored.DataStrength >= 1;
        if (Data is null || !Data.SequenceEqual(scored.Data))
            Data = scored.Data;
        HasData = scored.Data.Count > 0;
        IsRarelyBuilt = HasData && scored.RarelyBuilt;
        RarelyBuiltTip = IsRarelyBuilt ? ExplainText.RarelyBuilt("Your hero", scored.BuildRatio!.Value) + "\n" + HideRarelyBuiltHint : null;
        DataTip = string.IsNullOrEmpty(dataTip) ? null : dataTip;
    }
}

/// <summary>A section's title, a tier or the data picks; clicking it collapses or expands the section.</summary>
public class SectionHeaderViewModel(string key, string title, Color color, string? note = null) : ViewModelBase
{
    public string Key { get; } = key;
    public string Title { get; } = title.ToUpperInvariant();
    public Color Color { get; } = color;
    public IBrush Brush => new SolidColorBrush(Color);

    /// <summary>A line under the title saying what the section is.</summary>
    public string? Note { get; } = note;
    public bool HasNote => Note is not null;

    [Reactive] public string CountText { get; private set; } = "";
    [Reactive] public string Chevron { get; private set; } = "▾";
    [Reactive] public string ToolTipText { get; private set; } = "";
    [Reactive] public bool IsCollapsed { get; private set; }

    public void SetValues(int count, bool collapsed)
    {
        CountText = count.ToString();
        IsCollapsed = collapsed;
        Chevron = collapsed ? "▸" : "▾";
        ToolTipText = collapsed ? "Click to expand" : "Click to collapse";
    }
}
