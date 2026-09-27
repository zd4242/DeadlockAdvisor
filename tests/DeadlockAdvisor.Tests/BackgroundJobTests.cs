using System.Reactive.Concurrency;
using System.Reactive.Linq;
using DeadlockAdvisor.Features.Shared.BackgroundJobs;
using DeadlockAdvisor.Services;

namespace DeadlockAdvisor.Tests;

public sealed class BackgroundJobTests : IDisposable
{
    private readonly HistoricalScheduler _clock = new();
    private readonly BackgroundJobViewModel _job;

    public BackgroundJobTests() => _job = new BackgroundJobViewModel("Match stats", _clock);

    public void Dispose() => _job.Dispose();

    [Fact]
    public void EstimatesTimeLeftFromThePaceSinceTheFirstStep()
    {
        Assert.True(_job.IsIndeterminate);
        Assert.Equal("starting…", _job.StatusText);

        // Setup before the first step doesn't count against the pace.
        _clock.AdvanceBy(TimeSpan.FromSeconds(30));
        _job.Report(new FetchProgress(0, 2600, "as/full: all matches"));
        Assert.False(_job.IsIndeterminate);
        Assert.Equal("0%", _job.StatusText);

        _clock.AdvanceBy(TimeSpan.FromSeconds(2));
        _job.Report(new FetchProgress(4, 2600, "as/full: all matches · Initiate"));
        Assert.Equal("0%", _job.StatusText);

        _clock.AdvanceBy(TimeSpan.FromSeconds(118));
        _job.Report(new FetchProgress(200, 2600, "as/full: Haze"));
        Assert.Equal("7% · about 24 min left", _job.StatusText);
        Assert.Equal("as/full: Haze\n200 of 2,600 · 2 min so far", _job.ToolTipText);
        Assert.Equal((200.0, 2600.0), (_job.Done, _job.Total));
    }

    [Theory]
    [InlineData(59, "under a minute left")]
    [InlineData(61, "about 2 min left")]
    [InlineData(3600, "about 1 h 00 min left")]
    [InlineData(5460, "about 1 h 31 min left")]
    public void TimeLeftReadsInWholeMinutes(int seconds, string expected) =>
        Assert.Equal(expected, BackgroundJobViewModel.DescribeRemaining(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void CancellingStopsReportsAndCanOnlyHappenOnce()
    {
        _job.Report(new FetchProgress(10, 100, "Hero portraits: Haze"));

        _job.CancelCommand.Execute().Subscribe();

        Assert.True(_job.Token.IsCancellationRequested);
        Assert.Equal(BackgroundJobState.Cancelling, _job.State);
        Assert.True(_job.IsRunning);
        Assert.Equal("stopping…", _job.StatusText);
        Assert.False(_job.CancelCommand.CanExecute.FirstAsync().Wait());
        _job.Report(new FetchProgress(20, 100, "Hero portraits: Vindicta"));
        Assert.Equal(10, _job.Done);
    }

    [Fact]
    public void ACancelThatAsksWaitsForTheAnswer()
    {
        Action? stop = null;
        using var job = new BackgroundJobViewModel("Match stats", _clock, cancel => stop = cancel);

        job.CancelCommand.Execute().Subscribe();
        Assert.False(job.Token.IsCancellationRequested);

        stop!();
        Assert.True(job.Token.IsCancellationRequested);
    }

    [Fact]
    public void AFinishedJobOpensItsReportOnceAndThenLeaves()
    {
        var opened = 0;
        var dismissed = 0;
        _job.Dismissed.Subscribe(_ => dismissed++);
        Assert.False(_job.OpenCommand.CanExecute.FirstAsync().Wait());

        _job.Succeed("fetched", () => opened++);

        Assert.True(_job.IsFinished);
        Assert.False(_job.HasFailed);
        Assert.Equal("fetched", _job.StatusText);
        Assert.Equal(_job.Total, _job.Done);
        _job.OpenCommand.Execute().Subscribe();
        Assert.Equal((1, 1), (opened, dismissed));
    }

    [Fact]
    public void AFailedJobSaysSo()
    {
        _job.Fail(() => { });

        Assert.True(_job.HasFailed);
        Assert.Equal("failed", _job.StatusText);
        Assert.Equal("Click to see what went wrong.", _job.ToolTipText);
        Assert.False(_job.CancelCommand.CanExecute.FirstAsync().Wait());
    }
}
