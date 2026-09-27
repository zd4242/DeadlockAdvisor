using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Theme;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Results;

/// <summary>
/// One recommendation. Built once per item and updated in place: the list is re-sorted and
/// re-filtered on every change to the match, and rebuilding rows each time is what made the
/// Python app's lists lag.
/// </summary>
public class ResultRowViewModel : ViewModelBase
{
    public static readonly string StandoutTip =
        "Standout in real matches for this line-up: the enemies lift plus a third of your lift\n"
        + $"comes to {Format.Num(ItemScoring.PickMinAgainst)} or more (your hero's lifts run about three times bigger).";

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

    public string TierText => $"T{Tier}";
    public Color TierColor => Palette.TierColor(Tier);
    public string ShopText => ShopCategory.Length > 0 ? ShopCategory.ToUpperInvariant() : "—";
    public Color ShopColor => Palette.ShopColor(ShopCategory);

    /// <summary>The formula score, whatever the list is ranked by.</summary>
    [Reactive] public double Score { get; private set; }
    [Reactive] public string ScoreText { get; private set; } = "";
    [Reactive] public bool IsNegative { get; private set; }
    [Reactive] public bool IsZero { get; private set; }

    /// <summary>The bar: the ranking's measure over the largest one on screen, below 0 for a negative one.</summary>
    [Reactive] public double Fraction { get; private set; }

    /// <summary>Only in the flat list, where no section header says which tier the item is from.</summary>
    [Reactive] public bool ShowTier { get; private set; }

    [Reactive] public bool IsStandout { get; private set; }
    [Reactive] public OrderedDictionary<string, double>? Data { get; private set; }
    [Reactive] public bool HasData { get; private set; }
    [Reactive] public string? DataTip { get; private set; }
    [Reactive] public bool IsSelected { get; set; }

    public void SetValues(ScoredItem scored, double fraction, bool showTier, string dataTip)
    {
        Score = scored.Score;
        IsNegative = scored.Score < 0;
        IsZero = scored.Score == 0;
        ScoreText = IsNegative ? Format.Num(-scored.Score) : Format.Num(scored.Score);
        Fraction = fraction;
        ShowTier = showTier;
        IsStandout = scored.DataStrength >= 1;
        if (Data is null || !Data.SequenceEqual(scored.Data))
            Data = scored.Data;
        HasData = scored.Data.Count > 0;
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
