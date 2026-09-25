using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Models;

/// <summary>One stat's share of a stat-derived coefficient.</summary>
/// <param name="Factor">1, or the rule's conditional factor for a conditional stat.</param>
public sealed record StatPart(
    string Stat,
    string Label,
    string Unit,
    double Value,
    double PerUnit,
    double Factor,
    bool Conditional)
{
    public double Amount => Value * PerUnit * Factor;

    /// <summary>"Spirit Resist 15% (conditional)"</summary>
    public string Short()
    {
        var value = NumberFormat.Python(Value);
        var amount = Unit == "%" ? value + Unit : $"{value} {Unit}".Trim();
        return $"{Label} {amount}" + (Conditional ? " (conditional)" : "");
    }

    /// <summary>"Spirit Resist 15% (conditional) × 0.1 × 0.5 = 0.75"</summary>
    public string Describe()
    {
        var text = $"{Short()} × {NumberFormat.Python(PerUnit)}";
        if (Factor != 1)
            text += $" × {NumberFormat.Python(Factor)}";
        return $"{text} = {NumberFormat.Python(NumberFormat.Round(Amount, 3))}";
    }
}
