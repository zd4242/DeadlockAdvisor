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

    [Fact]
    public void AnErrorStaysLongerThanOtherMessagesUnlessGivenATime()
    {
        var service = Service();
        var seen = new List<Notification>();
        using var _ = service.Notifications.Subscribe(seen.Add);

        service.ShowInformation("info");
        service.ShowError("error");
        service.ShowError("with a cause", new InvalidOperationException("boom"));
        service.ShowError("brief", TimeSpan.FromSeconds(1));

        Assert.Equal(NotificationService.DefaultDuration, seen[0].Duration);
        Assert.Equal(NotificationService.DefaultErrorDuration, seen[1].Duration);
        Assert.Equal(NotificationService.DefaultErrorDuration, seen[2].Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), seen[3].Duration);
        Assert.True(NotificationService.DefaultErrorDuration >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void RecentKeepsTheLastMessagesWithTheirTime()
    {
        var service = Service();
        service.ShowInformation("before anyone listens");
        using var _ = service.Notifications.Subscribe(_ => { });
        for (var i = 1; i <= NotificationService.MaxRecent; i++)
        {
            _now += TimeSpan.FromMinutes(1);
            service.ShowWarning($"m{i}");
        }

        var recent = service.Recent;
        Assert.Equal(NotificationService.MaxRecent, recent.Count);
        Assert.Equal("m1", recent[0].Message);
        Assert.Equal($"m{NotificationService.MaxRecent}", recent[^1].Message);
        Assert.Equal(_now, recent[^1].At);
        Assert.Equal(NotificationSeverity.Warning, recent[^1].Severity);
    }
}
