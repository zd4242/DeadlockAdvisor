using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Models;

/// <summary>
/// Who is in the match, on which team and slot, and who is focused: what undo steps between. The net worth
/// readings are left out, so a scoreboard read that lands between two picks doesn't count as a change or get rolled back.
/// </summary>
public sealed class MatchPicks : IEquatable<MatchPicks>
{
    public MatchPicks(IEnumerable<KeyValuePair<string, Role>> roles, IEnumerable<KeyValuePair<string, int>> slots, IEnumerable<string> focused,
        string? likelyYou)
    {
        Roles = roles.ToList();
        Slots = slots.ToDictionary();
        Focused = focused.ToHashSet();
        LikelyYou = likelyYou;
    }

    /// <summary>In pick order, which is the order each team comes out in.</summary>
    public IReadOnlyList<KeyValuePair<string, Role>> Roles { get; }

    public IReadOnlyDictionary<string, int> Slots { get; }
    public IReadOnlySet<string> Focused { get; }
    public string? LikelyYou { get; }

    public bool Equals(MatchPicks? other) =>
        other is not null
        && LikelyYou == other.LikelyYou
        && Roles.SequenceEqual(other.Roles)
        && Slots.Count == other.Slots.Count && Slots.All(slot => other.Slots.TryGetValue(slot.Key, out var at) && at == slot.Value)
        && Focused.SetEquals(other.Focused);

    public override bool Equals(object? obj) => Equals(obj as MatchPicks);

    public override int GetHashCode() => HashCode.Combine(Roles.Count, Slots.Count, Focused.Count, LikelyYou);
}
