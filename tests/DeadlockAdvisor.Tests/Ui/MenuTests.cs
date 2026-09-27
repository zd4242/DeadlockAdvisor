using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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
        var howScoringWorks = help.Items.OfType<MenuItem>().Single();
        Click(TopLevel.GetTopLevel(howScoringWorks)!, howScoringWorks);

        Assert.Equal(0, drags);
        var modal = ui.Window.OwnedWindows.OfType<ModalWindow>().Single();
        Assert.Equal("How scoring works", Assert.IsType<MessageModalViewModel>(((ModalViewModel)modal.DataContext!).Content).Title);
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
        using var ui = new UiHarness();
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
