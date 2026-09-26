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

    [Fact]
    public void RandomizeFillsAFullMatchWithALane()
    {
        var heroIds = Enumerable.Range(0, 20).Select(i => $"hero{i}").ToList();
        var match = new MatchState();
        match.SetRole("stale", Role.Enemy);

        match.Randomize(heroIds, new Random(1));

        Assert.NotNull(match.SelfHero);
        Assert.Equal(MatchState.MaxAllies, match.Allies.Count);
        Assert.Equal(MatchState.MaxEnemies, match.Enemies.Count);
        Assert.Equal(1 + MatchState.MaxAllies + MatchState.MaxEnemies, match.RoleMap.Count);
        Assert.All(match.RoleMap.Keys, heroId => Assert.Contains(heroId, heroIds));
        Assert.Single(match.Allies, match.IsInLane);
        Assert.Equal(2, match.Enemies.Count(match.IsInLane));
    }

    [Fact]
    public void RandomizeFillsWhatItCanFromAShortRoster()
    {
        var match = new MatchState();
        match.Randomize(["a", "b", "c"], new Random(1));

        Assert.NotNull(match.SelfHero);
        Assert.Equal(2, match.Allies.Count);
        Assert.Empty(match.Enemies);
    }
}
