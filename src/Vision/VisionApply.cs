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
    /// Write the roster in, in place of whatever was there, returning how many heroes were assigned.
    /// Heroes missing from heroes.csv are dropped rather than invented, as loading a saved match
    /// drops them. The net worth history survives a detection of the same twelve heroes, so
    /// detecting again mid-match adds to it rather than starting over.
    /// </summary>
    /// <param name="laneSlots">
    /// The lane as the review showed it (off the game's highlights when it could read them), so what
    /// lands is what was checked; without it the lane comes from the layout's pairing.
    /// </param>
    /// <param name="netWorth">Souls per slot read off the same capture, and when it was taken.</param>
    public static int ApplyToMatch(MatchState match, IReadOnlyList<string?> slotHeroes, int? selfSlot,
        IEnumerable<string>? validHeroIds = null, IReadOnlyList<int>? laneSlots = null,
        (IReadOnlyList<int?> Souls, DateTimeOffset At)? netWorth = null)
    {
        var valid = validHeroIds?.ToHashSet();
        var heroes = slotHeroes
            .Select(hero => !string.IsNullOrEmpty(hero) && (valid is null || valid.Contains(hero)) ? hero : null)
            .ToList();

        var previous = match.RoleMap.Where(entry => entry.Value != Role.None).Select(entry => entry.Key).ToHashSet();
        var history = match.NetWorth.Snapshots.ToList();
        match.Clear();

        var roles = RolesFor(heroes, selfSlot);
        foreach (var (heroId, role) in roles)
            match.SetRole(heroId, role);
        for (var slot = 0; slot < heroes.Count; slot++)
        {
            if (heroes[slot] is { } heroId && match.RoleOf(heroId) != Role.None)
                match.Slots[heroId] = slot;
        }
        var lane = laneSlots is null
            ? LaneHeroesFor(heroes, selfSlot)
            : laneSlots.Where(i => i >= 0 && i < heroes.Count && heroes[i] is not null).Select(i => heroes[i]!);
        foreach (var heroId in lane)
            match.SetLane(heroId, true);

        if (roles.Count > 0 && previous.SetEquals(roles.Keys))
        {
            foreach (var snapshot in history)
                match.NetWorth.Add(snapshot);
        }
        if (netWorth is { } reading)
            match.NetWorth.Add(SnapshotFor(match, reading.Souls, reading.At));
        return roles.Count;
    }

    /// <summary>Souls per top-bar slot, as souls per hero in the match.</summary>
    public static NetWorthSnapshot SnapshotFor(MatchState match, IReadOnlyList<int?> slotSouls, DateTimeOffset at)
    {
        var souls = new Dictionary<string, int>();
        for (var slot = 0; slot < slotSouls.Count; slot++)
        {
            if (slotSouls[slot] is { } value && match.HeroInSlot(slot) is { } heroId)
                souls[heroId] = value;
        }
        return new NetWorthSnapshot(at, souls);
    }
}
