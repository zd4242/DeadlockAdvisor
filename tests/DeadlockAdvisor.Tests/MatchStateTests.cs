using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Tests;

public class MatchStateTests
{
    [Fact]
    public void RoundTripsAndDropsUnknownHeroes()
    {
        var match = new MatchState();
        match.SetRole("heavy_spirit", Role.Enemy);
        match.SetRole("generic", Role.Self);

        var restored = new MatchState();
        restored.LoadSaved(match.ToSaved(), ["heavy_spirit"]); // "generic" no longer exists

        Assert.Equal(["heavy_spirit"], restored.Enemies);
        Assert.Null(restored.SelfHero);
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
    public void TeamsKeepTheirPickOrderWithYouInYourOwnPlace()
    {
        var match = new MatchState();
        match.SetRole("a", Role.Ally);
        match.SetRole("me", Role.Self);
        match.SetRole("b", Role.Ally);
        match.SetRole("c", Role.Ally);
        match.SetRole("x", Role.Enemy);
        match.SetRole("y", Role.Enemy);

        Assert.Equal(["a", "me", "b", "c"], match.OwnTeam);

        // Changing sides takes the next free place on the new team...
        match.SetRole("a", Role.Enemy);
        Assert.Equal(["me", "b", "c"], match.OwnTeam);
        Assert.Equal(["x", "y", "a"], match.Enemies);

        // ...but a teammate becoming you trades places with nobody: the previous you stays on as an ally.
        match.SetRole("b", Role.Self);
        Assert.Equal(["me", "b", "c"], match.OwnTeam);
        Assert.Equal("b", match.SelfHero);
        Assert.Equal(Role.Ally, match.RoleOf("me"));

        var restored = new MatchState();
        restored.LoadSaved(match.ToSaved(), ["me", "b", "c", "x", "y", "a"]);
        Assert.Equal(["me", "b", "c"], restored.OwnTeam);
        Assert.Equal("b", restored.SelfHero);
        Assert.Equal(["x", "y", "a"], restored.Enemies);
    }

    [Fact]
    public void TopBarSlotsGoWithTheHeroesTheyWereReadFor()
    {
        var match = new MatchState();
        VisionApply.ApplyToMatch(match, ["me", "a", "b", null, null, null, "x", "y", null, null, null, null], 0);
        Assert.Equal([("me", 0), ("a", 1), ("b", 2), ("x", 6), ("y", 7)], match.Slots.Select(entry => (entry.Key, entry.Value)));
        Assert.Equal("x", match.HeroInSlot(6));

        // Switching who's you leaves both where the game's top bar has them.
        match.SetRole("a", Role.Self);
        Assert.Equal([("me", 0), ("a", 1), ("b", 2), ("x", 6), ("y", 7)], match.Slots.Select(entry => (entry.Key, entry.Value)));
        match.SetRole("me", Role.Self);
        Assert.Equal(Role.Ally, match.RoleOf("a"));

        // An enemy becoming you swaps the teams, each in its own order, and nobody's slot moves.
        match.SetRole("y", Role.Self);
        Assert.Equal(["x", "y"], match.OwnTeam);
        Assert.Equal("y", match.SelfHero);
        Assert.Equal(["me", "a", "b"], match.Enemies);
        Assert.Equal([("me", 0), ("a", 1), ("b", 2), ("x", 6), ("y", 7)], match.Slots.Select(entry => (entry.Key, entry.Value)));
        match.SetRole("me", Role.Self);
        Assert.Equal(["me", "a", "b"], match.OwnTeam);
        Assert.Equal(["x", "y"], match.Enemies);

        match.SetRole("a", Role.None);
        match.SetRole("b", Role.Enemy);
        match.SetRole("me", Role.Self);
        Assert.Equal(["me", "x", "y"], match.Slots.Keys);
        Assert.Null(match.HeroInSlot(1));

        match.Clear();
        Assert.Empty(match.Slots);
    }

    [Fact]
    public void OnlyEnemiesCanBeFocusedAndLeavingTheEnemyTeamDropsIt()
    {
        var match = new MatchState();
        VisionApply.ApplyToMatch(match, ["me", "a", "b", null, null, null, "x", "y", "z", null, null, null], 0);

        Assert.False(match.ToggleFocus("a"));
        Assert.False(match.SetFocus("nobody", true));
        Assert.True(match.ToggleFocus("x"));
        Assert.True(match.ToggleFocus("y"));
        Assert.Equal(["x", "y"], match.Focused.Order());
        Assert.False(match.ToggleFocus("y"));
        Assert.Equal(["x"], match.Focused);

        // Still an enemy: kept. Moved to your team, or removed: dropped.
        match.SetRole("x", Role.Enemy);
        Assert.Equal(["x"], match.Focused);
        match.SetRole("x", Role.Ally);
        Assert.Empty(match.Focused);
        match.SetFocus("z", true);
        match.SetRole("z", Role.None);
        Assert.Empty(match.Focused);

        // Playing as an enemy swaps the teams, so the focused enemies are allies now.
        match.SetFocus("y", true);
        match.SetRole("y", Role.Self);
        Assert.Empty(match.Focused);

        // And back: y is the enemy again.
        match.SetRole("me", Role.Self);
        Assert.True(match.SetFocus("y", true));
        match.ClearFocus();
        Assert.Empty(match.Focused);
        match.SetFocus("y", true);
        match.Clear();
        Assert.Empty(match.Focused);
    }

    [Fact]
    public void ADetectionOfTheSameHeroesKeepsTheFocusAndANewLineUpDropsIt()
    {
        string?[] heroes = ["me", "a", null, null, null, null, "x", "y", null, null, null, null];
        var match = new MatchState();
        VisionApply.ApplyToMatch(match, heroes, 0);
        match.SetFocus("y", true);

        VisionApply.ApplyToMatch(match, heroes, 0);
        Assert.Equal(["y"], match.Focused);

        VisionApply.ApplyToMatch(match, ["me", "a", null, null, null, null, "x", "z", null, null, null, null], 0);
        Assert.Empty(match.Focused);
    }

    [Fact]
    public void TheFocusIsSavedWithTheMatch()
    {
        var match = new MatchState();
        VisionApply.ApplyToMatch(match, ["me", "a", null, null, null, null, "x", "y", null, null, null, null], 0);
        match.SetFocus("x", true);
        match.SetFocus("y", true);

        var restored = new MatchState();
        restored.LoadSaved(match.ToSaved(), ["me", "a", "x"]); // "y" no longer exists

        Assert.Equal(["x"], restored.Focused);
    }

    [Fact]
    public void NetWorthHistoryKeepsEachHerosLatestReading()
    {
        var start = new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
        var history = new NetWorthHistory();
        history.Add(new NetWorthSnapshot(start, new Dictionary<string, int> { ["a"] = 10_000, ["x"] = 12_000 }));
        // A side whose pills didn't add up leaves its heroes out of a reading.
        history.Add(new NetWorthSnapshot(start.AddMinutes(2), new Dictionary<string, int> { ["a"] = 13_000 }));
        history.Add(new NetWorthSnapshot(start.AddMinutes(3), new Dictionary<string, int>()));

        Assert.Equal(2, history.Snapshots.Count);
        Assert.Equal(13_000, history.Latest("a"));
        Assert.Equal(12_000, history.Latest("x"));
        Assert.Null(history.Latest("nobody"));
        Assert.Equal((3_000, TimeSpan.FromMinutes(2)), history.Change("a"));
        Assert.Null(history.Change("x"));
    }

    [Fact]
    public void SlotsAndNetWorthAreSavedWithTheMatch()
    {
        var at = new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);
        var match = new MatchState();
        VisionApply.ApplyToMatch(match, ["me", "a", null, null, null, null, "x", null, null, null, null, null], 0,
            netWorth: ([5_000, 6_000, null, null, null, null, 7_000, null, null, null, null, null], at));

        var restored = new MatchState();
        restored.LoadSaved(match.ToSaved(), ["me", "x"]); // "a" no longer exists

        Assert.Equal([("me", 0), ("x", 6)], restored.Slots.Select(entry => (entry.Key, entry.Value)));
        Assert.Equal(at, restored.NetWorth.LatestAt);
        Assert.Equal((5_000, 7_000), (restored.NetWorth.Latest("me"), restored.NetWorth.Latest("x")));
        Assert.Null(restored.NetWorth.Latest("a"));
    }

