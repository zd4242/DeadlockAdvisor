using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Models;

/// <summary>
/// Who's in the current match: which heroes are allies, enemies or you, and which are flagged as
/// in your lane. Roster sizes (5 allies + you, 6 enemies) are advisory: the UI shows "4/6" counts
/// and blocks nothing, so an unusual or partly known match still works.
/// </summary>
public sealed class MatchState
{
    public const int MaxAllies = 5;
    public const int MaxEnemies = 6;

    // Insertion-ordered, like the Python dicts: the ally and enemy lists come out in this order.
    public OrderedDictionary<string, Role> RoleMap { get; } = [];
    public OrderedDictionary<string, bool> LaneFlags { get; } = [];

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
                LaneFlags.Remove(other);
            }
        }

        if (role == Role.None)
        {
            RoleMap.Remove(heroId);
            LaneFlags.Remove(heroId);
            return;
        }

        RoleMap[heroId] = role;
        if (role == Role.Self)
            LaneFlags.Remove(heroId);
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

    /// <summary>
    /// Only allies and enemies can be flagged in-lane. You don't need flagging: you're always
    /// counted in your own lane.
    /// </summary>
    public void SetLane(string heroId, bool inLane)
    {
        if (RoleOf(heroId) is Role.Ally or Role.Enemy)
            LaneFlags[heroId] = inLane;
        else
            LaneFlags.Remove(heroId);
    }

    public bool ToggleLane(string heroId)
    {
        SetLane(heroId, !IsInLane(heroId));
        return IsInLane(heroId);
    }

    public bool IsInLane(string heroId) => LaneFlags.GetValueOrDefault(heroId);

    public void Clear()
    {
        RoleMap.Clear();
        LaneFlags.Clear();
    }

    public void ClearLane() => LaneFlags.Clear();

    /// <summary>
    /// Replace the match with a random full one drawn from <paramref name="heroIds"/>: you, the
    /// allies and the enemies, with one ally and two enemies in your lane. Fills as many slots as
    /// there are heroes for.
    /// </summary>
    public void Randomize(IEnumerable<string> heroIds, Random random)
    {
        Clear();
        var shuffled = heroIds.ToArray();
        random.Shuffle(shuffled);

        var allies = shuffled.Skip(1).Take(MaxAllies).ToList();
        var enemies = shuffled.Skip(1 + MaxAllies).Take(MaxEnemies).ToList();

        foreach (var self in shuffled.Take(1))
            SetRole(self, Role.Self);
        foreach (var ally in allies)
            SetRole(ally, Role.Ally);
        foreach (var enemy in enemies)
            SetRole(enemy, Role.Enemy);
        foreach (var heroId in allies.Take(1).Concat(enemies.Take(2)))
            SetLane(heroId, true);
    }

    public List<string> Allies => HeroesWith(Role.Ally);
    public List<string> Enemies => HeroesWith(Role.Enemy);

    public string? SelfHero => RoleMap.FirstOrDefault(entry => entry.Value == Role.Self).Key;

    public bool IsEmpty => RoleMap.Count == 0;

    /// <summary>
    /// Everyone relevant to the lane-phase view: yourself (implicit) plus whichever allies and
    /// enemies are flagged as laning with you, typically 1 ally + 2 enemies.
    /// </summary>
    public List<string> LaneHeroes
    {
        get
        {
            var heroes = LaneFlags.Where(entry => entry.Value).Select(entry => entry.Key).ToList();
            if (SelfHero is { } self && !heroes.Contains(self))
                heroes.Add(self);
            return heroes;
        }
    }

    private List<string> HeroesWith(Role role) =>
        RoleMap.Where(entry => entry.Value == role).Select(entry => entry.Key).ToList();

    // -- saving, so the app can reopen on the match you left -------------------

    public SavedMatch ToSaved()
    {
        var saved = new SavedMatch();
        foreach (var (heroId, role) in RoleMap)
            saved.Roles[heroId] = role.Key();
        saved.Lane = LaneFlags.Where(entry => entry.Value).Select(entry => entry.Key).ToList();
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
        foreach (var heroId in saved.Lane)
        {
            if (RoleMap.ContainsKey(heroId))
                SetLane(heroId, true);
        }
    }
}
