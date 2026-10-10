using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.ItemCard;

namespace DeadlockAdvisor.Tests.Ui;

/// <summary>
/// Headless popups are drawn in the window's overlay layer, not in a window of their own, so these
/// show that a popup takes its target's zoom, not where a real popup window lands.
/// </summary>
public class PopupZoomTests
{
    private static double ScaleOf(Visual visual) =>
        visual.TransformToVisual((Visual)visual.GetVisualRoot()!)!.Value.M11;

    [AvaloniaTheory]
    [InlineData(2, 1.0)]
    [InlineData(5, 1.5)]
    [InlineData(7, 2.0)]
    public void AFlyoutOpensAtTheAppsZoomAndInsideTheWindow(int zoomIndex, double scale)
    {
        using var ui = new UiHarness(settings => settings.Current.ZoomIndex = zoomIndex);
        ui.Show();
        var more = ui.Window.MatchPage.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MoreButton");
        var menu = (MenuFlyout)more.Flyout!;

        menu.ShowAt(more);
        UiHarness.Settle();

        var presenter = ui.Window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
        Assert.Equal(scale, ScaleOf(presenter), 3);
        var corner = presenter.TranslatePoint(new Point(presenter.Bounds.Width, presenter.Bounds.Height), ui.Window)!.Value;
        var origin = presenter.TranslatePoint(default, ui.Window)!.Value;
        Assert.True(origin.X >= 0 && origin.Y >= 0);
        Assert.True(corner.X <= ui.Window.ClientSize.Width && corner.Y <= ui.Window.ClientSize.Height);
        ui.Screenshot($"flyout_zoom_{zoomIndex}.png");
        menu.Hide();
        UiHarness.Settle();
    }

    [AvaloniaFact]
    public void ATooltipOpensAtTheAppsZoom()
    {
        using var ui = new UiHarness(settings => settings.Current.ZoomIndex = 5);
        ui.Show();
        var target = ui.Window.GetVisualDescendants().OfType<Control>()
            .First(control => control.IsEffectivelyVisible && ToolTip.GetTip(control) is string);

        ToolTip.SetIsOpen(target, true);
        UiHarness.Settle();

        var tip = ui.Window.GetVisualDescendants().OfType<ToolTip>().Single();
        Assert.Equal(1.5, ScaleOf(tip), 3);
        ToolTip.SetIsOpen(target, false);
        UiHarness.Settle();
    }

    [AvaloniaFact]
    public async Task TheItemCardIsScaledOnceNotTwice()
    {
        using var ui = new UiHarness(settings => settings.Current.ZoomIndex = 5);
        MatchPageTests.SetUpMatch(ui);
        ui.Show();
        var icon = ui.Window.GetVisualDescendants().OfType<Controls.Art.ArtImage>()
            .First(image => ItemCardHover.GetItemId(image) is not null && image.IsEffectivelyVisible);

        ui.Window.MouseMove(icon.TranslatePoint(new Point(17, 17), ui.Window)!.Value);
        Assert.True(await UiHarness.WaitUntilAsync(() => ui.Window.ItemCards.ShownItemId is not null));

        var card = ui.Window.GetVisualDescendants().OfType<Control>().First(control => control.Width == ItemCardBuilder.Width);
        Assert.Equal(ZoomLevels.Steps[5], ScaleOf(card), 3);
        ui.Window.ItemCards.Hide();
        UiHarness.Settle();
    }
}
