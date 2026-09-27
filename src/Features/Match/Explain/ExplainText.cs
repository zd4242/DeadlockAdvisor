using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Scoring;
using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Features.Match.Explain;

/// <summary>The explain panel's wording, from the Python app's results_view.py.</summary>
public static class ExplainText
{
    public static string RelationWord(Relation relation) => relation switch
    {
        Relation.Against => "enemy",
        Relation.With => "ally",
        _ => "you",
    };

    /// <summary>How the data numbers name each relation: "enemies" for the counters, "you" for your hero.</summary>
    public static string DataWord(string relation) => relation == "against" ? "enemies" : "you";

    /// <summary>
    /// Where a coefficient came from, when it isn't just the typed number. The multiplied-out value is
    /// already beside it, so a lone stat just names itself: "from Spirit Resist 30%", otherwise
    /// "(2 typed + 3 from Spirit Resist 30%) × 0.8 trait weight".
    /// </summary>
    public static string? CoefficientSource(TraitPart part)
    {
        if (part.StatParts.Count == 0 && part.Weight == 1)
            return null;
        if (part.Coefficient == 0 && part.StatParts.Count == 1 && part.Weight == 1)
            return $"from {part.StatParts[0].Short()}";

        var pieces = new List<string>();
        if (part.Coefficient != 0)
            pieces.Add($"{Format.Num(part.Coefficient)} typed");
        pieces.AddRange(part.StatParts.Select(stat => $"{Format.Num(NumberFormat.Round(stat.Amount, 3))} from {stat.Short()}"));
        var text = string.Join(" + ", pieces);
        if (part.Weight != 1)
            text = (pieces.Count > 1 ? $"({text})" : text) + $" × {Format.Num(part.Weight)} trait weight";
        return text;
    }

    /// <summary>The full derivation, stat by stat, for hovering the short source.</summary>
    public static string CoefficientTooltip(TraitPart part)
    {
        var lines = new List<string>();
        if (part.Coefficient != 0)
            lines.Add($"{Format.Num(part.Coefficient)} typed");
        lines.AddRange(part.StatParts.Select(stat => stat.Describe()));
        if (part.Weight != 1)
            lines.Add($"× {Format.Num(part.Weight)} trait weight");
        lines.Add($"= coefficient {Format.Num(NumberFormat.Round(part.EffectiveCoefficient, 3))}");
        return string.Join("\n", lines);
    }

    /// <summary>"(80 − 61 avg) × 3": how far the hero sits from the roster's average, times the effective coefficient.</summary>
    public static string Arithmetic(TraitPart part) =>
        $"{Deviation(part)} × {Format.Num(NumberFormat.Round(part.EffectiveCoefficient, 2))}";

    /// <summary>"(80 − 61 avg)", or just "80" when the roster averages 0 on the trait.</summary>
    public static string Deviation(TraitPart part)
    {
        if (part.Baseline == 0)
            return Format.Num(part.HeroScore);
        var baseline = NumberFormat.Round(part.Baseline, 1);
        var sign = baseline < 0 ? "+" : "−";
        return $"({Format.Num(part.HeroScore)} {sign} {Format.Num(Math.Abs(baseline))} avg)";
    }

    public static string Share(TraitPart part) => Format.Signed(NumberFormat.Round(part.Amount, 2));

    /// <summary>"×1.18 · 25k vs 19k avg": how much the hero's net worth scaled their share, or null when it didn't.</summary>
    public static string? NetWorth(NetWorthStanding? standing) =>
        standing is { Factor: not 1.0 }
            ? $"×{NumberFormat.Fixed(standing.Factor, 2)} · {Format.Compact(standing.Souls)} vs {Format.Compact((int)Math.Round(standing.Average))} avg"
            : null;

    public static string NetWorthTooltip(NetWorthStanding standing) =>
        $"{Format.Compact(standing.Souls)} souls against the match's average of {Format.Compact((int)Math.Round(standing.Average))},\n"
        + $"so this hero's share counts ×{NumberFormat.Fixed(standing.Factor, 2)}.\n"
        + $"Net worth scales a hero's share by ×{Format.Num(1 - NetWorthWeights.MaxShift)} to ×{Format.Num(1 + NetWorthWeights.MaxShift)}.";
}
