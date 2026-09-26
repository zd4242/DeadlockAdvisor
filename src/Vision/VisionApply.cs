using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Vision;

/// <summary>
/// Writes a detection into the match. Order matters: <see cref="MatchState.SetLane"/> ignores a hero
/// with no role yet, so every role is assigned before any lane flag, and making someone You clears
/// their lane flag, since you're counted in your own lane already.
/// </summary>
public static class VisionApply
{
    /// <summary>
    /// Slot → hero plus which slot is you, as hero → role. With no self slot there's no telling the
    /// teams apart, so everyone is left unassigned rather than split down the middle at random.
    /// </summary>
    public static OrderedDictionary<string, Role> RolesFor(IReadOnlyList<string?> slotHeroes, int? selfSlot)
    {
        var roles = new OrderedDictionary<string, Role>();
        if (selfSlot is not { } self || self is < 0 or >= Layout.SlotCount)
            return roles;
        var ownBase = self < Layout.PerTeam ? 0 : Layout.PerTeam;
        for (var index = 0; index < slotHeroes.Count; index++)
        {
            if (string.IsNullOrEmpty(slotHeroes[index]))
                continue;
            roles[slotHeroes[index]!] = index == self ? Role.Self
                : index >= ownBase && index < ownBase + Layout.PerTeam ? Role.Ally
                : Role.Enemy;
        }
        return roles;
    }

    /// <summary>
    /// Your lane partner and the two enemies opposite: each team is three pairs, and the pairs face
    /// each other in order. You're left out, since the match counts you in your own lane implicitly.
    /// </summary>
    public static List<string> LaneHeroesFor(IReadOnlyList<string?> slotHeroes, int? selfSlot)
    {
        if (selfSlot is not { } self || self is < 0 or >= Layout.SlotCount)
            return [];
        var ownBase = self < Layout.PerTeam ? 0 : Layout.PerTeam;
        var foeBase = Layout.PerTeam - ownBase;
        var local = self - ownBase;
        var pair = local / 2;
        int[] wanted = [ownBase + pair * 2 + (1 - local % 2), foeBase + pair * 2, foeBase + pair * 2 + 1];
        return wanted.Where(i => i < slotHeroes.Count && !string.IsNullOrEmpty(slotHeroes[i]))
            .Select(i => slotHeroes[i]!)
            .ToList();
    }

    /// <summary>
    /// Write the roster in, returning how many heroes were assigned. Heroes missing from heroes.csv
    /// are dropped rather than invented, as loading a saved match drops them.
    /// </summary>
    public static int ApplyToMatch(MatchState match, IReadOnlyList<string?> slotHeroes, int? selfSlot,
        IEnumerable<string>? validHeroIds = null, bool clearFirst = true)
    {
        var valid = validHeroIds?.ToHashSet();
        var heroes = slotHeroes
            .Select(hero => !string.IsNullOrEmpty(hero) && (valid is null || valid.Contains(hero)) ? hero : null)
            .ToList();

        if (clearFirst)
            match.Clear();

        var roles = RolesFor(heroes, selfSlot);
        foreach (var (heroId, role) in roles)
            match.SetRole(heroId, role);
        foreach (var heroId in LaneHeroesFor(heroes, selfSlot))
            match.SetLane(heroId, true);
        return roles.Count;
    }
}
