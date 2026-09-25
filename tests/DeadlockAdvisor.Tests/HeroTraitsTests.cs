using Avalonia.Input;
using DeadlockAdvisor.Features.HeroTraits;
using DeadlockAdvisor.Features.Shared.Modals.Choice;
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
        _vm.FilterText = "  LA ";
        var shown = _vm.VisibleRows.Select(row => _vm.Heroes[row].HeroName).ToList();
        Assert.All(shown, name => Assert.Contains("la", name.ToLowerInvariant()));

        _vm.CurrentRow = _vm.VisibleRows[0];
        Type("33");
        Assert.Equal(_vm.VisibleRows[1], _vm.CurrentRow);
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
    public void CopyFromOffersOnlyRatedHeroesAndClearHeroBlanksOne()
    {
        ChoiceModalViewModel? modal = null;
        using var _ = _fixture.Modals.ShowModalObservable.Subscribe(shown => modal = shown as ChoiceModalViewModel);
        var store = _fixture.Data.Store;
        var target = _vm.Heroes[0];

        _vm.ClearHeroCommand.Execute().Subscribe();
        Assert.Equal(0, store.HeroFilledCount(target.HeroId));
        Assert.Equal($"Cleared every trait on {target.HeroName}.", _vm.ContextSpans.Single().Text);

        _vm.CopyFromCommand.Execute().Subscribe();
        Assert.NotNull(modal);
        Assert.DoesNotContain(target.HeroName, modal.Choices);
        var source = _vm.Heroes.First(hero => hero.HeroName == modal.Choices[1]);
        modal.SelectedIndex = 1;
        modal.OkCommand.Execute().Subscribe();

        Assert.All(_vm.Categories, category =>
            Assert.Equal(store.HeroScore(source.HeroId, category.CategoryId), store.HeroScore(target.HeroId, category.CategoryId)));
        Assert.StartsWith($"Copied {source.HeroName}'s profile onto {target.HeroName}", _vm.ContextSpans.Single().Text);
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
