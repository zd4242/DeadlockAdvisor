using System.Reactive.Concurrency;
using DeadlockAdvisor.Features.MainWindow;

namespace DeadlockAdvisor.Tests;

public sealed class UpdateSchedulerTests
{
    private readonly HistoricalScheduler _clock = new();
    private int _passes;

    private UpdateScheduler Scheduler(Func<double>? jitter = null) => new(_clock, jitter ?? (() => 0.5), () => _passes++);

    [Fact]
    public void ItDoesNothingUntilStartedAndThenPassesOnceEverySixHours()
    {
        using var scheduler = Scheduler();
        _clock.AdvanceBy(TimeSpan.FromDays(1));
        Assert.Equal(0, _passes);

        scheduler.Start();
        _clock.AdvanceBy(UpdateScheduler.Interval - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _passes);
        _clock.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _passes);
        _clock.AdvanceBy(UpdateScheduler.Interval);
        Assert.Equal(2, _passes);
    }

    [Fact]
    public void EachTickStraysFromTheIntervalByItsOwnJitterButNeverByMoreThanTenPercent()
    {
        var jitters = new Queue<double>([0, 1, 0.5, 0.5]);
        using var scheduler = Scheduler(jitters.Dequeue);

        scheduler.Start();
        _clock.AdvanceBy(UpdateScheduler.Interval * 0.9);
        Assert.Equal(1, _passes);

        _clock.AdvanceBy(UpdateScheduler.Interval * 1.1 - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _passes);
        _clock.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _passes);

        _clock.AdvanceBy(UpdateScheduler.Interval);
        Assert.Equal(3, _passes);
    }

    [Fact]
    public void ItKeepsTheIntervalWhateverTheJitterSays()
    {
        using var scheduler = Scheduler(() => 7);
        scheduler.Start();

        _clock.AdvanceBy(UpdateScheduler.Interval * 1.1 - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _passes);
        _clock.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _passes);
    }

    [Fact]
    public void ItStopsWhenDisposed()
    {
        using var scheduler = Scheduler();
        scheduler.Start();
        scheduler.Dispose();

        _clock.AdvanceBy(TimeSpan.FromDays(1));
        scheduler.Start();
        _clock.AdvanceBy(TimeSpan.FromDays(1));

        Assert.Equal(0, _passes);
    }

}
