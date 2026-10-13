using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;

namespace DeadlockAdvisor.Tests;

public class MatchHistoryTests
{
    private static MatchPicks Picks(params (string HeroId, Role Role)[] heroes)
    {
        var match = new MatchState();
        foreach (var (heroId, role) in heroes)
            match.SetRole(heroId, role);
        return match.Picks();
    }

    [Fact]
    public void UndoAndRedoWalkBackAndForwardThroughTheChanges()
    {
        var empty = Picks();
        var one = Picks(("a", Role.Self));
        var two = Picks(("a", Role.Self), ("b", Role.Enemy));
        var history = new MatchHistory(empty);

        history.Record(one);
        history.Record(two);

        Assert.Equal(one, history.Undo());
        Assert.Equal(empty, history.Undo());
        Assert.Null(history.Undo());
        Assert.Equal(one, history.Redo());
        Assert.Equal(two, history.Redo());
        Assert.Null(history.Redo());
    }

    [Fact]
    public void ANoteOfAnUnchangedMatchIsNotAStep()
    {
        var history = new MatchHistory(Picks(("a", Role.Self)));

        history.Record(Picks(("a", Role.Self)));

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ANewChangeEndsTheRedo()
    {
        var history = new MatchHistory(Picks());
        history.Record(Picks(("a", Role.Self)));
        history.Undo();
        Assert.True(history.CanRedo);

        history.Record(Picks(("b", Role.Self)));

        Assert.False(history.CanRedo);
        Assert.Equal(Picks(), history.Undo());
    }

    [Fact]
    public void OnlyTheLatestStepsAreKept()
    {
        var history = new MatchHistory(Picks());
        for (var step = 1; step <= MatchHistory.Limit + 5; step++)
            history.Record(Picks(($"hero{step}", Role.Self)));

        var undone = 0;
        while (history.Undo() is not null)
            undone++;

        Assert.Equal(MatchHistory.Limit, undone);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var history = new MatchHistory(Picks());
        history.Record(Picks(("a", Role.Self)));
        history.Undo();

        history.Reset(Picks(("b", Role.Self)));

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void PicksDifferByTeamOrderSlotAndFocus()
    {
        Assert.NotEqual(Picks(("a", Role.Ally)), Picks(("a", Role.Enemy)));
        Assert.NotEqual(Picks(("a", Role.Ally), ("b", Role.Ally)), Picks(("b", Role.Ally), ("a", Role.Ally)));

        var focused = new MatchState();
        focused.SetRole("x", Role.Enemy);
        var before = focused.Picks();
        focused.SetFocus("x", true);
        Assert.NotEqual(before, focused.Picks());

        var placed = new MatchState();
        placed.PlaceUnsided("x", 3);
        var moved = new MatchState();
        moved.PlaceUnsided("x", 4);
        Assert.NotEqual(placed.Picks(), moved.Picks());
    }

    [Fact]
    public void RestoringPutsTheMatchBackButKeepsTheNetWorthAndDropsGoneHeroes()
    {
        var match = new MatchState();
        match.SetRole("me", Role.Self);
        match.SetRole("x", Role.Enemy);
        match.SetFocus("x", true);
        var picks = match.Picks();
        match.NetWorth.Add(new NetWorthSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, int> { ["me"] = 500 }));
        match.Clear();
        match.NetWorth.Add(new NetWorthSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, int> { ["me"] = 900 }));

        match.Restore(picks, ["me", "x"]);

        Assert.Equal("me", match.SelfHero);
        Assert.Equal(["x"], match.Enemies);
        Assert.Contains("x", match.Focused);
        Assert.Equal(900, match.NetWorth.Latest("me"));

        match.Restore(picks, ["me"]);

        Assert.Empty(match.Enemies);
        Assert.Empty(match.Focused);
    }
}
