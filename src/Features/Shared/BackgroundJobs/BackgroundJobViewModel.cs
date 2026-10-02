using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Shared.BackgroundJobs;

public enum BackgroundJobState
{
    Running,
    Cancelling,
    Succeeded,
    Failed,
}

/// <summary>
/// A long network job running behind the window, shown in the status bar: how far along it is, about
/// how long it has left, and a way to stop it. Once it ends it stays there as its result, to open or
/// dismiss. Reports arrive on the UI thread.
/// </summary>
public sealed class BackgroundJobViewModel : ViewModelBase, IProgress<FetchProgress>
{
    // The first few steps say little about the pace, so there's no estimate until the job has run this long.
    private static readonly TimeSpan _estimateAfter = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _cancel = new();
    private readonly Subject<Unit> _dismissed = new();
    private readonly IScheduler _clock;
    private readonly DateTimeOffset _started;
    private (int Done, DateTimeOffset At)? _paceFrom;
    private Action? _open;

    /// <param name="confirmCancel">Given the cancel, asks before calling it. Without it, Cancel stops the job at once.</param>
    public BackgroundJobViewModel(string title, IScheduler clock, Action<Action>? confirmCancel = null)
    {
        Title = title;
        _clock = clock;
        _started = clock.Now;

        CancelCommand = ReactiveCommand.Create(() => (confirmCancel ?? (cancel => cancel()))(Cancel),
            this.WhenAnyValue(vm => vm.State, state => state == BackgroundJobState.Running));
        var finished = this.WhenAnyValue(vm => vm.IsFinished);
        OpenCommand = ReactiveCommand.Create(() =>
        {
            _open?.Invoke();
            _dismissed.OnNext(Unit.Default);
        }, finished);
        DismissCommand = ReactiveCommand.Create(() => _dismissed.OnNext(Unit.Default), finished);

        this.WhenAnyValue(vm => vm.State)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsRunning));
                this.RaisePropertyChanged(nameof(IsFinished));
                this.RaisePropertyChanged(nameof(HasFailed));
            })
            .DisposeWith(Disposables);
    }

    /// <summary>"Match data", "Art": what the status bar calls it.</summary>
    public string Title { get; }

    [Reactive] public BackgroundJobState State { get; private set; }
    public bool IsRunning => State is BackgroundJobState.Running or BackgroundJobState.Cancelling;
    public bool IsFinished => !IsRunning;
    public bool HasFailed => State == BackgroundJobState.Failed;

    /// <summary>What it's fetching now.</summary>
    [Reactive] public string Detail { get; private set; } = "";

    [Reactive] public double Done { get; private set; }
    [Reactive] public double Total { get; private set; } = 1;

    /// <summary>Until the first report, when there's nothing to count yet.</summary>
    [Reactive] public bool IsIndeterminate { get; private set; } = true;

    /// <summary>"34% · about 12 min left" while it runs, then how it ended.</summary>
    [Reactive] public string StatusText { get; private set; } = "starting…";

    [Reactive] public string ToolTipText { get; private set; } = "Starting…";

    public CancellationToken Token => _cancel.Token;

    /// <summary>Stop the job, after asking if it was set up to ask.</summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Show the finished job's report, and take it out of the status bar.</summary>
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    /// <summary>Take the finished job out of the status bar unread.</summary>
    public ReactiveCommand<Unit, Unit> DismissCommand { get; }

    /// <summary>Opened or dismissed: whoever shows it can let it go.</summary>
    public IObservable<Unit> Dismissed => _dismissed.AsObservable();

    public void Report(FetchProgress value)
    {
        if (State != BackgroundJobState.Running)
            return;

        var now = _clock.Now;
        _paceFrom ??= (value.Done, now);
        Total = Math.Max(1, value.Total);
        Done = Math.Min(value.Done, Total);
        Detail = value.Text;
        IsIndeterminate = false;

        var percent = $"{Math.Floor(Done * 100 / Total):0}%";
        StatusText = Remaining(value.Done, value.Total, now) is { } left ? $"{percent} · {DescribeRemaining(left)}" : percent;
        ToolTipText = $"{Detail}\n{Format.Thousands(value.Done)} of {Format.Thousands(value.Total)} · {DescribeDuration(now - _started)} so far";
    }

    /// <summary>Stop before the next step; the call in flight is dropped. Does nothing once it's stopping or over.</summary>
    public void Cancel()
    {
        if (State != BackgroundJobState.Running)
            return;
        State = BackgroundJobState.Cancelling;
        StatusText = "stopping…";
        ToolTipText = "Stopping…";
        _cancel.Cancel();
    }

    /// <param name="status">How it ended, in a few words: "fetched", "12 downloaded".</param>
    /// <param name="open">Shows the full report.</param>
    public void Succeed(string status, Action open) => Finish(BackgroundJobState.Succeeded, status, open, "Click for the full report.");

    /// <param name="open">Shows what went wrong.</param>
    public void Fail(Action open) => Finish(BackgroundJobState.Failed, "failed", open, "Click to see what went wrong.");

    private void Finish(BackgroundJobState state, string status, Action open, string toolTip)
    {
        _open = open;
        Done = Total;
        IsIndeterminate = false;
        StatusText = status;
        ToolTipText = toolTip;
        State = state;
    }

    /// <summary>At the pace since the first report, which leaves out the setup before any counting starts.</summary>
    private TimeSpan? Remaining(int done, int total, DateTimeOffset now)
    {
        if (_paceFrom is not { } from)
            return null;
        var steps = done - from.Done;
        var spent = now - from.At;
        if (steps <= 0 || spent < _estimateAfter)
            return null;
        return spent / steps * Math.Max(0, total - done);
    }

    internal static string DescribeRemaining(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1))
            return "under a minute left";
        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes < 60 ? $"about {minutes} min left" : $"about {minutes / 60} h {minutes % 60:00} min left";
    }

    internal static string DescribeDuration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return $"{(int)span.TotalSeconds} s";
        var minutes = (int)span.TotalMinutes;
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancel.Dispose();
            _dismissed.Dispose();
        }
        base.Dispose(disposing);
    }
}
