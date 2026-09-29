using Avalonia.Input;
using DeadlockAdvisor.Features.HeroTraits;
using DeadlockAdvisor.Features.Shared.Modals.Choice;
using DeadlockAdvisor.Features.Shared.Modals.Confirmation;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Support;

namespace DeadlockAdvisor.Tests;

/// <summary>The hero grid's keyboard rules, exactly as its hint states them.</summary>
public sealed class HeroTraitsTests : IDisposable
{
    private readonly DataFixture _fixture = new();
    private readonly HeroTraitsViewModel _vm;

    public HeroTraitsTests()
    {
        _vm = new HeroTraitsViewModel(_fixture.Data, _fixture.Modals);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _fixture.Dispose();
    }

    private double Score(int row, int column) =>
        _fixture.Data.Store.HeroScore(_vm.Heroes[row].HeroId, _vm.Categories[column].CategoryId);

    private int SignedColumn => _vm.Categories.ToList().FindIndex(category => category.IsSigned);

    private void Type(string text)
    {
        foreach (var character in text)
            _vm.HandleText(character.ToString());
    }

    private void Press(Key key, KeyModifiers modifiers = KeyModifiers.None) => _vm.HandleKey(key, modifiers);

    [Fact]
    public void ANumberCommitsTheMomentNoMoreDigitsFitAndDropsAHero()
    {
        Type("5");
        Assert.Equal("5", _vm.PendingText);
        Assert.Equal(0, _vm.CurrentRow);

        Type("4");
        Assert.Equal(54, Score(0, 0));
        Assert.Null(_vm.PendingText);
        Assert.Equal((1, 0), (_vm.CurrentRow, _vm.CurrentColumn));

        // 100 takes all three keystrokes: 10 could still be the start of 100.
        Type("10");
        Assert.Equal("10", _vm.PendingText);
        Type("0");
        Assert.Equal(100, Score(1, 0));

        // A zero can't start anything bigger.
        Type("0");
        Assert.Equal(0, Score(2, 0));
        Assert.Equal(3, _vm.CurrentRow);
    }

    [Fact]
    public void SpaceOrTabEndsAShortNumberAndOtherwiseSpaceDoesNothing()
    {
        Type("7");
        Press(Key.Space);
        Assert.Equal(7, Score(0, 0));
        Assert.Equal(1, _vm.CurrentRow);

        Press(Key.Space);
        Assert.Equal(1, _vm.CurrentRow);

        Type("3");
        Press(Key.Tab);
        Assert.Equal(3, Score(1, 0));
        Assert.Equal((2, 0), (_vm.CurrentRow, _vm.CurrentColumn));

        // With nothing pending, Tab moves along the row instead.
        Press(Key.Tab);
        Assert.Equal((2, 1), (_vm.CurrentRow, _vm.CurrentColumn));
    }

    [Fact]
    public void MinusComesFirstAndOnlyOnSignedTraits()
    {
        Type("-");
        Assert.Null(_vm.PendingText);

        _vm.CurrentColumn = SignedColumn;
        Type("-");
        Assert.Equal("-_", _vm.PendingText);
        Type("-");
        Assert.Null(_vm.PendingText);
        Type("-50");
        Assert.Equal(-50, Score(0, SignedColumn));
    }

    [Fact]
    public void BackspaceRubsOutADigitOrBlanksTheCellAndMovesOn()
    {
        Type("4");
        Press(Key.Back);
        Assert.Null(_vm.PendingText);
        Assert.Equal(0, _vm.CurrentRow);

        _vm.SetValue(0, 0, 40);
        Press(Key.Back);
        Assert.Equal(0, Score(0, 0));
        Assert.Equal(1, _vm.CurrentRow);
    }

    [Fact]
    public void EscapeAbandonsAndLeavingTheCellAbandonsToo()
    {
        var before = Score(0, 0);
        Type("4");
        Press(Key.Escape);
        Assert.Null(_vm.PendingText);

        Type("4");
        _vm.CurrentRow = 2;
        Assert.Null(_vm.PendingText);
        Assert.Equal(before, Score(0, 0));
    }

