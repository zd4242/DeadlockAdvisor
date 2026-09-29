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
public readonly record struct Bars(double Fraction, double? Data = null, string? Tip = null);

/// <summary>
/// One recommendation. Built once per item and updated in place: the list is re-sorted and
/// re-filtered on every change to the match, and rebuilding rows each time is what made the
/// Python app's lists lag.
/// </summary>
public class ResultRowViewModel : ViewModelBase
{
    public static readonly string StandoutTip =
        "Stands out in real matches: players win noticeably more often with it against these enemies, or on your hero.\n"
        + $"Its win-rate gain against the enemies, plus a third of the gain on your hero, comes to {Format.Num(ItemScoring.PickMinAgainst)} point or more\n"
        + "(gains on your own hero usually run about three times bigger).";

    public const string DisagreeTip =
        "The formula and the match data clearly disagree: one rates this item well, the other poorly.\n"
        + "Click it to see why each thinks what it does: a rule may be missing, or the data may reflect\n"
        + "who buys the item more than what it does.";

    public ResultRowViewModel(string itemId, string name, string shopCategory, int tier)
    {
        ItemId = itemId;
        Name = name;
        ShopCategory = shopCategory;
        Tier = tier;
    }

    public string ItemId { get; }
    public string Name { get; }
    public string ShopCategory { get; }
    public int Tier { get; }

    public Color ShopColor => Palette.ShopColor(ShopCategory);

    /// <summary>The number on the right: the formula score, or the blend when ranking by formula and data together.</summary>
    [Reactive] public DisplayAmount Score { get; private set; }

    /// <summary>As printed, so a score that rounds to 0.0 reads as zero rather than a red ▼0.0.</summary>
    [Reactive] public bool IsNegative { get; private set; }
    [Reactive] public bool IsZero { get; private set; }

    /// <summary>The bar: the ranking's measure over the largest one on screen (the formula's part, ranking by both), below 0 for a negative one.</summary>
    [Reactive] public double Fraction { get; private set; }

    /// <summary>A second bar under it, the data's part, when ranking by formula and data together.</summary>
    [Reactive] public double DataFraction { get; private set; }
    [Reactive] public bool HasDataBar { get; private set; }
    [Reactive] public string? BarTip { get; private set; }

    [Reactive] public bool IsStandout { get; private set; }

    /// <summary>Ranking by both, the formula and the data point clearly opposite ways (<see cref="BlendScale.Disagree"/>).</summary>
    [Reactive] public bool Disagrees { get; private set; }
    [Reactive] public OrderedDictionary<string, double>? Data { get; private set; }
    [Reactive] public bool HasData { get; private set; }

    /// <summary>Your hero rarely builds the item, so its gains against the enemies count for less.</summary>
    [Reactive] public bool IsRarelyBuilt { get; private set; }
    [Reactive] public string? DataTip { get; private set; }
    [Reactive] public bool IsSelected { get; set; }

    /// <param name="shown">The number on the right, when it isn't the formula score.</param>
    public void SetValues(ScoredItem scored, Bars bars, string dataTip, double? shown = null, bool disagrees = false)
    {
        Score = new DisplayAmount(shown ?? scored.Score);
        Disagrees = disagrees;
        IsNegative = Score.Shown < 0;
        IsZero = Score.Shown == 0;
        Fraction = bars.Fraction;
        DataFraction = bars.Data ?? 0;
        HasDataBar = bars.Data is not null;
        BarTip = bars.Tip;
        IsStandout = scored.DataStrength >= 1;
        if (Data is null || !Data.SequenceEqual(scored.Data))
            Data = scored.Data;
        HasData = scored.Data.Count > 0;
        IsRarelyBuilt = HasData && scored.RarelyBuilt;
        var tips = new List<string>();
        if (IsRarelyBuilt)
            tips.Add(ExplainText.RarelyBuilt("Your hero", scored.BuildRatio!.Value));
        if (!string.IsNullOrEmpty(dataTip))
            tips.Add(dataTip);
        DataTip = tips.Count > 0 ? string.Join("\n\n", tips) : null;
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
