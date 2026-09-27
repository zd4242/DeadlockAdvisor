namespace DeadlockAdvisor.Vision;

/// <summary>
/// What the top bar says each hero is worth: the pill under every portrait and each team's total
/// beside the clock, in souls.
/// </summary>
/// <param name="Pills">Souls per slot, left to right, or null where a pill couldn't be read.</param>
/// <param name="Totals">The left and right teams' totals, or null where unread.</param>
public sealed record NetWorthReading(IReadOnlyList<int?> Pills, IReadOnlyList<int?> Totals)
{
    public static NetWorthReading Empty { get; } = new(new int?[Layout.SlotCount], [null, null]);

    /// <summary>
    /// Whether every pill on one side was read and they add up to its total, given how the game
    /// rounds them: a pill to the nearest thousand from 10k (±500), to the nearest hundred below that
    /// (±50) and exactly under 1k, and a total down to the thousand (3,600 shows as "3k").
    /// </summary>
    public bool Agrees(int side) => Check(side) is { Unread: 0, Fits: true };

    /// <summary>
    /// Whether the pills read on one side can be trusted: all of them adding up to the total, or,
    /// with some hidden (an overlay, a damage number), those read coming to no more than it. A side
    /// read in full that doesn't add up has a misread somewhere, and nothing says which pill.
    /// </summary>
    public bool Plausible(int side) => Check(side) is { Fits: true };

    /// <summary>Souls per slot, only for the plausible sides: a misread digit is worse than no reading.</summary>
    public IReadOnlyList<int?> Souls
    {
        get
        {
            var plausible = new[] { Plausible(0), Plausible(1) };
            return Pills.Select((souls, slot) => plausible[slot / Layout.PerTeam] ? souls : null).ToList();
        }
    }

    public int ReadCount => Souls.Count(souls => souls is not null);

    /// <summary>
    /// How many of a side's pills are unread, and whether those read fit its total: never more than
    /// it, and, with none unread, no less either. Nothing fits an unread total.
    /// </summary>
    private (int Unread, bool Fits) Check(int side)
    {
        int low = 0, high = 0, unread = 0;
        for (var slot = side * Layout.PerTeam; slot < (side + 1) * Layout.PerTeam; slot++)
        {
            if (Pills[slot] is not { } souls)
            {
                unread++;
                continue;
            }
            var slack = souls >= 10_000 ? 500 : souls >= 1_000 ? 50 : 0;
            low += souls - slack;
            high += souls + slack;
        }
        var fits = Totals[side] is { } total && low < total + 1_000 && (unread > 0 || high >= total);
        return (unread, fits);
    }
}
