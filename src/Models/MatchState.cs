using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Models;

/// <summary>
/// Who's in the current match: which heroes are allies, enemies or you. Roster sizes (5 allies + you, 6 enemies) are advisory: the UI shows "4/6" counts
/// and blocks nothing, so an unusual or partly known match still works. A detected match also
/// knows where each hero sits on the game's top bar, which is how net worth read off it finds them.
/// </summary>
public sealed class MatchState
{
    public const int TeamSize = 6;
    public const int MaxAllies = TeamSize - 1;
    public const int MaxEnemies = TeamSize;

    // Insertion-ordered, like the Python dicts: each team comes out in this order, which is the
    // game's top-bar order after a detection and pick order otherwise.
    public OrderedDictionary<string, Role> RoleMap { get; } = [];

    /// <summary>Hero → their slot on the game's top bar (0-11, left to right), for heroes placed by a detection.</summary>
    public OrderedDictionary<string, int> Slots { get; } = [];

    public NetWorthHistory NetWorth { get; } = new();

    public Role RoleOf(string heroId) => RoleMap.GetValueOrDefault(heroId, Role.None);

    /// <summary>Only one hero can be you at a time: assigning Self clears it from whoever had it.</summary>
    public void SetRole(string heroId, Role role)
    {
        if (role == Role.Self)
        {
            foreach (var (other, otherRole) in RoleMap.ToList())
            {
                if (otherRole != Role.Self || other == heroId)
                    continue;
                // The Python app leaves the previous "you" in the map as unassigned rather than
                // removing it; kept for identical saved matches.
                RoleMap[other] = Role.None;
                Slots.Remove(other);
            }
        }

        if (role == Role.None)
        {
            RoleMap.Remove(heroId);
            Slots.Remove(heroId);
            return;
        }

        // Changing sides takes the next free place on the new team, as a fresh pick would, and
        // leaves the top-bar slot the hero was read in.
        if (RoleMap.TryGetValue(heroId, out var previous) && previous.Team() != role.Team())
        {
            RoleMap.Remove(heroId);
            Slots.Remove(heroId);
        }
        RoleMap[heroId] = role;
    }

    /// <summary>
    /// The palette's click-to-assign: clicking a hero who already holds that role clears them.
    /// Returns the role they ended up with.
    /// </summary>
    public Role ToggleRole(string heroId, Role role)
    {
        if (RoleOf(heroId) == role)
        {
            SetRole(heroId, Role.None);
            return Role.None;
        }
        SetRole(heroId, role);
        return role;
    }

    public void Clear()
    {
        RoleMap.Clear();
        Slots.Clear();
        NetWorth.Clear();
    }

    /// <summary>Who a detection placed in a top-bar slot, if anyone still in the match.</summary>
    public string? HeroInSlot(int slot) => Slots.FirstOrDefault(entry => entry.Value == slot).Key;

    /// <summary>
    /// Replace the match with a random full one drawn from <paramref name="heroIds"/>: you, the
    /// allies and the enemies, apart from whoever <paramref name="keep"/> holds on to. Fills as
    /// many slots as there are heroes for.
    /// </summary>
    public void Randomize(IEnumerable<string> heroIds, Random random, RandomizeKeep keep = RandomizeKeep.Nothing)
    {
        var kept = RoleMap.Where(entry => keep switch
        {
            RandomizeKeep.Self => entry.Value == Role.Self,
            RandomizeKeep.OwnTeam => entry.Value.Team() == Role.Ally,
            _ => false,
        }).ToList();

        Clear();
        foreach (var (heroId, role) in kept)
            RoleMap[heroId] = role;

        var shuffled = heroIds.Where(heroId => !RoleMap.ContainsKey(heroId)).ToArray();
        random.Shuffle(shuffled);
        var draw = new Queue<string>(shuffled);

        if (SelfHero is null)
            Draw(draw, Role.Self, 1);
        Draw(draw, Role.Ally, MaxAllies - Allies.Count);
        Draw(draw, Role.Enemy, MaxEnemies - Enemies.Count);
    }

    private void Draw(Queue<string> draw, Role role, int count)
    {
        for (var drawn = 0; drawn < count && draw.TryDequeue(out var heroId); drawn++)
            SetRole(heroId, role);
    }

    public List<string> Allies => HeroesWith(Role.Ally);
    public List<string> Enemies => HeroesWith(Role.Enemy);

    /// <summary>You and your allies, with you in your own place rather than first.</summary>
    public List<string> OwnTeam => RoleMap.Where(entry => entry.Value.Team() == Role.Ally).Select(entry => entry.Key).ToList();

    public string? SelfHero => RoleMap.FirstOrDefault(entry => entry.Value == Role.Self).Key;

    public bool IsEmpty => RoleMap.Count == 0;

    private List<string> HeroesWith(Role role) =>
        RoleMap.Where(entry => entry.Value == role).Select(entry => entry.Key).ToList();

    // -- saving, so the app can reopen on the match you left -------------------

    public SavedMatch ToSaved()
    {
        var saved = new SavedMatch();
        foreach (var (heroId, role) in RoleMap)
            saved.Roles[heroId] = role.Key();
        foreach (var (heroId, slot) in Slots)
            saved.Slots[heroId] = slot;
        saved.NetWorth = NetWorth.Snapshots
            .Select(snapshot => new SavedNetWorth { At = snapshot.At, Souls = snapshot.Souls.ToDictionary() })
            .ToList();
        return saved;
    }

    /// <summary>Restore a saved match, silently dropping heroes that no longer exist.</summary>
    public void LoadSaved(SavedMatch? saved, IEnumerable<string> validHeroIds)
    {
        Clear();
        if (saved is null)
            return;

        var valid = validHeroIds.ToHashSet();
        foreach (var (heroId, value) in saved.Roles)
        {
            if (valid.Contains(heroId) && Roles.TryParse(value, out var role))
                RoleMap[heroId] = role;
        }
        foreach (var (heroId, slot) in saved.Slots)
        {
            if (RoleOf(heroId) != Role.None)
                Slots[heroId] = slot;
        }
        foreach (var snapshot in saved.NetWorth)
            NetWorth.Add(new NetWorthSnapshot(snapshot.At, snapshot.Souls.Where(entry => valid.Contains(entry.Key)).ToDictionary()));
    }
}
