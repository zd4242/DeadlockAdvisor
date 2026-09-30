namespace DeadlockAdvisor.Vision;

/// <summary>
/// Heroes don't change slots during a match, so a detection of the match already applied can lean on
/// it: a slot the read couldn't settle (a dead player, a portrait faded out of sight) keeps the hero
/// applied there, and you stay you when your backplate can't be read (on a kill streak it turns
/// teal). Only when the read is plainly the same match: enough slots agree, and none disagrees
/// confidently, since that would mean a new match that happens to share some heroes.
/// </summary>
public static class RosterContinuity
{
    /// <summary>Confident slots that must agree with the applied roster before it's leaned on.</summary>
    public const int MinAgreeing = 6;

    /// <param name="applied">The hero applied to each slot, by slot.</param>
    /// <param name="appliedSelf">The slot applied as you, if any.</param>
    public static Detection Apply(Detection detection, IReadOnlyDictionary<int, string> applied, int? appliedSelf)
    {
        if (!IsSameMatch(detection, applied))
            return detection;

        var confidentHeroes = detection.Slots.Where(slot => slot.IsConfident).Select(slot => slot.HeroId!).ToHashSet();
        var slots = detection.Slots.Select(reading =>
        {
            if (reading.IsConfident || !applied.TryGetValue(reading.Index, out var hero) || confidentHeroes.Contains(hero))
                return reading;
            return reading with { HeroId = hero, Kept = true };
        }).ToList();
        return detection with { Slots = slots, SelfSlot = detection.SelfSlot ?? appliedSelf };
    }

    public static bool IsSameMatch(Detection detection, IReadOnlyDictionary<int, string> applied)
    {
        var confident = detection.Slots.Where(slot => slot.IsConfident && applied.ContainsKey(slot.Index)).ToList();
        return confident.Count(slot => applied[slot.Index] == slot.HeroId) >= MinAgreeing
               && confident.All(slot => applied[slot.Index] == slot.HeroId);
    }
}
