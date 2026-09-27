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

namespace DeadlockAdvisor.Tests.Ui;

public class MenuTests
{
    private static void Click(TopLevel root, Visual target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;
        root.MouseDown(at, MouseButton.Left);
        root.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();
    }

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
        ui.ViewModel.Match.ResultsTab = 1;
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
}
