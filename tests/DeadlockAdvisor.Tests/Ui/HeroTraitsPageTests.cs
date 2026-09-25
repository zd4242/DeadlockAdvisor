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
            settings.Current.LastPage = 1;
            settings.Current.ZoomIndex = zoomIndex;
        });
        var page = ui.ViewModel.HeroTraits;
        (page.CurrentRow, page.CurrentColumn) = (3, 2);
        ui.Show();

        ui.Screenshot(file);
        Assert.Equal("Billy", page.Heroes[page.CurrentRow].HeroName);
    }

    [AvaloniaFact]
    public void TypingIntoTheGridCommitsAndMovesDown()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
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

    [AvaloniaFact]
    public void F2OpensASpinBoxOverTheCellAndEnterCommitsIt()
    {
        using var ui = new UiHarness(settings => settings.Current.LastPage = 1);
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
