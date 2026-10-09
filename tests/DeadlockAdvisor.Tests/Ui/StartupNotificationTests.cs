using Avalonia.Headless.XUnit;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DeadlockAdvisor.Tests.Ui;

public class StartupNotificationTests
{
    /// <summary>The data folder is loaded before the window exists, so what it says then has to wait for the overlay.</summary>
    [AvaloniaFact]
    public async Task AMessageSentBeforeTheWindowExistsIsShownWhenItAppears()
    {
        var notifications = new NotificationService(new FakeLoggingService());
        notifications.ShowWarning("Couldn't load the data folder; using the default one instead", TimeSpan.FromMinutes(5));

        using var ui = new UiHarness(overrides: services => services.AddSingleton<INotificationService>(notifications));
        ui.Show();

        var shown = await UiHarness.WaitUntilAsync(() =>
            ui.ViewModel.NotificationOverlay.Notifications.Any(n => n.Message.StartsWith("Couldn't load the data folder")));
        Assert.True(shown);
    }
}