    [Fact]
    public void EnterCommitsAndDropsAHeroWithoutWrapping()
    {
        Type("6");
        Press(Key.Enter);
        Assert.Equal(6, Score(0, 0));
        Assert.Equal(1, _vm.CurrentRow);

        var last = _vm.Heroes.Count - 1;
        _vm.CurrentRow = last;
        Press(Key.Enter);
        Assert.Equal((last, 0), (_vm.CurrentRow, _vm.CurrentColumn));
    }

    [Fact]
    public void FinishingAColumnWrapsToTheTopOfTheNext()
    {
        _vm.CurrentRow = _vm.Heroes.Count - 1;
        Type("55");
        Assert.Equal((0, 1), (_vm.CurrentRow, _vm.CurrentColumn));
    }

    [Fact]
    public void AnyOtherKeyFinishesTheNumberBeforeMoving()
    {
        Type("8");
        Press(Key.Right);
        Assert.Equal(8, Score(0, 0));
        Assert.Equal((0, 1), (_vm.CurrentRow, _vm.CurrentColumn));
    }

    [Fact]
    public void TheFilterHidesHeroesAndEntrySkipsThem()
    {
        // A fuzzy match: a run of letters or the starts of words.
        _vm.FilterText = "  LG ";
        Assert.Equal(["Lady Geist"], _vm.VisibleRows.Select(row => _vm.Heroes[row].HeroName));
        _vm.FilterText = "  LA ";
        var shown = _vm.VisibleRows.Select(row => _vm.Heroes[row].HeroName).ToList();
        Assert.Contains("Lash", shown);
        Assert.Contains("Lady Geist", shown);
        Assert.DoesNotContain("Holliday", shown);

        _vm.CurrentRow = _vm.VisibleRows[0];
        Type("33");
        Assert.Equal(_vm.VisibleRows[1], _vm.CurrentRow);
    }

    private List<double> ShownScores(int column) => _vm.VisibleRows.Select(row => Score(row, column)).ToList();

    [Fact]
    public void SortingCyclesHighestFirstThenLowestFirstThenByName()
    {
        _vm.CurrentColumn = 3;
        var byName = _vm.VisibleRows.ToList();

        _vm.SortCommand.Execute(SignedColumn).Subscribe();
        var descending = ShownScores(SignedColumn);
        Assert.Equal(descending.OrderDescending(), descending);
        Assert.Equal((_vm.VisibleRows[0], SignedColumn), (_vm.CurrentRow, _vm.CurrentColumn));
        // Ties keep name order.
        Assert.All(_vm.VisibleRows.Zip(_vm.VisibleRows.Skip(1)), pair =>
            Assert.True(Score(pair.First, SignedColumn) != Score(pair.Second, SignedColumn) || pair.First < pair.Second));

        _vm.SortCommand.Execute(SignedColumn).Subscribe();
        var ascending = ShownScores(SignedColumn);
        Assert.Equal(ascending.Order(), ascending);

        _vm.SortCommand.Execute(SignedColumn).Subscribe();
        Assert.Equal(-1, _vm.SortColumn);
        Assert.Equal(byName, _vm.VisibleRows);
    }

    [Fact]
    public void EntryFollowsTheSortedOrderAndEditsDoNotReSort()
    {
        _vm.SortCommand.Execute(0).Subscribe();
        var order = _vm.VisibleRows.ToList();

        Type("0");
        Assert.Equal(order[1], _vm.CurrentRow);
        Assert.Equal(order, _vm.VisibleRows);

        Press(Key.Up);
        Assert.Equal(order[0], _vm.CurrentRow);
        Press(Key.End, KeyModifiers.Control);
        Assert.Equal(order[^1], _vm.CurrentRow);
    }

