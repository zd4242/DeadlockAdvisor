using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Core;

/// <summary>A score or share as the results show it: printed to a tenth, with the fuller value for hovering.</summary>
public readonly record struct DisplayAmount(double Value)
{
    /// <summary>The value as printed, for its ▲/▼, so one that rounds to 0.0 gets no triangle.</summary>
    public double Shown => NumberFormat.Round(Value, 1);

    /// <summary>The printed size, without its sign.</summary>
    public string Text => Format.Tenths(Value);

    public string Tip => Format.Num(Value);
}
