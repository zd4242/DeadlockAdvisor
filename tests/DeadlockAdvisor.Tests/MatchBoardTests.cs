using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Board;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class MatchBoardTests
{
    private readonly MatchBoardViewModel _board;

    public MatchBoardTests()
    {
        var store = Golden.LoadStore();
        _board = new MatchBoardViewModel(new MatchState(), () => store);
    }

    [Fact]
    public void TheMatchBarFillsEachTeamFromTheLeftAndClosesGaps()
    {
        _board.SetRole("haze", Role.Ally);
        _board.SetRole("wraith", Role.Self);
        _board.SetRole("abrams", Role.Ally);
        _board.SetRole("lash", Role.Enemy);

        Assert.Equal(["haze", "wraith", "abrams", null, null, null], _board.AllySlots.Select(slot => slot.HeroId));
        Assert.True(_board.AllySlots[1].IsSelf);
        Assert.Equal("3/6", _board.AllyCount);
        Assert.Equal("lash", _board.EnemySlots[0].HeroId);
        Assert.All(_board.EnemySlots, slot => Assert.Equal(Role.Enemy, slot.Team));

        _board.SetRole("haze", Role.None);
        Assert.Equal(["wraith", "abrams", null, null, null, null], _board.AllySlots.Select(slot => slot.HeroId));
    }

    [Fact]
    public void AnEmptyAllySlotPicksYouUntilYoureSet()
    {
        Assert.False(_board.IsPickerOpen);
        _board.EmptySlotClicked(Role.Ally);
        Assert.True(_board.IsPickerOpen);
        Assert.Equal(Role.Self, _board.Mode);

        _board.SetRole("wraith", Role.Self);
        _board.EmptySlotClicked(Role.Ally);
        Assert.Equal(Role.Ally, _board.Mode);

        _board.EmptySlotClicked(Role.Enemy);
        Assert.Equal(Role.Enemy, _board.Mode);
    }
}
