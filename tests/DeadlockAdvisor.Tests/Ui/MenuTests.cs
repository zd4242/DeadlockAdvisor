using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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
