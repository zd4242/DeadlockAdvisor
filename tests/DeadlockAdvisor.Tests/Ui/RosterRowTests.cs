using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>The match bar's portraits as the window is resized.</summary>
public class RosterRowTests
{
    private static Rect PortraitIn(Visual root, RosterSlot slot) =>
        slot.PortraitRect.Translate(new Vector(slot.TranslatePoint(default, root)!.Value.X, 0));

    /// <summary>
    /// A row once took whole-cell widths, so its outer portrait crept inward as the window widened and
    /// then jumped back out by a dozen pixels. The outermost edges now follow the window a pixel at a time.
    /// </summary>
    [AvaloniaFact]
    public void TheOuterPortraitsFollowTheWindowWithoutJumping()
    {
        using var ui = new UiHarness();
        var board = ui.ViewModel.Match.Board;
        foreach (var hero in new[] { "haze", "infernus", "vindicta", "abrams", "lash", "seven" })
            board.SetRole(hero, Role.Enemy);
        foreach (var hero in new[] { "dynamo", "kelvin", "paradox", "shiv", "yamato" })
            board.SetRole(hero, Role.Ally);
        board.SetRole("wraith", Role.Self);
        ui.Show();

        double? left = null, right = null, size = null;
        for (var width = ui.Window.MinWidth; width <= 1500; width++)
        {
            ui.Window.Width = width;
            UiHarness.Settle();
            var slots = ui.Window.GetVisualDescendants().OfType<RosterSlot>().ToList();
            var outerLeft = PortraitIn(ui.Window, slots[0]);
            var outerRight = PortraitIn(ui.Window, slots[^1]);

            if (left is not null)
            {
                Assert.True(Math.Abs(outerLeft.Left - left.Value) <= 2, $"The left edge jumped at {width}: {left} to {outerLeft.Left}");
                Assert.True(Math.Abs(outerRight.Right - right!.Value) <= 2, $"The right edge jumped at {width}: {right} to {outerRight.Right}");
                Assert.True(outerLeft.Width >= size, $"The portraits shrank at {width}: {size} to {outerLeft.Width}");
            }
            (left, right, size) = (outerLeft.Left, outerRight.Right, outerLeft.Width);
        }
    }
}