    [Fact]
    public void TheFilterKeepsTheSortedOrder()
    {
        _vm.SortCommand.Execute(0).Subscribe();
        _vm.FilterText = "a";
        var shown = ShownScores(0);
        Assert.NotEmpty(shown);
        Assert.Equal(shown.OrderDescending(), shown);
    }

    [Fact]
    public void ValuesAreClampedToTheTraitsScaleAndSaved()
    {
        _vm.SetValue(0, 0, 250);
        Assert.Equal(100, Score(0, 0));
        _vm.SetValue(0, SignedColumn, -250);
        Assert.Equal(-100, Score(0, SignedColumn));

        _fixture.Clock.AdvanceBy(DataService.SaveDebounce);
        var saved = DataStore.Load(_fixture.Data.DataDir);
        Assert.Equal(100, saved.HeroScore(_vm.Heroes[0].HeroId, _vm.Categories[0].CategoryId));
    }

    [Fact]
    public void ClearHeroAsksFirstAndOnlyClearsOnConfirm()
    {
        ConfirmationModalViewModel? confirm = null;
        using var _ = _fixture.Modals.ShowModalObservable.Subscribe(shown => confirm = shown as ConfirmationModalViewModel);
        var store = _fixture.Data.Store;
        var hero = _vm.Heroes[0];
        var filled = store.HeroFilledCount(hero.HeroId);
        Assert.True(filled > 0);

        _vm.ClearHeroCommand.Execute().Subscribe();
        Assert.NotNull(confirm);
        Assert.True(confirm.IsDestructive);
        Assert.Contains(hero.HeroName, confirm.Prompt);
        confirm.CancelCommand!.Execute(null);
        Assert.Equal(filled, store.HeroFilledCount(hero.HeroId));
        Assert.False(_fixture.Modals.IsModalOpen);

        _vm.ClearHeroCommand.Execute().Subscribe();
        confirm.ConfirmCommand!.Execute(null);
        Assert.Equal(0, store.HeroFilledCount(hero.HeroId));
        Assert.StartsWith($"Cleared every trait on {hero.HeroName}.", _vm.ContextSpans.Single().Text);

        // Nothing left to lose, so nothing to ask.
        confirm = null;
        _vm.ClearHeroCommand.Execute().Subscribe();
        Assert.Null(confirm);
    }

    [Fact]
    public void CopyFromOffersOnlyRatedHeroes()
    {
        ChoiceModalViewModel? modal = null;
        using var _ = _fixture.Modals.ShowModalObservable.Subscribe(shown => modal = shown as ChoiceModalViewModel);
        var store = _fixture.Data.Store;
        var target = _vm.Heroes[0];
        store.ClearHeroScores(target.HeroId);

        _vm.CopyFromCommand.Execute().Subscribe();
        Assert.NotNull(modal);
        Assert.DoesNotContain(target.HeroId, modal.Choices.Select(choice => choice.ArtId));
        var source = _vm.Heroes.First(hero => hero.HeroId == modal.Choices[1].ArtId);
        modal.Selected = modal.Choices[1];
        modal.OkCommand.Execute().Subscribe();

        Assert.All(_vm.Categories, category =>
            Assert.Equal(store.HeroScore(source.HeroId, category.CategoryId), store.HeroScore(target.HeroId, category.CategoryId)));
        Assert.StartsWith($"Copied {source.HeroName}'s profile onto {target.HeroName}", _vm.ContextSpans.Single().Text);
    }

