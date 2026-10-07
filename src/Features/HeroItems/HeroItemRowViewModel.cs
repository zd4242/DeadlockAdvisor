using Avalonia.Media;
using Avalonia.Media.Immutable;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;
using DeadlockAdvisor.Theme;

namespace DeadlockAdvisor.Features.HeroItems;

/// <summary>One item's row in a hero's item table, as printed.</summary>
/// <param name="average">The hero's own win rate over the same matches, which each row's is read against.</param>
/// <param name="heroName">The hero the table is for, named in the fit's tip.</param>
public class HeroItemRowViewModel(HeroItemRow row, double average, string heroName = "The hero")
{
    /// <summary>A win rate within this of the average, as a fraction, reads as average.</summary>
    public const double AverageMargin = 0.005;

    /// <summary>A fit within this many points of 0 reads as typical.</summary>
    public const double FitMargin = 0.5;

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

    /// <summary>"+1.8", "−0.4": the win rate's distance from the average, in points, beside it.</summary>
    public string WinRateGapText => Format.SignedFixed((row.WinRate - average) * 100, 1);
    public bool AboveAverage => row.WinRate - average >= AverageMargin;
    public bool BelowAverage => row.WinRate - average <= -AverageMargin;

    /// <summary>Green or red behind the win rate, stronger the further it is from the average; clear for an average one.</summary>
    public IBrush WinRateFill =>
        AboveAverage || BelowAverage
            ? new ImmutableSolidColorBrush(Palette.WithAlpha(TintColor, (byte)Math.Round(10 + 110 * TintStrength)))
            : Brushes.Transparent;

    /// <summary>
    /// The win rate's colour. It leaves the plain text colour's for the tint's own as soon as it's tinted, then goes
    /// back towards it as the tint behind it strengthens, so it stays readable on the strongest.
    /// </summary>
    public IBrush WinRateForeground =>
        new ImmutableSolidColorBrush(AboveAverage || BelowAverage
            ? Palette.Mix(Palette.Mix(Palette.Text, TintColor, 0.7), Palette.Text, 0.9 * TintStrength)
            : Palette.Text);

    private Color TintColor => AboveAverage ? Palette.Positive : Palette.Negative;

    /// <summary>0 at the margin from the average, to 1 at <see cref="FullTintAt"/> from it and beyond.</summary>
    private double TintStrength => Math.Clamp((Math.Abs(row.WinRate - average) - AverageMargin) / (FullTintAt - AverageMargin), 0, 1);

    /// <summary>"+1.8 points on the hero's 50.06% average".</summary>
    public string WinRateTip =>
        $"{Format.SignedFixed((row.WinRate - average) * 100, 1)} points on the hero's {Percent(average)} average win rate with these filters";

    /// <summary>"+1.8", "−2.0": how much more the hero wins with it than others who build it, next to its other items of the tier; "—" without one.</summary>
    public string FitText => row.Fit is { } fit ? Format.SignedFixed(fit.Shown, 1) : "—";
    public bool FitGood => row.Fit?.Shown >= FitMargin;
    public bool FitBad => row.Fit?.Shown <= -FitMargin;

    /// <summary>The fit's arithmetic with this row's own numbers, or why there's none.</summary>
    public string FitTip
    {
        get
        {
            if (row.Fit is not { } fit)
                return $"Bought in under {Format.Thousands(HeroFits.MinMatches)} of these matches, too few to measure.";
            var tip = $"{heroName} won {Percent(row.WinRate)} with it, and everyone who built it {Percent(fit.EveryoneWinRate)} "
                      + $"({Format.SignedFixed(fit.Raw + fit.TierAverage, 2)}).\n"
                      + $"{heroName}'s tier {row.Item.Tier} items run {Format.SignedFixed(fit.TierAverage, 2)} on average, "
                      + $"so it's {Format.SignedFixed(fit.Raw, 2)} next to them.";
            return Format.SignedFixed(fit.Shown, 1) == Format.SignedFixed(fit.Raw, 1)
                ? tip
                : $"{tip}\nPulled toward 0 for how few matches it rests on: {Format.SignedFixed(fit.Shown, 1)}.";
        }
    }

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