    [Fact]
    public void RandomizeFillsAFullMatch()
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

    [Fact]
    public void RandomizeCanKeepYourHero()
    {
        var heroIds = Enumerable.Range(0, 20).Select(i => $"hero{i}").ToList();
        var match = new MatchState();
        match.SetRole("hero0", Role.Self);
        match.SetRole("hero1", Role.Ally);
        match.SetRole("hero2", Role.Enemy);

        match.Randomize(heroIds, new Random(1), RandomizeKeep.Self);

        Assert.Equal("hero0", match.SelfHero);
        Assert.Equal(MatchState.MaxAllies, match.Allies.Count);
        Assert.Equal(MatchState.MaxEnemies, match.Enemies.Count);
        Assert.Equal(1 + MatchState.MaxAllies + MatchState.MaxEnemies, match.RoleMap.Count);
    }

    [Fact]
    public void RandomizeCanKeepYourTeamAndTopItUp()
    {
        var heroIds = Enumerable.Range(0, 20).Select(i => $"hero{i}").ToList();
        var match = new MatchState();
        match.SetRole("hero0", Role.Ally);
        match.SetRole("hero1", Role.Self);
        match.SetRole("hero2", Role.Ally);
        match.SetRole("hero3", Role.Enemy);

        match.Randomize(heroIds, new Random(1), RandomizeKeep.OwnTeam);

        Assert.Equal(["hero0", "hero1", "hero2"], match.OwnTeam.Take(3));
        Assert.Equal("hero1", match.SelfHero);
        Assert.Equal(MatchState.MaxAllies, match.Allies.Count);
        Assert.Equal(MatchState.MaxEnemies, match.Enemies.Count);
        Assert.Equal(1 + MatchState.MaxAllies + MatchState.MaxEnemies, match.RoleMap.Count);
    }

