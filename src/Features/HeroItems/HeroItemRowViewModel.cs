using Avalonia.Media;
using Avalonia.Media.Immutable;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.HeroItems;

/// <summary>One item's row in a hero's item table, as printed.</summary>
/// <param name="average">The hero's own win rate over the same matches, which each row's is read against.</param>
public class HeroItemRowViewModel(HeroItemRow row, double average)
{
    /// <summary>A win rate within this of the average, as a fraction, reads as average.</summary>
    public const double AverageMargin = 0.005;

    /// <summary>The distance from the average, as a fraction, at which the win rate's tint is at its strongest.</summary>
    public const double FullTintAt = 0.05;

    /// <summary>"52.59%".</summary>
    public static string Percent(double share) => NumberFormat.Fixed(share * 100, 2) + "%";

    /// <summary>"+0.06%", "−1.23%"; empty without a change to show.</summary>
    private static string Change(double? change) => change is { } value ? Format.SignedFixed(value * 100, 2) + "%" : "";

    /// <summary>Above 0, below 0, or 0 as printed, so a change that rounds to nothing stays plain.</summary>
    private static int SignOf(double? change) => change is { } value ? Math.Sign(Math.Round(value * 10000)) : 0;

    public string ItemId => row.Item.ItemId;
    public string Name => row.Item.ItemName;
    public Color ShopColor => Palette.ShopColor(row.Item.Category);
    public string TierText => $"T{row.Item.Tier}";
    public Color TierColor => Palette.TierColor(row.Item.Tier);
    public string CostText => row.Item.Cost > 0 ? Format.Thousands(row.Item.Cost) : "";

    public string WinRateText => Percent(row.WinRate);
    public bool AboveAverage => row.WinRate - average >= AverageMargin;
    public bool BelowAverage => row.WinRate - average <= -AverageMargin;

    /// <summary>Green or red behind the win rate, stronger the further it is from the average; clear for an average one.</summary>
    public IBrush WinRateFill
    {
        get
        {
            if (!AboveAverage && !BelowAverage)
                return Brushes.Transparent;
            var strength = Math.Min(1, Math.Abs(row.WinRate - average) / FullTintAt);
            return new ImmutableSolidColorBrush(Palette.WithAlpha(AboveAverage ? Palette.Positive : Palette.Negative, (byte)Math.Round(36 + 84 * strength)));
        }
    }

    /// <summary>"+1.8 points on the hero's 50.06% average".</summary>
    public string WinRateTip =>
        $"{Format.SignedFixed((row.WinRate - average) * 100, 1)} points on the hero's {Percent(average)} average win rate with these filters";

    public string WinRateChangeText => Change(row.WinRateChange);
    public bool WinRateRose => SignOf(row.WinRateChange) > 0;
    public bool WinRateFell => SignOf(row.WinRateChange) < 0;

    public string UsageText => Percent(row.Usage);
    public double Usage => row.Usage;
    public string UsageChangeText => Change(row.UsageChange);
    public bool UsageRose => SignOf(row.UsageChange) > 0;
    public bool UsageFell => SignOf(row.UsageChange) < 0;

    /// <summary>"11k / 10k", with the exact counts in <see cref="WinLossTip"/>.</summary>
    public string WinLossText => $"{Format.Compact((int)row.Wins)} / {Format.Compact((int)row.Losses)}";
    public string WinLossTip => $"{Format.Thousands(row.Wins)} won, {Format.Thousands(row.Losses)} lost";
}
