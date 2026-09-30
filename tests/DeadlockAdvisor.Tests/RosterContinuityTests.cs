using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Tests;

public class RosterContinuityTests
{
    private static readonly string[] _roster =
        ["apollo", "ivy", "wraith", "bebop", "silver", "doorman", "celeste", "lash", "venator", "paige", "mirage", "mo_and_krill"];

    private static readonly Dictionary<int, string> _applied = _roster.Select((hero, slot) => (hero, slot)).ToDictionary(pair => pair.slot, pair => pair.hero);

    /// <summary>A read of the roster above, with the given slots unread (a dead player) and any overrides read confidently.</summary>
    private static Detection Read(int? self, IEnumerable<int> unread, Dictionary<int, string>? overrides = null)
    {
        var dead = unread.ToHashSet();
        var slots = _roster.Select((hero, slot) => dead.Contains(slot)
                ? new SlotReading(slot, null, 0.2, 0, null, default, [])
                : new SlotReading(slot, overrides?.GetValueOrDefault(slot) ?? hero, 0.85, 0.4, null, default, []))
            .ToList();
        return new Detection(slots, new Geometry(1280, 116, 17), self, 0, null, []);
    }

    [Fact]
    public void TheSameMatchKeepsWhoItCouldNotRead()
    {
        var kept = RosterContinuity.Apply(Read(self: null, unread: [3, 9]), _applied, appliedSelf: 0);

        Assert.Equal(_roster, kept.Slots.Select(slot => slot.HeroId));
        Assert.Equal([3, 9], kept.Slots.Where(slot => slot.Kept).Select(slot => slot.Index));
        Assert.False(kept.Slots[3].IsConfident);
        Assert.True(kept.Slots[3].IsSettled);
        // You on a kill streak: your backplate isn't a team colour, so you're carried over.
        Assert.Equal(0, kept.SelfSlot);
        Assert.True(kept.IsSettled);
    }

    [Fact]
    public void AReadOfYouIsNotOverridden()
    {
        Assert.Equal(1, RosterContinuity.Apply(Read(self: 1, unread: []), _applied, appliedSelf: 0).SelfSlot);
    }

    [Fact]
    public void ANewMatchIsReadAfresh()
    {
        // One confident slot disagrees: a new match that happens to share heroes.
        var fresh = Read(self: null, unread: [3], new Dictionary<int, string> { [5] = "haze" });

        Assert.Same(fresh, RosterContinuity.Apply(fresh, _applied, appliedSelf: 0));
    }

    [Fact]
    public void TooFewAgreeingSlotsIsNoMatch()
    {
        var mostlyDead = Read(self: null, unread: [0, 1, 2, 3, 4, 5, 6]);

        Assert.False(RosterContinuity.IsSameMatch(mostlyDead, _applied));
        Assert.Same(mostlyDead, RosterContinuity.Apply(mostlyDead, _applied, appliedSelf: 0));
    }

    [Fact]
    public void AHeroReadConfidentlyElsewhereIsNotKeptTwice()
    {
        // Nothing was applied to slot 11, which now reads bebop confidently; bebop's own slot, 3, is unread.
        var applied = _applied.Where(pair => pair.Key != 11).ToDictionary();
        var read = Read(self: 0, unread: [3], new Dictionary<int, string> { [11] = "bebop" });

        var kept = RosterContinuity.Apply(read, applied, appliedSelf: 0);

        Assert.Null(kept.Slots[3].HeroId);
        Assert.False(kept.Slots[3].Kept);
    }
}
