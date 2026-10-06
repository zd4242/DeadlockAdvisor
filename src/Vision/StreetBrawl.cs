namespace DeadlockAdvisor.Vision;

/// <summary>
/// Street Brawl is 4v4 on the same twelve-slot strip: each team's two outermost slots stay blank. A
/// blank slot is a flat grey panel, where a dead or faded hero still shows a portrait, so the strip
/// is only a Street Brawl when those blanks are plainly panels and the eight heroes inside them all
/// read confidently.
/// </summary>
public static class StreetBrawl
{
    /// <summary>
    /// Flatter than this (<see cref="ImageOps.Contrast"/>) is a blank panel. On the labelled Street
    /// Brawl captures the blanks read at 0.02 or less, against 0.04 for the faintest faded portrait
    /// and 0.05 for the faintest dead one.
    /// </summary>
    public const double MaxBlankContrast = 0.03;

    /// <summary>The outermost two slots of each team: 0 and 1 on the left, 10 and 11 on the right.</summary>
    public static readonly IReadOnlyList<int> OuterSlots = [0, 1, Layout.SlotCount - 2, Layout.SlotCount - 1];

    /// <summary>
    /// The blank slots of a Street Brawl strip, or none when the strip isn't one: the outer two of
    /// each team read as blank panels with no hero, and the four inside each team all read confidently.
    /// </summary>
    public static IReadOnlyList<int> BlankSlots(IReadOnlyList<SlotReading> slots, Func<int, RgbImage?> cropOf)
    {
        if (slots.Count != Layout.SlotCount)
            return [];
        var inner = Enumerable.Range(0, Layout.SlotCount).Except(OuterSlots);
        if (!inner.All(slot => slots[slot].IsConfident))
            return [];
        return OuterSlots.All(slot => IsBlank(slots[slot], cropOf(slot))) ? OuterSlots : [];
    }

    private static bool IsBlank(SlotReading reading, RgbImage? crop) =>
        reading.HeroId is null && crop is not null && ImageOps.Contrast(crop) < MaxBlankContrast;
}
