using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Vision;

/// <summary>Writes a detection into the match.</summary>
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
    /// Write the roster in, in place of whatever was there, returning how many heroes were placed.
    /// Heroes missing from heroes.csv are dropped rather than invented, as loading a saved match
    /// drops them. Without a self slot the heroes are placed in their slots with no team
    /// (<see cref="MatchState.Unsided"/>) until one of them is picked as you. The net worth history and the
    /// focus survive a detection of the same twelve heroes, so detecting again mid-match adds to the one and
    /// keeps the other rather than starting over.
    /// </summary>
    /// <param name="netWorth">Souls per slot read off the same capture, and when it was taken.</param>
    /// <param name="likelySelfSlot">With no <paramref name="selfSlot"/>, the slot that might be you, to point out on the match bar.</param>
    public static int ApplyToMatch(MatchState match, IReadOnlyList<string?> slotHeroes, int? selfSlot,
        IEnumerable<string>? validHeroIds = null, (IReadOnlyList<int?> Souls, DateTimeOffset At)? netWorth = null,
        int? likelySelfSlot = null)
    {
        var valid = validHeroIds?.ToHashSet();
        var heroes = slotHeroes
            .Select(hero => !string.IsNullOrEmpty(hero) && (valid is null || valid.Contains(hero)) ? hero : null)
            .ToList();

        var previous = match.RoleMap.Where(entry => entry.Value != Role.None || match.Slots.ContainsKey(entry.Key)).Select(entry => entry.Key).ToHashSet();
        var history = match.NetWorth.Snapshots.ToList();
        var focused = match.Focused.ToList();
        match.Clear();

        var roles = RolesFor(heroes, selfSlot);
        if (roles.Count > 0)
        {
            foreach (var (heroId, role) in roles)
                match.SetRole(heroId, role);
            for (var slot = 0; slot < heroes.Count; slot++)
            {
                if (heroes[slot] is { } heroId)
                    match.Slots[heroId] = slot;
            }
        }
        else
        {
            for (var slot = 0; slot < heroes.Count; slot++)
            {
                if (heroes[slot] is { } heroId)
                    match.PlaceUnsided(heroId, slot);
            }
            if (likelySelfSlot is { } likely && likely >= 0 && likely < heroes.Count)
                match.SuggestSelf(heroes[likely]);
        }

        var placed = match.RoleMap.Keys.ToHashSet();
        if (placed.Count > 0 && previous.SetEquals(placed))
        {
            foreach (var snapshot in history)
                match.NetWorth.Add(snapshot);
            foreach (var heroId in focused)
                match.SetFocus(heroId, true);
        }
        if (netWorth is { } reading)
            match.NetWorth.Add(SnapshotFor(match, reading.Souls, reading.At));
        return placed.Count;
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
