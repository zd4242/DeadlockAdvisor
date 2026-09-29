using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using DeadlockAdvisor.Behaviors;

namespace DeadlockAdvisor.Tests.Ui;

public class AutoScrollTests
{
    private static (UiHarness Ui, ScrollViewer Scroller, Point Anchor) OnHeroTraits()
    {
        var ui = new UiHarness(settings =>
        {
            UiHarness.Editing(settings, 1);
            settings.Current.ArtDownloadOffered = true;
        });
        ui.Show();
        var scroller = ui.Window.HeroTraitsPage.Scroller;
        var anchor = scroller.TranslatePoint(new Point(400, 300), ui.Window)!.Value;
        return (ui, scroller, anchor);
    }

    [Fact]
    public void SpeedGrowsWithDistancePastTheDeadZone()
    {
        Assert.Equal(0, MiddleClickAutoScroll.Speed(10, 12));
        Assert.Equal(-(8 * 4 + 64 * 0.04), MiddleClickAutoScroll.Speed(-20, 12), 9);
        Assert.True(MiddleClickAutoScroll.Speed(200, 12) > 10 * MiddleClickAutoScroll.Speed(30, 12));
    }

    [AvaloniaFact]
    public async Task AMiddleClickKeepsScrollingTowardTheCursorUntilTheNextClick()
    {
        var (ui, scroller, anchor) = OnHeroTraits();
        using var _ = ui;

        ui.Window.MouseDown(anchor, MouseButton.Middle);
        ui.Window.MouseUp(anchor, MouseButton.Middle);
        Assert.True(ui.Window.AutoScroll.IsScrolling);

        ui.Window.MouseMove(anchor + new Point(0, 150));
        Assert.True(await UiHarness.WaitUntilAsync(() => scroller.Offset.Y > 20));

        ui.Window.MouseDown(anchor + new Point(0, 150), MouseButton.Left);
        ui.Window.MouseUp(anchor + new Point(0, 150), MouseButton.Left);
        Assert.False(ui.Window.AutoScroll.IsScrolling);
        var stopped = scroller.Offset.Y;
        await Task.Delay(100);
        UiHarness.Settle();
        Assert.Equal(stopped, scroller.Offset.Y);
        // The click that stopped it didn't also pick a cell.
        Assert.Equal((0, 0), (ui.ViewModel.HeroTraits.CurrentRow, ui.ViewModel.HeroTraits.CurrentColumn));
    }

    [AvaloniaFact]
    public async Task HoldingAndSteeringStopsOnRelease()
    {
        var (ui, scroller, anchor) = OnHeroTraits();
        using var _ = ui;

        ui.Window.MouseDown(anchor, MouseButton.Middle);
        ui.Window.MouseMove(anchor + new Point(0, 120), RawInputModifiers.MiddleMouseButton);
        Assert.True(await UiHarness.WaitUntilAsync(() => scroller.Offset.Y > 20));
        ui.Window.MouseUp(anchor + new Point(0, 120), MouseButton.Middle);

        Assert.False(ui.Window.AutoScroll.IsScrolling);
    }

    [AvaloniaFact]
    public void AKeyOrTheWheelStopsIt()
    {
        var (ui, _, anchor) = OnHeroTraits();
        using var __ = ui;

        ui.Window.MouseDown(anchor, MouseButton.Middle);
        ui.Window.MouseUp(anchor, MouseButton.Middle);
        ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(ui.Window.AutoScroll.IsScrolling);

        ui.Window.MouseDown(anchor, MouseButton.Middle);
        ui.Window.MouseUp(anchor, MouseButton.Middle);
        ui.Window.MouseWheel(anchor, new Vector(0, -1));
        Assert.False(ui.Window.AutoScroll.IsScrolling);
    }
}
