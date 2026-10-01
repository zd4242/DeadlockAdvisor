using DeadlockAdvisor.Services.Formats;

namespace DeadlockAdvisor.Services.GameApi;

/// <summary>
/// The top of a shop's investment curve: spending <see cref="Souls"/> in the shop gives <see cref="Bonus"/>.
/// The curve has steps (a big one at 4,800 souls), but which step an item tips you over depends on the
/// build, so an item is credited the curve's average rate.
/// </summary>
public sealed record ShopBonus(double Bonus, double Souls)
{
    /// <summary>What an item costing <paramref name="cost"/> souls adds to the bonus, on average.</summary>
    public double Share(double cost) => NumberFormat.Round(cost * Bonus / Souls, 1);
}
