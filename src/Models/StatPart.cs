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

    /// <summary>"15%", or "15 m" for a stat in other units.</summary>
    public string ValueText
    {
        get
        {
            var value = NumberFormat.Short(Value);
            return Unit == "%" ? value + Unit : $"{value} {Unit}".Trim();
        }
    }

    /// <summary>"Spirit Resist 15% (conditional)"</summary>
    public string Short() => $"{Label} {ValueText}" + (Conditional ? " (conditional)" : "");

    /// <summary>"Spirit Resist 15% (conditional) × 0.1 × 0.5 = 0.75"</summary>
    public string Describe()
    {
        var text = $"{Short()} × {NumberFormat.Short(PerUnit)}";
        if (Factor != 1)
            text += $" × {NumberFormat.Short(Factor)}";
        return $"{text} = {NumberFormat.Short(NumberFormat.Round(Amount, 3))}";
    }
}
