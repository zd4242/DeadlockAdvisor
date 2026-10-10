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

    // Insertion-ordered: each team comes out in this order, which is the
    // game's top-bar order after a detection and pick order otherwise.
    public OrderedDictionary<string, Role> RoleMap { get; } = [];

    /// <summary>Hero → their slot on the game's top bar (0-11, left to right), for heroes placed by a detection.</summary>
    public OrderedDictionary<string, int> Slots { get; } = [];

    public NetWorthHistory NetWorth { get; } = new();

    private readonly HashSet<string> _focused = [];

    /// <summary>The enemies the recommendations lean toward; a hero who stops being an enemy drops out.</summary>
    public IReadOnlySet<string> Focused => _focused;

    public Role RoleOf(string heroId) => RoleMap.GetValueOrDefault(heroId, Role.None);

    /// <summary>Focus on an enemy, or stop; anyone not an enemy can't be focused. Returns whether they're focused now.</summary>
    public bool SetFocus(string heroId, bool focused)
    {
        if (focused && RoleOf(heroId) == Role.Enemy)
            _focused.Add(heroId);
        else
            _focused.Remove(heroId);
        return _focused.Contains(heroId);
    }

    public bool ToggleFocus(string heroId) => SetFocus(heroId, !_focused.Contains(heroId));

    public void ClearFocus() => _focused.Clear();

    /// <summary>
    /// Only one hero can be you at a time. A hero already in the match who becomes you keeps everyone
    /// in it: a teammate trades roles with the previous you, and an enemy brings their side with them,
    /// so the teams trade places. A hero from outside the match clears Self from the previous you.
    /// </summary>
    public void SetRole(string heroId, Role role)
    {
        if (role != Role.None && HasUnsided)
        {
            if (IsUnsided(heroId))
            {
                TakeSides(heroId, role);
                return;
            }
            // A hero from outside the read: the match is being built by hand now.
            DropUnsided();
        }

        if (role == Role.Self && SelfHero is { } formerSelf && formerSelf != heroId)
        {
            switch (RoleOf(heroId))
            {
                case Role.Ally:
                    RoleMap[formerSelf] = Role.Ally;
                    break;
                case Role.Enemy:
                    SwapSides();
                    break;
                default:
                    // The previous "you" stays in the map, unassigned, rather than being removed,
                    // so saved matches keep their order.
                    RoleMap[formerSelf] = Role.None;
                    Slots.Remove(formerSelf);
                    break;
            }
        }

        if (role != Role.Enemy)
            _focused.Remove(heroId);
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
    /// Heroes read off the top bar whose team isn't known yet, left side first. They sit in <see cref="RoleMap"/>
    /// unassigned, with their slot: a detection that found every hero but not which one is you. The
    /// first role given to any of them splits the two sides (<see cref="TakeSides"/>).
    /// </summary>
    public List<string> Unsided => RoleMap
        .Where(entry => entry.Value == Role.None && Slots.ContainsKey(entry.Key))
        .Select(entry => entry.Key)
        .OrderBy(heroId => Slots[heroId])
        .ToList();

    public bool HasUnsided => RoleMap.Any(entry => entry.Value == Role.None && Slots.ContainsKey(entry.Key));

    private string? _likelyYou;

    /// <summary>Which unsided hero looked like you (a kill streak's backplate): pointed out for a click, never assumed.</summary>
    public string? LikelyYou => _likelyYou is { } heroId && IsUnsided(heroId) ? heroId : null;

    public void SuggestSelf(string? heroId) => _likelyYou = heroId;

    public bool IsUnsided(string heroId) => RoleMap.TryGetValue(heroId, out var role) && role == Role.None && Slots.ContainsKey(heroId);

    /// <summary>Put a hero read in a top-bar slot into the match without a team.</summary>
    public void PlaceUnsided(string heroId, int slot)
    {
        RoleMap[heroId] = Role.None;
        Slots[heroId] = slot;
    }

    /// <summary>
    /// A hero on one side of the bar takes <paramref name="role"/>: their side becomes that team and the other side
    /// the opposite one, so clicking yourself splits a match whose heroes are all known but not whose they are.
    /// </summary>
    private void TakeSides(string heroId, Role role)
    {
        var team = role.Team();
        var side = Slots[heroId] / TeamSize;
        foreach (var other in Unsided)
        {
            var mine = Slots[other] / TeamSize == side;
            RoleMap[other] = mine ? team : team == Role.Ally ? Role.Enemy : Role.Ally;
        }
        RoleMap[heroId] = role;
    }

    private void DropUnsided()
    {
        foreach (var heroId in Unsided)
        {
            RoleMap.Remove(heroId);
            Slots.Remove(heroId);
        }
    }

    /// <summary>
    /// Allies (you included) become enemies and enemies allies, each team keeping its order and top-bar slots.
    /// The focused enemies are allies now, so nobody is focused.
    /// </summary>
    private void SwapSides()
    {
        _focused.Clear();
        foreach (var (heroId, role) in RoleMap.ToList())
        {
            if (role != Role.None)
                RoleMap[heroId] = role.Team() == Role.Ally ? Role.Enemy : Role.Ally;
        }
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
        _focused.Clear();
        _likelyYou = null;
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
        saved.Focused = Enemies.Where(_focused.Contains).ToList();
        saved.LikelyYou = LikelyYou;
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
            if (RoleMap.ContainsKey(heroId))
                Slots[heroId] = slot;
        }
        foreach (var snapshot in saved.NetWorth)
            NetWorth.Add(new NetWorthSnapshot(snapshot.At, snapshot.Souls.Where(entry => valid.Contains(entry.Key)).ToDictionary()));
        foreach (var heroId in saved.Focused)
            SetFocus(heroId, true);
        SuggestSelf(saved.LikelyYou);
    }
}
