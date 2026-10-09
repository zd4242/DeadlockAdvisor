using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeadlockAdvisor.Features.Shared.Modals.Message;
using DeadlockAdvisor.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class NotificationOverlayTests
{
    [AvaloniaFact]
    public async Task ClickingAToastTakesItAwayAndItStaysInTheRecentMessages()
    {
        using var ui = new UiHarness();
        ui.Show();
        var notifications = ui.Services.GetRequiredService<INotificationService>();

        notifications.ShowError("Writing to the data folder failed");
        UiHarness.Settle();
        var toast = ui.Window.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("notification"));
        // Slid in, for the click and the screenshot.
        Assert.True(await UiHarness.WaitUntilAsync(() => toast.Opacity >= 1));
        ui.Screenshot("notification_error.png");

        var at = toast.TranslatePoint(new Point(toast.Bounds.Width / 2, toast.Bounds.Height / 2), ui.Window)!.Value;
        ui.Window.MouseDown(at, MouseButton.Left);
        ui.Window.MouseUp(at, MouseButton.Left);
        UiHarness.Settle();

        Assert.Empty(ui.ViewModel.NotificationOverlay.Notifications);
        Assert.Single(notifications.Recent, message => message.Message == "Writing to the data folder failed");
    }

    [AvaloniaFact]
    public void RecentMessagesListsTheLatestFirstWithTheirTime()
    {
        using var ui = new UiHarness();
        var shown = new List<object>();
        using var watch = ui.Services.GetRequiredService<IModalService>().ShowModalObservable.Subscribe(shown.Add);
        ui.Show();
        var notifications = ui.Services.GetRequiredService<INotificationService>();
        notifications.ShowInformation("First");
        notifications.ShowError("Second");

        ui.ViewModel.RecentMessagesCommand.Execute().Subscribe();
        UiHarness.Settle();

        var dialog = Assert.IsType<MessageModalViewModel>(Assert.Single(shown));
        Assert.Equal("Recent messages", dialog.Title);
        var lines = dialog.Body.Split('\n');
        Assert.Matches(@"^\d\d:\d\d  Error  Second$", lines[0]);
        Assert.Matches(@"^\d\d:\d\d  Info  First$", lines[1]);
    }

    [AvaloniaFact]
    public void RecentMessagesSaysSoWhenThereAreNone()
    {
        using var ui = new UiHarness();
        var shown = new List<object>();
        using var watch = ui.Services.GetRequiredService<IModalService>().ShowModalObservable.Subscribe(shown.Add);
        ui.Show();

        ui.ViewModel.RecentMessagesCommand.Execute().Subscribe();
        UiHarness.Settle();

        Assert.Equal("No messages yet.", Assert.IsType<MessageModalViewModel>(Assert.Single(shown)).Body);
    }
}
