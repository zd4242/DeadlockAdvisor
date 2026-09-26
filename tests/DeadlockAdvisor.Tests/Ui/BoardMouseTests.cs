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

    private static void Click(Window window, Point at, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
        }
        UiHarness.Settle();
    }

    private static HeroTile Tile(UiHarness ui, string heroId) =>
        ui.Window.GetVisualDescendants().OfType<HeroTile>().First(tile => tile.HeroId == heroId);

    /// <summary>A roster slot's portrait (or its ×), placed as SlotStrip lays them out.</summary>
    private static Point Slot(UiHarness ui, string strip, int index, bool remove = false)
    {
        var control = ui.Window.GetVisualDescendants().OfType<SlotStrip>().Single(s => s.Name == strip);
        var fit = Math.Floor((control.Bounds.Width - 2 - 8 * 5) / 6);
        var portrait = Math.Clamp(fit - fit % 2, 36, 80);
        var left = 1 + index * (portrait + 8);
        var local = remove ? new Point(left + portrait - 12, 12) : new Point(left + portrait / 2, portrait / 2);
        return control.TranslatePoint(local, ui.Window)!.Value;
    }

    [AvaloniaFact]
    public async Task TilesSlotsAndResultsRespondToTheMouse()
    {
        using var ui = new UiHarness();
        ui.Show();
        var match = ui.ViewModel.Match;
        var board = match.Board;

        // An empty ally slot picks "You" while you're unset.
        Click(ui.Window, Slot(ui, "AllyStrip", 0));
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

        // Clicking a roster portrait toggles lane; its × removes.
        Click(ui.Window, Slot(ui, "EnemyStrip", 0));
        Assert.True(board.IsInLane("haze"));
        Click(ui.Window, Slot(ui, "EnemyStrip", 0, remove: true));
        Assert.Equal(Role.None, board.RoleOf("haze"));

        // Clicking a recommendation explains it.
        Click(ui.Window, Center(Tile(ui, "haze"), ui.Window));
        match.ResultsTab = 1;
        UiHarness.Settle();
        var row = ui.Window.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("result") && border.IsEffectivelyVisible);
        Click(ui.Window, Center(row, ui.Window));
        Assert.True(match.Explain.HasItem);
        Assert.Equal(["wraith", "haze"], ui.Settings.Current.LastMatch!.Roles.Keys);
    }
}
