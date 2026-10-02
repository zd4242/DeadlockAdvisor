using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.ItemFormulas.ByTrait;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests.Ui;

public class ItemFormulasPageTests
{
    private const string Item = "focus_lens";
    private const string Trait = "deals_spirit_damage_general";

    /// <summary>The state the screenshots are taken in.</summary>
    [AvaloniaFact]
    public void ByItemPanelRenders()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        var page = ui.ViewModel.ItemFormulas.ByItem;
        page.SelectedRow = page.Items.Single(row => row.ItemId == Item);
        ui.Show();

        ui.Screenshot("by_item.png");
        Assert.Equal("Focus Lens", page.DetailTitle);
        Assert.NotEmpty(page.Rules);
        Assert.NotEmpty(page.DerivedRules);
        Assert.NotEmpty(page.Preview!);
    }

    [AvaloniaFact]
    public void AFilledSearchShowsItsClearButtonWithoutFocus()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        var page = ui.ViewModel.ItemFormulas.ByItem;
        page.SearchText = "asdf";
        ui.Show();
        var search = ui.Window.ItemFormulasPage.ByItemPanel.SearchBox;
        Assert.False(search.IsFocused);

        var clear = search.GetVisualDescendants().OfType<Button>().Single();
        var glyph = clear.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        Assert.True(clear.IsEffectivelyVisible);
        Assert.NotNull(glyph.Fill);
        Assert.True(glyph.Bounds.Width > 0 && glyph.Bounds.Height > 0);
        var centre = clear.TranslatePoint(new Point(clear.Bounds.Width / 2, clear.Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseDown(centre, MouseButton.Left);
        ui.Window.MouseUp(centre, MouseButton.Left);
        UiHarness.Settle();

        Assert.Equal("", page.SearchText);
        Assert.Empty(search.GetVisualDescendants().OfType<Button>());
    }

    [AvaloniaFact]
    public void TheDividerStopsBeforeEitherPaneIsSquashed()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        var page = ui.ViewModel.ItemFormulas.ByItem;
        page.SelectedRow = page.Items.Single(row => row.ItemId == Item);
        ui.Show();
        var panel = ui.Window.ItemFormulasPage.ByItemPanel;
        var split = panel.DetailSplit;
        var divider = panel.DetailSplitter;

        foreach (var offset in new[] { 2000.0, -2000.0 })
        {
            var start = divider.TranslatePoint(new Point(divider.Bounds.Width / 2, 4), ui.Window)!.Value;
            ui.Window.MouseDown(start, MouseButton.Left);
            ui.Window.MouseMove(start + new Point(0, offset / 2));
            ui.Window.MouseMove(start + new Point(0, offset));
            ui.Window.MouseUp(start + new Point(0, offset), MouseButton.Left);
            UiHarness.Settle();

            // Dragged all the way, the pane it was pushed into stops at its minimum.
            var squeezed = offset > 0 ? split.RowDefinitions[2] : split.RowDefinitions[0];
            Assert.Equal(140, squeezed.ActualHeight, 0.5);
        }
        Assert.NotNull(ui.Settings.Current.ByItemSplitterPosition);
    }

    [AvaloniaFact]
    public void ByTraitPanelRenders()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        ui.ViewModel.ItemFormulas.SelectedTab = 1;
        var page = ui.ViewModel.ItemFormulas.ByTrait;
        page.SelectedCategory = page.Categories.Single(category => category.CategoryId == Trait);
        ui.Show();

        ui.Screenshot("by_trait.png");
        Assert.NotEmpty(page.StatRules);
        Assert.Equal(page.Rows[0], page.CurrentRow);
    }

    [AvaloniaFact]
    public void TypingDownTheCoefficientColumnSetsEachItem()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        ui.ViewModel.ItemFormulas.SelectedTab = 1;
        var page = ui.ViewModel.ItemFormulas.ByTrait;
        page.SelectedCategory = page.Categories.Single(category => category.CategoryId == Trait);
        ui.Show();
        ui.Window.ItemFormulasPage.ByTraitPanel.FocusGrid();
        UiHarness.Settle();

        var (first, second) = (page.Rows[0], page.Rows[1]);
        ui.Window.KeyTextInput("4");
        ui.Window.KeyTextInput("-");
        ui.Window.KeyTextInput("2");
        UiHarness.Settle();

        var store = ui.Data.Store;
        Assert.Equal(4, store.Coefficient(first.ItemId, Trait, Relation.Against));
        Assert.Equal(-2, store.Coefficient(second.ItemId, Trait, Relation.Against));
        Assert.Same(page.Rows[2], page.CurrentRow);

        // Backspace on the row above clears it and moves on again.
        ui.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        ui.Window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        UiHarness.Settle();
        Assert.Equal(0, store.Coefficient(second.ItemId, Trait, Relation.Against));
        Assert.Same(page.Rows[2], page.CurrentRow);

        ui.Data.FlushSaves();
        Assert.Equal(4, DataStore.Load(ui.Data.DataDir).Coefficient(first.ItemId, Trait, Relation.Against));
    }

    [AvaloniaFact]
    public async Task HoveringAnIconInTheGridShowsItsCard()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        ui.ViewModel.ItemFormulas.SelectedTab = 1;
        var page = ui.ViewModel.ItemFormulas.ByTrait;
        ui.Show();
        var grid = ui.Window.ItemFormulasPage.ByTraitPanel.Grid;

        var icon = grid.TranslatePoint(new Point(15, CoefficientGrid.HeaderHeight + CoefficientGrid.RowHeight * 1.5), ui.Window)!.Value;
        ui.Window.MouseMove(icon);
        Assert.True(await UiHarness.WaitUntilAsync(() => ui.Window.ItemCards.ShownItemId is not null));
        Assert.Equal(page.Rows[1].ItemId, ui.Window.ItemCards.ShownItemId);

        // The name beside it doesn't.
        ui.Window.MouseMove(icon + new Point(200, 0));
        UiHarness.Settle();
        Assert.Null(ui.Window.ItemCards.ShownItemId);
    }

    [AvaloniaFact]
    public void HeaderClicksSortAscendingThenDescendingThenClear()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        ui.ViewModel.ItemFormulas.SelectedTab = 1;
        var page = ui.ViewModel.ItemFormulas.ByTrait;
        ui.Show();
        var unsorted = page.Rows.Select(row => row.ItemId).ToList();

        page.SortCommand.Execute(0).Subscribe();
        var ascending = page.Rows.Select(row => row.Name.ToLowerInvariant()).ToList();
        Assert.Equal(ascending.Order(StringComparer.Ordinal), ascending);

        page.SortCommand.Execute(0).Subscribe();
        var descending = page.Rows.Select(row => row.Name.ToLowerInvariant()).ToList();
        Assert.Equal(descending.OrderDescending(StringComparer.Ordinal), descending);

        page.SortCommand.Execute(0).Subscribe();
        Assert.Equal(-1, page.SortColumn);
        Assert.Equal(unsorted, page.Rows.Select(row => row.ItemId));
        UiHarness.Settle();
        Assert.Equal(CoefficientGrid.HeaderHeight + page.Rows.Count * CoefficientGrid.RowHeight,
            ui.Window.ItemFormulasPage.ByTraitPanel.Grid.Bounds.Height);
    }
}
