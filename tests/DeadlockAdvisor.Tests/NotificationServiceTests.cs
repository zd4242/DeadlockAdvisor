using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Tests.Fakes;

namespace DeadlockAdvisor.Tests;

public class NotificationServiceTests
{
    private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private NotificationService Service() => new(new FakeLoggingService(), () => _now);

    [Fact]
    public void MessagesSentBeforeTheFirstSubscriberArriveOnceInOrder()
    {
        var service = Service();
        service.ShowInformation("one");
        service.ShowError("two");

        var seen = new List<string>();
        using var _ = service.Notifications.Subscribe(n => seen.Add(n.Message));
        service.ShowSuccess("three");

        Assert.Equal(["one", "two", "three"], seen);
    }

    [Fact]
    public void ALaterSubscriberOnlyGetsNewMessages()
    {
        var service = Service();
        service.ShowInformation("early");
        using var first = service.Notifications.Subscribe(_ => { });

        var seen = new List<string>();
        using var second = service.Notifications.Subscribe(n => seen.Add(n.Message));
        service.ShowInformation("later");

        Assert.Equal(["later"], seen);
    }

    [Fact]
    public void OnlyTheNewestMessagesAreHeld()
    {
        var service = Service();
        for (var i = 1; i <= NotificationService.MaxHeld + 3; i++)
            service.ShowInformation($"m{i}");

        var seen = new List<string>();
        using var _ = service.Notifications.Subscribe(n => seen.Add(n.Message));

        Assert.Equal(NotificationService.MaxHeld, seen.Count);
        Assert.Equal("m4", seen[0]);
        Assert.Equal($"m{NotificationService.MaxHeld + 3}", seen[^1]);
    }

    [Fact]
    public void AMessageThatWaitedTooLongIsDropped()
    {
        var service = Service();
        service.ShowInformation("stale");
        _now += NotificationService.MaxHeldAge + TimeSpan.FromSeconds(1);
        service.ShowInformation("fresh");

        var seen = new List<string>();
        using var _ = service.Notifications.Subscribe(n => seen.Add(n.Message));

        Assert.Equal(["fresh"], seen);
    }

    [Fact]
    public void AnUnsubscribedFirstListenerDoesNotBringBackOldMessages()
    {
        var service = Service();
        service.ShowInformation("early");
        service.Notifications.Subscribe(_ => { }).Dispose();

        var seen = new List<string>();
        using var _ = service.Notifications.Subscribe(n => seen.Add(n.Message));

        Assert.Empty(seen);
    }
}
