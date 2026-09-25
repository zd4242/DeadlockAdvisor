using Avalonia.Media;
using DeadlockAdvisor.Core;
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

    [Reactive] public double Score { get; private set; }
    [Reactive] public string ScoreText { get; private set; } = "";
    [Reactive] public double Fraction { get; private set; }

    /// <summary>Only in the flat list, where no section header says which tier the item is from.</summary>
    [Reactive] public bool ShowTier { get; private set; }

    [Reactive] public OrderedDictionary<string, double>? Data { get; private set; }
    [Reactive] public bool HasData { get; private set; }
    [Reactive] public string? DataTip { get; private set; }
    [Reactive] public bool IsSelected { get; set; }

    public void SetValues(double score, double fraction, bool showTier, OrderedDictionary<string, double> data, string dataTip)
    {
        Score = score;
        ScoreText = Format.Num(score);
        Fraction = fraction;
        ShowTier = showTier;
        if (Data is null || !Data.SequenceEqual(data))
            Data = data;
        HasData = data.Count > 0;
        DataTip = string.IsNullOrEmpty(dataTip) ? null : dataTip;
    }
}

/// <summary>A tier section's title; clicking it collapses or expands the section.</summary>
public class TierHeaderViewModel(int tier, string title) : ViewModelBase
{
    public int Tier { get; } = tier;
    public string Title { get; } = title.ToUpperInvariant();
    public Color Color => Palette.TierColor(Tier);
    public IBrush Brush => new SolidColorBrush(Color);

    [Reactive] public string CountText { get; private set; } = "";
    [Reactive] public string Chevron { get; private set; } = "▾";
    [Reactive] public string ToolTipText { get; private set; } = "";

    public void SetValues(int count, bool collapsed)
    {
        CountText = count.ToString();
        Chevron = collapsed ? "▸" : "▾";
        ToolTipText = collapsed ? "Click to expand" : "Click to collapse";
    }
}

