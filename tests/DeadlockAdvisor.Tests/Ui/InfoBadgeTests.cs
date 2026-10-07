using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Controls;
using DeadlockAdvisor.Features.Match.Board;

namespace DeadlockAdvisor.Tests.Ui;

public class InfoBadgeTests
{
    private static (UiHarness Ui, InfoBadge Badge, Point Center) OnTheMatchBar()
    {
        var ui = new UiHarness();
        ui.Show();
        var badge = ui.Window.MatchPage.GetVisualDescendants().OfType<RosterView>().Single()
            .GetVisualDescendants().OfType<InfoBadge>().Single();
        return (ui, badge, badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), ui.Window)!.Value);
    }

    private static void Click(UiHarness ui, Point point)
    {
        ui.Window.MouseDown(point, MouseButton.Left);
        ui.Window.MouseUp(point, MouseButton.Left);
        UiHarness.Settle();
    }

    [AvaloniaFact]
    public void ClickingTheBadgeHoldsItsTextOpenUntilTheNextClickElsewhere()
    {
        var (ui, badge, center) = OnTheMatchBar();
        using var _ = ui;
        Assert.False(badge.IsPinned);

        Click(ui, center);
        Assert.True(badge.IsPinned);

        Click(ui, center + new Point(400, 300));
        Assert.False(badge.IsPinned);
    }

    [AvaloniaFact]
    public void ClickingTheBadgeAgainPutsItsTextAway()
    {
        var (ui, badge, center) = OnTheMatchBar();
        using var _ = ui;

        Click(ui, center);
        Assert.True(badge.IsPinned);

        Click(ui, center);
        Assert.False(badge.IsPinned);

        Thread.Sleep(300);
        Click(ui, center);
        Assert.True(badge.IsPinned);
    }
}