    [Fact]
    public void RandomizeKeepingYourHeroDrawsOneWhenYoureNotSet()
    {
        var match = new MatchState();
        match.Randomize(["a", "b", "c"], new Random(1), RandomizeKeep.Self);

        Assert.NotNull(match.SelfHero);
        Assert.Equal(2, match.Allies.Count);
    }

    // -- heroes read off the bar before the teams are known ---------------------

    private static MatchState Unsided(int count = 12)
    {
        var match = new MatchState();
        for (var slot = 0; slot < count; slot++)
            match.PlaceUnsided($"h{slot}", slot);
        return match;
    }

    [Fact]
    public void UnsidedHeroesAreInTheMatchButNotOnATeam()
    {
        var match = Unsided();

        Assert.True(match.HasUnsided);
        Assert.False(match.IsEmpty);
        Assert.Equal(Enumerable.Range(0, 12).Select(slot => $"h{slot}"), match.Unsided);
        Assert.Empty(match.Allies);
        Assert.Empty(match.Enemies);
        Assert.Null(match.SelfHero);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(8, 6)]
    public void PickingYourselfSplitsTheSidesAndKeepsTheBarsOrder(int slot, int ownBase)
    {
        var match = Unsided();

        match.SetRole($"h{slot}", Role.Self);

        Assert.False(match.HasUnsided);
        Assert.Equal($"h{slot}", match.SelfHero);
        Assert.Equal(Enumerable.Range(ownBase, 6).Select(i => $"h{i}"), match.OwnTeam);
        Assert.Equal(Enumerable.Range(6 - ownBase, 6).Select(i => $"h{i}"), match.Enemies);
        Assert.Equal(Enumerable.Range(0, 12), Enumerable.Range(0, 12).Select(i => match.Slots[$"h{i}"]));
    }

    [Theory]
    [InlineData(Role.Ally, "h1", "h7")]
    [InlineData(Role.Enemy, "h7", "h1")]
    public void AnyRoleGivenToAnUnsidedHeroSplitsTheSidesWithoutPickingYou(Role role, string ally, string enemy)
    {
        var match = Unsided();

        match.SetRole("h1", role);

        Assert.Null(match.SelfHero);
        Assert.Equal(Role.Ally, match.RoleOf(ally));
        Assert.Equal(Role.Enemy, match.RoleOf(enemy));
        Assert.Equal(6, match.Allies.Count);
        Assert.Equal(6, match.Enemies.Count);
    }

    [Fact]
    public void AStreetBrawlSplitsTheSameWay()
    {
        var match = new MatchState();
        foreach (var slot in new[] { 2, 3, 4, 5, 6, 7, 8, 9 })
            match.PlaceUnsided($"h{slot}", slot);

        match.SetRole("h7", Role.Self);

        Assert.Equal(["h6", "h7", "h8", "h9"], match.OwnTeam);
        Assert.Equal(["h2", "h3", "h4", "h5"], match.Enemies);
    }

    [Fact]
    public void RemovingAnUnsidedHeroLeavesTheRestWaiting()
    {
        var match = Unsided();

        match.SetRole("h3", Role.None);

        Assert.Equal(11, match.Unsided.Count);
        Assert.False(match.Slots.ContainsKey("h3"));
        Assert.True(match.HasUnsided);
    }

    [Fact]
    public void PickingAHeroFromOutsideTheReadDropsTheUnsidedOnes()
    {
        var match = Unsided();

        match.SetRole("outsider", Role.Self);

        Assert.False(match.HasUnsided);
        Assert.Equal("outsider", Assert.Single(match.RoleMap).Key);
        Assert.Empty(match.Slots);
    }

    [Fact]
    public void UnsidedHeroesAndTheHeroThatLooksLikeYouSurviveBeingSaved()
    {
        var match = Unsided();
        match.SuggestSelf("h4");

        var restored = new MatchState();
        restored.LoadSaved(match.ToSaved(), Enumerable.Range(0, 12).Select(slot => $"h{slot}"));

        Assert.Equal(match.Unsided, restored.Unsided);
        Assert.Equal(Enumerable.Range(0, 12), Enumerable.Range(0, 12).Select(i => restored.Slots[$"h{i}"]));
        Assert.Equal("h4", restored.LikelyYou);
    }

    [Fact]
    public void TheSuggestedHeroIsForgottenOnceTheSidesAreSplitOrTheMatchIsCleared()
    {
        var match = Unsided();
        match.SuggestSelf("h4");
        Assert.Equal("h4", match.LikelyYou);

        match.SetRole("h9", Role.Self);
        Assert.Null(match.LikelyYou);

        match.Clear();
        match.PlaceUnsided("h4", 4);
        Assert.Null(match.LikelyYou);
    }
}
