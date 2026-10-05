using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Features.Match.Results;
using DeadlockAdvisor.Features.Shared.Modals.Base;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class MenuTests
{
    private static void Click(TopLevel root, Visual target) => Click(root, target, root);

    /// <summary>A press in a dropdown bubbles up to the title bar, which mustn't start a window drag and swallow the click.</summary>
    [AvaloniaFact]
    public void ClickingAMenuItemRunsItsCommand()
    {
        using var ui = new UiHarness();
        var drags = 0;
        ui.Window.WindowDragStarting += (_, _) => drags++;
        ui.Show();

        var help = ui.Window.GetVisualDescendants().OfType<MenuItem>().Single(item => Equals(item.Header, "_Help"));
        Click(ui.Window, help);
        Assert.True(help.IsSubMenuOpen);
        Assert.Equal(0, drags);
        var howScoringWorks = help.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "How Scoring Works"));
        Click(TopLevel.GetTopLevel(howScoringWorks)!, howScoringWorks);

        Assert.Equal(0, drags);
        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        Assert.Equal("How scoring works", Assert.IsType<MessageModalViewModel>(((ModalViewModel)modal.DataContext!).Content).Title);
    }

    [AvaloniaFact]
    public void TheDataMenusModelToolsAndCtrlRComeWithTheEditors()
    {
        using var ui = new UiHarness();
        var reloads = 0;
        using var watchReloads = ui.Data.StoreReplaced.Subscribe(_ => reloads++);
        ui.Show();
        var data = ui.Window.MainMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "_Data"));
        List<string> Shown() => data.Items.OfType<MenuItem>().Where(item => item.IsVisible).Select(item => (string)item.Header!).ToList();
        string[] modelTools = ["Sync New Heroes / Items / Categories", "Model Health Report", "Reload from Disk", "Export Snapshot to Excel"];

        Assert.Empty(Shown().Intersect(modelTools));
        Assert.Contains("Sync from Game API", Shown());
        ui.Window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);
        UiHarness.Settle();
        Assert.Equal(0, reloads);

        ui.ViewModel.Settings.General.ShowModelEditors = true;
        UiHarness.Settle();

        Assert.Equal(modelTools, Shown().Intersect(modelTools));
        ui.Window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);
        UiHarness.Settle();
        Assert.Equal(1, reloads);
    }

    /// <summary>The role menu's presses bubble up to the tile it was opened on, which mustn't take them as a click of its own.</summary>
    [AvaloniaFact]
    public void TheRoleMenuSetsExactlyTheRolePicked()
    {
        using var ui = new UiHarness();
        ui.Show();
        var board = ui.ViewModel.Match.Board;
        Click(ui.Window, ui.Window.GetVisualDescendants().OfType<ToggleButton>().Single(button => Equals(button.Content, "Edit heroes")));
        Assert.True(board.IsPickerOpen);
        var tile = ui.Window.GetVisualDescendants().OfType<Controls.HeroTile>().First(t => t.HeroId == "haze");

        var at = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Right);
        ui.Window.MouseUp(at, MouseButton.Right);
        UiHarness.Settle();
        var setAlly = TopLevel.GetTopLevel(tile)!.GetVisualDescendants().OfType<MenuItem>().Single(item => Equals(item.Header, "Set as Ally"));
        Click(TopLevel.GetTopLevel(setAlly)!, setAlly);

        Assert.Equal(Role.Ally, board.RoleOf("haze"));
        Assert.Equal(["haze"], ui.Settings.Current.LastMatch!.Roles.Keys);
    }

    /// <summary>From the other Item Formulas panel, with the item filtered out of the By Item list.</summary>
    [AvaloniaFact]
    public void TheResultMenuOpensTheItemsFormula()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
        var formulas = ui.ViewModel.ItemFormulas;
        formulas.SelectedTab = 1;
        formulas.ByItem.SearchText = "no item is called this";
        foreach (var hero in new[] { "haze", "infernus", "abrams" })
            ui.ViewModel.Match.Board.SetRole(hero, Role.Enemy);
        ui.Show();

        // The highest tier on screen, so it sits well down the tier-sorted By Item list.
        var row = ui.Window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("result") && InView(border))
            .MaxBy(border => ((ResultRowViewModel)border.DataContext!).Tier)!;
        var itemId = ((ResultRowViewModel)row.DataContext!).ItemId;
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Right);
        ui.Window.MouseUp(at, MouseButton.Right);
        UiHarness.Settle();
        var goTo = TopLevel.GetTopLevel(row)!.GetVisualDescendants().OfType<MenuItem>().Single(item => Equals(item.Header, "Go to Item Formula"));
        Click(TopLevel.GetTopLevel(goTo)!, goTo);

        Assert.True(ui.ViewModel.IsItemFormulasPage);
        Assert.True(formulas.IsByItem);
        Assert.Equal("", formulas.ByItem.SearchText);
        Assert.Equal(itemId, formulas.ByItem.CurrentItem?.ItemId);
        var list = ui.Window.ItemFormulasPage.ByItemPanel.GetVisualDescendants().OfType<ListBox>()
            .Single(candidate => candidate.ItemsSource == formulas.ByItem.Items);
        Assert.True(list.FindDescendantOfType<ScrollViewer>()!.Offset.Y > 0);
        Assert.True(InView(list.ContainerFromItem(formulas.ByItem.SelectedRow!)!));
    }

    [AvaloniaFact]
    public void TheExplanationMenuOpensTheItemsFormula()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings));
        var formulas = ui.ViewModel.ItemFormulas;
        foreach (var hero in new[] { "haze", "infernus", "abrams" })
            ui.ViewModel.Match.Board.SetRole(hero, Role.Enemy);
        ui.Show();
        var header = ui.Window.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Classes.Contains("itemHeader"));
        Assert.Null(header.ContextMenu);

        var results = ui.ViewModel.Match.Results;
        var row = results.Entries.OfType<ResultRowViewModel>().Last();
        results.Select(row);
        UiHarness.Settle();
        var at = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Right);
        ui.Window.MouseUp(at, MouseButton.Right);
        UiHarness.Settle();
        var goTo = TopLevel.GetTopLevel(header)!.GetVisualDescendants().OfType<MenuItem>().Single(item => Equals(item.Header, "Go to Item Formula"));
        Click(TopLevel.GetTopLevel(goTo)!, goTo);

        Assert.True(ui.ViewModel.IsItemFormulasPage);
        Assert.True(formulas.IsByItem);
        Assert.Equal(row.ItemId, formulas.ByItem.CurrentItem?.ItemId);
    }

    [AvaloniaFact]
    public void OnlyTheEditorsBringTheFormulaEntries()
    {
        using var ui = new UiHarness();
        foreach (var hero in new[] { "haze", "infernus", "abrams" })
            ui.ViewModel.Match.Board.SetRole(hero, Role.Enemy);
        ui.Show();
        var results = ui.ViewModel.Match.Results;
        var first = results.Entries.OfType<ResultRowViewModel>().First();
        results.Select(first);
        UiHarness.Settle();
        var header = ui.Window.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Classes.Contains("itemHeader"));
        List<Border> Rows() => ui.Window.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("result")).Take(2).ToList();
        List<string> Shown(Control owner) => RightClick(ui.Window, owner).Select(item => (string)item.Header!).ToList();

        Assert.Equal([WikiMenuItem.Text], Shown(header));
        Assert.All(Rows(), row => Assert.Equal([WikiMenuItem.Text], Shown(row)));
        Assert.False(((System.Windows.Input.ICommand)results.OpenFormulaCommand).CanExecute(results.SelectedItemId));

        ui.ViewModel.Settings.General.ShowModelEditors = true;
        UiHarness.Settle();

        var rows = Rows();
        Assert.Equal(["Go to Item Formula", WikiMenuItem.Text], Shown(header));
        Assert.All(rows, row => Assert.Equal(["Go to Item Formula", WikiMenuItem.Text], Shown(row)));
        Assert.NotSame(rows[0].ContextMenu, rows[1].ContextMenu);
    }

    [AvaloniaFact]
    public void TheMatchMenusLinkTheWikiPages()
    {
        using var ui = new UiHarness();
        foreach (var hero in new[] { "haze", "infernus", "abrams" })
            ui.ViewModel.Match.Board.SetRole(hero, Role.Enemy);
        ui.Show();

        var slot = ui.Window.GetVisualDescendants().OfType<Controls.RosterSlot>().First(s => s.HeroId == "infernus");
        Assert.Equal("Infernus", RightClickForWiki(ui.Window, slot).Page);

        var row = ui.Window.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("result"));
        var item = RightClickForWiki(ui.Window, row);
        Assert.Equal(((ResultRowViewModel)row.DataContext!).Name, item.Page);
        Assert.Equal(Core.Wiki.PageUrl(item.Page!), item.Url);
    }

    [AvaloniaFact]
    public void AHeroNameOffersCopyClearAndItsWikiPageAndBecomesCurrent()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 2));
        var page = ui.ViewModel.HeroTraits;
        ui.Show();
        var grid = ui.Window.HeroTraitsPage.Grid;

        var nameOfSecond = new Point(40, grid.HeaderHeight + Features.HeroTraits.TraitGrid.RowHeight * 1.5);
        var shown = RightClick(ui.Window, grid, nameOfSecond);

        Assert.Equal(["Copy traits from...", "Clear all traits...", WikiMenuItem.Text], shown.Select(item => item.Header));
        Assert.Equal(page.Heroes[page.VisibleRows[1]].HeroName, shown.OfType<WikiMenuItem>().Single().Page);
        Assert.Equal(page.VisibleRows[1], page.CurrentRow);
    }

    [AvaloniaFact]
    public void TheItemFormulaListsLinkTheirItemsWikiPages()
    {
        using var ui = new UiHarness(settings => UiHarness.Editing(settings, 3));
        var formulas = ui.ViewModel.ItemFormulas;
        ui.Show();

        var list = ui.Window.ItemFormulasPage.ByItemPanel.GetVisualDescendants().OfType<ListBox>()
            .Single(candidate => candidate.ItemsSource == formulas.ByItem.Items);
        var listed = list.ContainerFromIndex(1)!;
        var shown = RightClick(ui.Window, listed);
        Assert.Equal(["Copy rules from...", "Clear rules...", WikiMenuItem.Text], shown.Select(item => item.Header));
        Assert.Equal(formulas.ByItem.Items[1].HasRules, shown[1].IsEnabled);
        Assert.Equal(formulas.ByItem.Items[1].Name, shown.OfType<WikiMenuItem>().Single().Page);

        formulas.SelectedTab = 1;
        UiHarness.Settle();
        var byTrait = formulas.ByTrait;
        var grid = ui.Window.ItemFormulasPage.ByTraitPanel.Grid;
        var secondRow = new Point(100, Features.ItemFormulas.ByTrait.CoefficientGrid.HeaderHeight + Features.ItemFormulas.ByTrait.CoefficientGrid.RowHeight * 1.5);

        Assert.Equal(byTrait.Rows[1].Name, RightClickForWiki(ui.Window, grid, secondRow).Page);
        Assert.Same(byTrait.Rows[1], byTrait.CurrentRow);
    }

    /// <summary>
    /// Headless popups sit in an overlay whose dismiss layer takes any press, but desktop ones are windows the
    /// slot's handled press never reaches, so the requests are raised straight from the slots.
    /// </summary>
    [AvaloniaFact]
    public void OpeningAHerosRoleMenuClosesTheOneAlreadyOpen()
    {
        using var ui = new UiHarness();
        foreach (var hero in new[] { "haze", "infernus", "abrams" })
            ui.ViewModel.Match.Board.SetRole(hero, Role.Enemy);
        ui.Show();
        var slots = ui.Window.GetVisualDescendants().OfType<Controls.RosterSlot>().Where(s => s.HeroId is not null).ToList();

        foreach (var slot in slots)
        {
            slot.RaiseEvent(new HeroEventArgs(Controls.RosterSlot.MenuRequestedEvent, slot.HeroId!));
            UiHarness.Settle();
        }

        Assert.Equal(3, slots.Count);
        Assert.Single(ui.Window.GetVisualDescendants().OfType<ContextMenu>(), menu => menu.IsOpen);
    }

    private static WikiMenuItem RightClickForWiki(Window window, Control target, Point? at = null) =>
        RightClick(window, target, at).OfType<WikiMenuItem>().Single();

    /// <summary>
    /// Right-clicks <paramref name="target"/> (at its centre, or <paramref name="at"/> within it) and
    /// returns the shown entries of the menu that opens, closing it again.
    /// </summary>
    private static List<MenuItem> RightClick(Window window, Control target, Point? at = null)
    {
        var point = target.TranslatePoint(at ?? new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        UiHarness.Settle();
        var menu = window.GetVisualDescendants().OfType<ContextMenu>().Single(candidate => candidate.IsOpen);
        var shown = menu.GetVisualDescendants().OfType<MenuItem>().Where(item => item.IsVisible).ToList();
        menu.Close();
        UiHarness.Settle();
        return shown;
    }

    /// <summary>Wholly inside the scroll viewer showing it.</summary>
    private static bool InView(Control control)
    {
        var viewport = control.FindAncestorOfType<ScrollViewer>()!;
        var top = control.TranslatePoint(default, viewport)!.Value.Y;
        return top >= 0 && top + control.Bounds.Height <= viewport.Bounds.Height;
    }

    [AvaloniaFact]
    public void PressingTheTitleBarStillDragsTheWindow()
    {
        using var ui = new UiHarness();
        var drags = 0;
        ui.Window.WindowDragStarting += (_, _) => drags++;
        ui.Show();

        Click(ui.Window, ui.Window.FindControl<TextBlock>("WindowTitle")!);

        Assert.Equal(1, drags);
    }

    [AvaloniaFact]
    public void PressingTheTitleBarWithAMenuOpenClosesItAndDragsTheWindow()
    {
        using var ui = new UiHarness();
        var drags = 0;
        ui.Window.WindowDragStarting += (_, _) => drags++;
        ui.Show();
        var help = ui.Window.GetVisualDescendants().OfType<MenuItem>().Single(item => Equals(item.Header, "_Help"));
        Click(ui.Window, help);
        Assert.True(help.IsSubMenuOpen);

        Click(ui.Window, ui.Window.FindControl<TextBlock>("WindowTitle")!);

        Assert.False(help.IsSubMenuOpen);
        Assert.Equal(1, drags);
    }

    /// <summary>The modal's dim covers the title bar, but only its own window's content is modal: the title bar still moves it.</summary>
    [AvaloniaFact]
    public void PressingTheTitleBarUnderAModalDragsTheWindow()
    {
        using var ui = new UiHarness();
        var drags = 0;
        ui.Window.WindowDragStarting += (_, _) => drags++;
        ui.Show();
        ui.Services.GetRequiredService<IModalService>().ShowMessage("Title", "Body");
        UiHarness.Settle();
        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();

        // The modal window lies exactly over the main one, so a point in one is the same point in the other.
        void PressModalOver(Visual target) => Click(modal, target, ui.Window);
        PressModalOver(ui.Window.FindControl<TextBlock>("WindowTitle")!);
        Assert.Equal(1, drags);
        PressModalOver(ui.Window.MainMenu);
        Assert.Equal(2, drags);
        Assert.False(ui.Window.MainMenu.IsOpen);

        PressModalOver(ui.Window.StatusBar);
        Click(modal, modal.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "Title"), modal);
        Assert.Equal(2, drags);
        Assert.Same(modal, ui.Window.OwnedWindows.OfType<ModalWindow>().Single());
    }

    /// <summary>Clicks <paramref name="root"/> at the centre of <paramref name="target"/>, measured in <paramref name="frame"/>.</summary>
    private static void Click(TopLevel root, Visual target, Visual frame)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), frame)!.Value;
        root.MouseDown(at, MouseButton.Left);
        root.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();
    }
}
