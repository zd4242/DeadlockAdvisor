using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>The Match board driven with the mouse, through the real controls.</summary>
public class BoardMouseTests
{
    private static Point Center(Visual target, Visual root) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)!.Value;

    private static void Click(Window window, Point at, int times = 1, MouseButton button = MouseButton.Left)
    {
        for (var i = 0; i < times; i++)
        {
            window.MouseDown(at, button);
            window.MouseUp(at, button);
        }
        UiHarness.Settle();
    }

    private static HeroTile Tile(UiHarness ui, string heroId) =>
        ui.Window.GetVisualDescendants().OfType<HeroTile>().First(tile => tile.HeroId == heroId);

    /// <summary>A match bar slot (or its ×) on one team's row.</summary>
    private static Point Slot(UiHarness ui, string row, int index, bool remove = false)
    {
        var items = ui.Window.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == row);
        var slot = items.GetVisualDescendants().OfType<RosterSlot>().ElementAt(index);
        return remove ? slot.TranslatePoint(slot.RemoveBounds.Center, ui.Window)!.Value : Center(slot, ui.Window);
    }

    [AvaloniaFact]
    public async Task TilesSlotsAndResultsRespondToTheMouse()
    {
        using var ui = new UiHarness();
        ui.Show();
        var match = ui.ViewModel.Match;
        var board = match.Board;

        // An empty ally slot picks "You" while you're unset.
        Click(ui.Window, Slot(ui, "AllyRow", 0));
        Assert.Equal(Role.Self, board.Mode);

        // Double-clicking a tile sets you, whatever the mode.
        board.SetMode(Role.Enemy);
        Click(ui.Window, Center(Tile(ui, "wraith"), ui.Window), times: 2);
        Assert.Equal(Role.Self, board.RoleOf("wraith"));

        // A left click assigns the current mode; a second click (not a double-click) clears it.
        Click(ui.Window, Center(Tile(ui, "haze"), ui.Window));
        Assert.Equal(Role.Enemy, board.RoleOf("haze"));
        Click(ui.Window, Center(Tile(ui, "infernus"), ui.Window));
        Assert.Equal(Role.Enemy, board.RoleOf("infernus"));
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Click(ui.Window, Center(Tile(ui, "infernus"), ui.Window));
        Assert.Equal(Role.None, board.RoleOf("infernus"));

        // Right clicking a match bar portrait opens the role menu; its × removes. An empty enemy slot picks Enemy.
        Click(ui.Window, Slot(ui, "EnemyRow", 0), button: MouseButton.Right);
        var setAlly = ui.Window.GetVisualDescendants().OfType<MenuItem>().Single(item => Equals(item.Header, "Set as Ally"));
        var menu = TopLevel.GetTopLevel(setAlly)!;
        menu.MouseDown(Center(setAlly, menu), MouseButton.Left);
        menu.MouseUp(Center(setAlly, menu), MouseButton.Left);
        UiHarness.Settle();
        Assert.Equal(Role.Ally, board.RoleOf("haze"));

        // Clicking a teammate makes them you, and the previous you stays in their place as an ally.
        Click(ui.Window, Slot(ui, "AllyRow", 1));
        Assert.Equal(Role.Self, board.RoleOf("haze"));
        Assert.Equal(Role.Ally, board.RoleOf("wraith"));
        Assert.Equal(["wraith", "haze"], board.AllySlots.Take(2).Select(slot => slot.HeroId));
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Click(ui.Window, Slot(ui, "AllyRow", 0));
        Assert.Equal(Role.Self, board.RoleOf("wraith"));

        // Clicking an enemy makes them you, and the teams trade sides with nobody dropped.
        board.SetRole("lash", Role.Enemy);
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Click(ui.Window, Slot(ui, "EnemyRow", 0));
        Assert.Equal(Role.Self, board.RoleOf("lash"));
        Assert.Equal(["wraith", "haze"], board.EnemySlots.Take(2).Select(slot => slot.HeroId));
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Click(ui.Window, Slot(ui, "EnemyRow", 0));
        Assert.Equal(Role.Self, board.RoleOf("wraith"));
        Assert.Equal(Role.Ally, board.RoleOf("haze"));
        Assert.Equal(Role.Enemy, board.RoleOf("lash"));
        Click(ui.Window, Slot(ui, "EnemyRow", 0, remove: true));

        Click(ui.Window, Slot(ui, "AllyRow", 1, remove: true));
        Assert.Equal(Role.None, board.RoleOf("haze"));
        board.SetMode(Role.Ally);
        Click(ui.Window, Slot(ui, "EnemyRow", 0));
        Assert.Equal(Role.Enemy, board.Mode);

        // Clicking a recommendation explains it.
        Click(ui.Window, Center(Tile(ui, "haze"), ui.Window));
        UiHarness.Settle();
        var row = ui.Window.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("result") && border.IsEffectivelyVisible);
        Click(ui.Window, Center(row, ui.Window));
        Assert.True(match.Explain.HasItem);
        Assert.Equal(["wraith", "haze"], ui.Settings.Current.LastMatch!.Roles.Keys);
    }
}
