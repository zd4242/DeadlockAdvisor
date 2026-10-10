using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Board;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

public class MatchBoardTests
{
    private readonly DataStore _store = Golden.LoadStore();
    private readonly MatchBoardViewModel _board;

    public MatchBoardTests()
    {
        _board = new MatchBoardViewModel(new MatchState(), () => _store, new FakeSettingsService());
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
    public void TheSearchPicksTheBestMatchFirstThoughTheTilesStayInOrder()
    {
        string? Highlighted() => _board.Tiles.SingleOrDefault(tile => tile.IsHighlighted)?.HeroId;

        _board.SearchText = "ven";

        Assert.Equal(["seven", "venator"], _board.Tiles.Where(tile => tile.IsShown).Select(tile => tile.HeroId));
        Assert.Equal("venator", Highlighted());
        _board.MoveHighlight(1);
        Assert.Equal("seven", Highlighted());

        _board.SearchText = "gt";
        Assert.Equal("grey_talon", Highlighted());
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

    [Fact]
    public void FocusingAnEnemyDimsTheOthersAndNamesThemOverTheList()
    {
        var changes = 0;
        using var subscription = _board.MatchChanged.Subscribe(_ => changes++);
        _board.SetRole("wraith", Role.Self);
        _board.SetRole("lash", Role.Enemy);
        _board.SetRole("haze", Role.Enemy);
        _board.SetRole("abrams", Role.Enemy);
        Assert.False(_board.HasFocus);
        Assert.All(_board.EnemySlots, slot => Assert.False(slot.IsDimmed));

        _board.ToggleFocus("haze");
        Assert.Equal(5, changes);
        Assert.True(_board.HasFocus);
        Assert.Equal("vs Haze", _board.FocusLabel);
        Assert.Contains("×2.14", _board.FocusTip); // 3 enemies: 15/7 against 3/7
        Assert.Contains("×0.43", _board.FocusTip);
        Assert.Equal([(false, true), (true, false), (false, true), (false, false)],
            _board.EnemySlots.Take(4).Select(slot => (slot.IsFocused, slot.IsDimmed)));

        _board.ToggleFocus("abrams");
        Assert.Equal("vs Haze, Abrams", _board.FocusLabel);

        // Allies can't be focused; the chip's × stops focusing on everyone.
        _board.ToggleFocus("wraith");
        Assert.False(_board.IsFocused("wraith"));
        _board.ClearFocusCommand.Execute().Subscribe();
        Assert.False(_board.HasFocus);
        Assert.Equal("", _board.FocusLabel);
        Assert.All(_board.EnemySlots, slot => Assert.False(slot.IsFocused || slot.IsDimmed));
    }

    [Fact]
    public void AnEnemyRatedOnNoTraitCantBeFocused()
    {
        _store.Heroes["newcomer"] = new Hero("newcomer", "Newcomer");
        _board.Rebind();
        _board.SetRole("newcomer", Role.Enemy);

        Assert.False(_board.CanFocus("newcomer"));
        Assert.False(_board.EnemySlots[0].CanFocus);
        _board.ToggleFocus("newcomer");
        Assert.False(_board.HasFocus);
    }

    private (MatchBoardViewModel Board, MatchState Match, List<string> Heroes) UnsidedBoard()
    {
        var match = new MatchState();
        var board = new MatchBoardViewModel(match, () => _store, new FakeSettingsService());
        var heroes = _store.HeroesSorted().Take(12).Select(hero => hero.HeroId).ToList();
        for (var slot = 0; slot < heroes.Count; slot++)
            match.PlaceUnsided(heroes[slot], slot);
        board.Refresh();
        return (board, match, heroes);
    }

    [Fact]
    public void HeroesReadWithoutATeamSitOnTheSidesOfTheGamesBar()
    {
        var (board, _, heroes) = UnsidedBoard();

        Assert.True(board.IsPickingSelf);
        Assert.True(board.IsSelfMissing);
        Assert.False(board.ShowsNoSelfHint);
        Assert.Equal(heroes[..6], board.AllySlots.Select(slot => slot.HeroId));
        Assert.Equal(heroes[6..], board.EnemySlots.Select(slot => slot.HeroId));
        Assert.All(board.AllySlots.Concat(board.EnemySlots), slot => Assert.True(slot.IsUnsided));
        Assert.Equal(("", "", ""), (board.AllyNetWorth, board.EnemyNetWorth, board.NetWorthLead));
        Assert.False(board.HasFocus);
    }

    [Fact]
    public void HoveringAnUnsidedHeroPreviewsTheSidesAClickWouldMake()
    {
        var (board, _, heroes) = UnsidedBoard();
        var all = board.AllySlots.Concat(board.EnemySlots).ToList();

        board.PreviewSelf(heroes[8]);

        Assert.All(board.AllySlots, slot => Assert.Equal(Role.Enemy, slot.Preview));
        Assert.Equal([Role.Ally, Role.Ally, Role.Self, Role.Ally, Role.Ally, Role.Ally], board.EnemySlots.Select(slot => slot.Preview));

        board.PreviewSelf(heroes[1]);
        Assert.Equal([Role.Ally, Role.Self, Role.Ally, Role.Ally, Role.Ally, Role.Ally], board.AllySlots.Select(slot => slot.Preview));
        Assert.All(board.EnemySlots, slot => Assert.Equal(Role.Enemy, slot.Preview));

        board.PreviewSelf(null);
        Assert.All(all, slot => Assert.Equal(Role.None, slot.Preview));

        board.PreviewSelf(heroes[1]);
        board.SetRole(heroes[1], Role.Self);
        Assert.All(all, slot => Assert.Equal(Role.None, slot.Preview));
        Assert.False(board.IsPickingSelf);
    }

    [Fact]
    public void AHeroTheStripPointedAtIsNamedInTheQuestion()
    {
        var (board, match, heroes) = UnsidedBoard();
        match.SuggestSelf(heroes[4]);
        board.Refresh();

        Assert.Equal(_store.Heroes[heroes[4]].HeroName, board.LikelySelfName);
        Assert.Equal($"Probably {board.LikelySelfName}.\nClick them to confirm, or click your hero.", board.PickSelfQuestion);
        Assert.Equal([false, false, false, false, true, false], board.AllySlots.Select(slot => slot.IsLikelySelf));
    }
}
