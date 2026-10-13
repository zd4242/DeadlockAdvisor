using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>Dragging a portrait along the match bar to reorder its team, through the real controls.</summary>
public class RosterDragTests
{
    private static Point Center(Visual target, Visual root) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;

    private static Point Slot(UiHarness ui, string row, int index)
    {
        var items = ui.Window.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == row);
        return Center(items.GetVisualDescendants().OfType<RosterSlot>().ElementAt(index), ui.Window);
    }

    private static void Drag(UiHarness ui, Point from, params Point[] along)
    {
        ui.Window.MouseDown(from, MouseButton.Left);
        foreach (var point in along)
            ui.Window.MouseMove(point);
        ui.Window.MouseUp(along.Length > 0 ? along[^1] : from, MouseButton.Left);
        UiHarness.Settle();
    }

    private static UiHarness Match()
    {
        var ui = new UiHarness();
        var board = ui.ViewModel.Match.Board;
        foreach (var hero in new[] { "dynamo", "kelvin", "paradox" })
            board.SetRole(hero, Role.Ally);
        board.SetRole("wraith", Role.Self);
        foreach (var hero in new[] { "haze", "lash" })
            board.SetRole(hero, Role.Enemy);
        ui.Show();
        return ui;
    }

    [AvaloniaFact]
    public void DraggingAPortraitOntoAnotherMovesItThereWithoutClickingIt()
    {
        using var ui = Match();
        var board = ui.ViewModel.Match.Board;

        Drag(ui, Slot(ui, "AllyRow", 0), Slot(ui, "AllyRow", 1), Slot(ui, "AllyRow", 2));

        Assert.Equal(["kelvin", "paradox", "dynamo", "wraith"], board.AllySlots.Take(4).Select(slot => slot.HeroId));
        // A click on a teammate would have made them you.
        Assert.Equal("wraith", ui.ViewModel.Match.Match.SelfHero);

        Drag(ui, Slot(ui, "EnemyRow", 1), Slot(ui, "EnemyRow", 0));
        Assert.Equal(["lash", "haze"], board.EnemySlots.Take(2).Select(slot => slot.HeroId));
    }

    [AvaloniaFact]
    public void ADragCanBeUndoneAndYouCanBeDraggedToo()
    {
        using var ui = Match();
        var match = ui.ViewModel.Match;

        Drag(ui, Slot(ui, "AllyRow", 3), Slot(ui, "AllyRow", 0));
        Assert.Equal(["wraith", "dynamo", "kelvin", "paradox"], match.Board.AllySlots.Take(4).Select(slot => slot.HeroId));
        Assert.True(match.Board.AllySlots[0].IsSelf);

        match.Board.UndoCommand!.Execute(null);
        UiHarness.Settle();
        Assert.Equal(["dynamo", "kelvin", "paradox", "wraith"], match.Board.AllySlots.Take(4).Select(slot => slot.HeroId));
    }

    [AvaloniaFact]
    public void DroppingOutsideTheRowOrOnAnEmptySlotChangesNothing()
    {
        using var ui = Match();
        var board = ui.ViewModel.Match.Board;

        Drag(ui, Slot(ui, "AllyRow", 0), Slot(ui, "EnemyRow", 0));
        Drag(ui, Slot(ui, "AllyRow", 0), Slot(ui, "AllyRow", 1) + new Vector(0, 300));
        Assert.Equal(["dynamo", "kelvin", "paradox", "wraith"], board.AllySlots.Take(4).Select(slot => slot.HeroId));

        // The empty cells to the right of the last hero aren't places; the last hero is the nearest one.
        Drag(ui, Slot(ui, "AllyRow", 0), Slot(ui, "AllyRow", 5));
        Assert.Equal(["kelvin", "paradox", "wraith", "dynamo"], board.AllySlots.Take(4).Select(slot => slot.HeroId));
    }

    [AvaloniaFact]
    public void APressThatBarelyMovesIsStillAClick()
    {
        using var ui = Match();
        var at = Slot(ui, "AllyRow", 0);

        Drag(ui, at, at + new Vector(2, 1));

        Assert.Equal("dynamo", ui.ViewModel.Match.Match.SelfHero);
        Assert.Equal(["dynamo", "kelvin", "paradox", "wraith"], ui.ViewModel.Match.Board.AllySlots.Take(4).Select(slot => slot.HeroId));
    }
}