    [Fact]
    public void CtrlZUndoesAnEditAndCtrlYRedoesItLandingOnTheCell()
    {
        var (before, beforeBelow) = (Score(0, 0), Score(1, 0));
        Type("54");
        Type("7");
        Press(Key.Space);
        Assert.Equal((54.0, 7.0), (Score(0, 0), Score(1, 0)));
        Assert.True(_vm.CanUndo);

        Press(Key.Z, KeyModifiers.Control);
        Assert.Equal((54.0, beforeBelow), (Score(0, 0), Score(1, 0)));
        Press(Key.Z, KeyModifiers.Control);
        Assert.Equal(before, Score(0, 0));
        Assert.Equal((0, 0), (_vm.CurrentRow, _vm.CurrentColumn));
        Assert.StartsWith("Undid setting", _vm.ContextSpans.Single().Text);
        Assert.False(_vm.CanUndo);

        Press(Key.Y, KeyModifiers.Control);
        Assert.Equal(54, Score(0, 0));
        Press(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(7, Score(1, 0));
        Assert.Equal((1, 0), (_vm.CurrentRow, _vm.CurrentColumn));
        Assert.False(_vm.CanRedo);
    }

    [Fact]
    public void ANewEditDropsTheRedoHistory()
    {
        Type("54");
        _vm.UndoCommand.Execute().Subscribe();
        Assert.True(_vm.CanRedo);

        Type("33");
        Assert.Equal(33, Score(0, 0));
        Assert.False(_vm.CanRedo);
    }

    [Fact]
    public void CtrlZMidNumberOnlyTakesBackTheTyping()
    {
        Type("54");
        _vm.CurrentRow = 0;
        Type("1");
        Press(Key.Z, KeyModifiers.Control);
        Assert.Null(_vm.PendingText);
        Assert.Equal(54, Score(0, 0));
    }

    [Fact]
    public void AClearedHeroComesBackWithOneUndo()
    {
        var store = _fixture.Data.Store;
        var hero = _vm.Heroes[0];
        var profile = _vm.Categories.Select(category => store.HeroScore(hero.HeroId, category.CategoryId)).ToList();
        ConfirmationModalViewModel? confirm = null;
        using var _ = _fixture.Modals.ShowModalObservable.Subscribe(shown => confirm = shown as ConfirmationModalViewModel);

        _vm.ClearHeroCommand.Execute().Subscribe();
        confirm!.ConfirmCommand!.Execute(null);
        Assert.Equal(0, store.HeroFilledCount(hero.HeroId));

        _vm.CurrentRow = 3;
        Press(Key.Z, KeyModifiers.Control);
        Assert.Equal(profile, _vm.Categories.Select(category => store.HeroScore(hero.HeroId, category.CategoryId)));
        Assert.Equal(0, _vm.CurrentRow);
        Assert.Equal($"Undid clearing {hero.HeroName}.", _vm.ContextSpans.Single().Text);

        // The undo is saved like any other edit.
        _fixture.Clock.AdvanceBy(DataService.SaveDebounce);
        Assert.Equal(profile.Count(score => score != 0), DataStore.Load(_fixture.Data.DataDir).HeroFilledCount(hero.HeroId));
    }

    [Fact]
    public void TheHistorySurvivesASyncButNotAReloadFromDisk()
    {
        Type("54");
        _fixture.Data.NotifyReplaced();
        Assert.True(_vm.CanUndo);

        _fixture.Data.Reload();
        Assert.False(_vm.CanUndo);
        Assert.Equal(54, Score(0, 0));
    }

    [Fact]
    public void TheFooterDescribesTheFocusedCell()
    {
        _vm.CurrentColumn = 2;
        _vm.CurrentRow = 3;
        var text = string.Concat(_vm.ContextSpans.Select(span => span.Text));
        Assert.StartsWith($"{_vm.Heroes[3].HeroName}  ·  {_vm.Categories[2].CategoryName} (scale 0 to 100, currently", text);
        Assert.StartsWith($"{_vm.Heroes[3].HeroName}: ", _vm.ProgressText);
    }

    [Fact]
    public void AReloadedStoreIsPickedUp()
    {
        var path = Path.Combine(_fixture.Data.DataDir, DataStore.HeroScoresFile);
        File.WriteAllText(path, "hero_id,category_id,score\r\n");

        _fixture.Data.Reload();

        Assert.Equal(0, Score(0, 0));
    }
}
