using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests.Ui;

public class HeroTraitsPageTests
{
    /// <summary>The Python screenshot's state (scratchpad shoot_python_editors.py): Billy's Bullet Damage (General).</summary>
    [AvaloniaTheory]
    [InlineData(2, "hero_traits.png")]
    [InlineData(5, "hero_traits_150.png")]
    public void HeroTraitsPageRenders(int zoomIndex, string file)
    {
        using var ui = new UiHarness(settings =>
        {
            UiHarness.Editing(settings, 1);
            settings.Current.ZoomIndex = zoomIndex;
        });
        var page = ui.ViewModel.HeroTraits;
        (page.CurrentRow, page.CurrentColumn) = (3, 2);
        ui.Show();

        ui.Screenshot(file);
        Assert.Equal("Billy", page.Heroes[page.CurrentRow].HeroName);
    }

    /// <summary>Copy from… end to end with the mouse: the toolbar button, then the pick and OK in the modal's own window.</summary>
    [AvaloniaFact]
    public void CopyFromClonesTheProfilePickedInTheModal()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 1));
        var page = ui.ViewModel.HeroTraits;
        var store = ui.Data.Store;
        var target = page.Heroes.Single(hero => hero.HeroName == "Abrams");
        page.CurrentRow = page.Heroes.ToList().IndexOf(target);
        ui.Show();

        static void Click(TopLevel root, Visual target)
        {
            var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;
            root.MouseDown(at, MouseButton.Left);
            root.MouseUp(at, MouseButton.Left);
            UiHarness.Settle();
        }

        Click(ui.Window, ui.Window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Copy from...")));
        var modal = ui.Window.OwnedWindows.OfType<Features.Shared.Modals.Base.ModalWindow>().Single();
        var choice = Assert.IsType<Features.Shared.Modals.Choice.ChoiceModalViewModel>(((Features.Shared.Modals.Base.ModalViewModel)modal.DataContext!).Content);
        choice.SelectedIndex = choice.Choices.ToList().IndexOf("Billy");
        UiHarness.Settle();
        Click(modal, modal.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "OK")));

        var billy = page.Heroes.Single(hero => hero.HeroName == "Billy").HeroId;
        Assert.All(page.Categories, category =>
            Assert.Equal(store.HeroScore(billy, category.CategoryId), store.HeroScore(target.HeroId, category.CategoryId)));
    }

    [AvaloniaFact]
    public void TypingIntoTheGridCommitsAndMovesDown()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 1));
        var page = ui.ViewModel.HeroTraits;
        ui.Show();
        ui.Window.HeroTraitsPage.FocusGrid();
        UiHarness.Settle();

        var first = page.Heroes[0].HeroId;
        var second = page.Heroes[1].HeroId;
        var category = page.Categories[0].CategoryId;

        ui.Window.KeyTextInput("5");
        UiHarness.Settle();
        Assert.Equal("5", page.PendingText);
        ui.Window.KeyTextInput("4");
        UiHarness.Settle();
        Assert.Equal(54, ui.Data.Store.HeroScore(first, category));
        Assert.Equal(1, page.CurrentRow);

        ui.Window.KeyTextInput("7");
        ui.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal(7, ui.Data.Store.HeroScore(second, category));
        Assert.Equal(2, page.CurrentRow);

        ui.Window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal(0, ui.Data.Store.HeroScore(page.Heroes[2].HeroId, category));
        Assert.Equal(3, page.CurrentRow);

        // Tab ends a short number as Space does, and doesn't move focus out of the grid.
        ui.Window.KeyTextInput("3");
        ui.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal(3, ui.Data.Store.HeroScore(page.Heroes[3].HeroId, category));
        Assert.Equal(4, page.CurrentRow);
        Assert.Equal(0, page.CurrentColumn);

        ui.Data.FlushSaves();
        Assert.Equal(54, DataStore.Load(ui.Data.DataDir).HeroScore(first, category));
    }

    /// <summary>A click high on a slanted label sorts by the column that label leans off, not the one it's drawn over.</summary>
    [AvaloniaFact]
    public void ClickingATraitHeaderSortsByIt()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 1));
        var page = ui.ViewModel.HeroTraits;
        ui.Show();
        var grid = ui.Window.HeroTraitsPage.Grid;

        // On column 2's label, 30px up its slant: by then it's drawn over column 3.
        const double rise = 30;
        var foot = TraitGridColumnCenter(2);
        var at = grid.TranslatePoint(new Point(foot + rise, grid.HeaderHeight - rise), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Left);
        ui.Window.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();

        Assert.Equal((2, true), (page.SortColumn, page.SortDescending));
        Assert.Equal((page.VisibleRows[0], 2), (page.CurrentRow, page.CurrentColumn));
        Assert.True(grid.IsFocused);
        ui.Screenshot("hero_traits_sorted.png");
    }

    private static double TraitGridColumnCenter(int column) =>
        Features.HeroTraits.TraitGrid.RowHeaderWidth + (column + 0.5) * Features.HeroTraits.TraitGrid.ColumnWidth;

    [AvaloniaFact]
    public void F2OpensASpinBoxOverTheCellAndEnterCommitsIt()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 1));
        var page = ui.ViewModel.HeroTraits;
        ui.Show();
        ui.Window.HeroTraitsPage.FocusGrid();
        UiHarness.Settle();

        ui.Window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
        UiHarness.Settle();
        var editor = ui.Window.HeroTraitsPage.Grid.GetVisualDescendants().OfType<NumericUpDown>().Single();
        Assert.True(editor.IsVisible);
        Assert.True(editor.IsKeyboardFocusWithin);

        ui.Window.KeyTextInput("37");
        ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiHarness.Settle();

        Assert.False(editor.IsVisible);
        Assert.Equal(37, ui.Data.Store.HeroScore(page.Heroes[0].HeroId, page.Categories[0].CategoryId));
        Assert.True(ui.Window.HeroTraitsPage.Grid.IsFocused);
        // The spin box takes whole numbers, as Qt's QSpinBox did, and doesn't move the cell.
        Assert.Equal((0, 0), (page.CurrentRow, page.CurrentColumn));
    }
}
