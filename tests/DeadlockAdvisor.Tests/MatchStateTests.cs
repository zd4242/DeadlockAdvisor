using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Tests;

public class MatchStateTests
{
    [Fact]
    public void RoundTripsAndDropsUnknownHeroes()
    {
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Self);
        match.SetLane("heavy_spirit", true);

        var restored = new MatchState();
        restored.LoadSaved(match.ToSaved(), ["heavy_spirit"]); // "generic" no longer exists

        Assert.Equal(["heavy_spirit"], restored.Enemies);
        Assert.Null(restored.SelfHero);
        Assert.True(restored.IsInLane("heavy_spirit"));
    }

    [Fact]
    public void ToggleRoleClearsWhenReapplied()
    {
        var match = new MatchState();
        Assert.Equal(Role.Enemy, match.ToggleRole("a", Role.Enemy));
        Assert.Equal(Role.None, match.ToggleRole("a", Role.Enemy));
        // switching sides reassigns rather than clearing
        match.ToggleRole("a", Role.Enemy);
        Assert.Equal(Role.Ally, match.ToggleRole("a", Role.Ally));
    }

    [Fact]
    public void OnlyOneHeroCanBeSelf()
    {
        var match = new MatchState();
        match.SetRole("a", Role.Self);
        match.SetRole("b", Role.Self);

        Assert.Equal("b", match.SelfHero);
        Assert.Equal(Role.None, match.RoleOf("a"));
    }

    [Fact]
    public void OnlyAlliesAndEnemiesCanBeFlaggedInLane()
    {
        var match = new MatchState();
        match.SetRole("me", Role.Self);
        match.SetRole("foe", Role.Enemy);

        match.SetLane("me", true);
        match.SetLane("nobody", true);

        Assert.False(match.IsInLane("me"));
        Assert.False(match.IsInLane("nobody"));
        Assert.True(match.ToggleLane("foe"));
        Assert.Equal(["foe", "me"], match.LaneHeroes);
    }
}
