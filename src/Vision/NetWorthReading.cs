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
    /// Whether one side's pills can add up to its total, given how the game rounds them: a pill to
    /// the nearest thousand from 10k (±500), to the nearest hundred below that (±50) and exactly
    /// under 1k, and a total down to the thousand (3,600 shows as "3k").
    /// </summary>
    public bool Agrees(int side)
    {
        if (Totals[side] is not { } total)
            return false;
        int low = 0, high = 0;
        for (var slot = side * Layout.PerTeam; slot < (side + 1) * Layout.PerTeam; slot++)
        {
            if (Pills[slot] is not { } souls)
                return false;
            var slack = souls >= 10_000 ? 500 : souls >= 1_000 ? 50 : 0;
            low += souls - slack;
            high += souls + slack;
        }
        return low < total + 1_000 && high >= total;
    }

    /// <summary>Souls per slot, only for the sides whose pills add up: a misread digit is worse than no reading.</summary>
    public IReadOnlyList<int?> Souls
    {
        get
        {
            var agrees = new[] { Agrees(0), Agrees(1) };
            return Pills.Select((souls, slot) => agrees[slot / Layout.PerTeam] ? souls : null).ToList();
        }
    }

    public int ReadCount => Souls.Count(souls => souls is not null);
}
