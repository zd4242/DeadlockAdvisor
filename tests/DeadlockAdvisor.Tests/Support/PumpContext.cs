using System.Collections.Concurrent;
using System.Threading;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>
/// A stand-in for the UI thread. What an <c>await</c> started under it comes back to is queued, and runs only when the test
/// pumps it on its own thread, so a test can tell work that ran away from the caller (its continuation is posted, from
/// another thread) from work that ran inline (nothing is posted), and can act between two steps.
/// </summary>
public sealed class PumpContext : SynchronizationContext
{
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(30);
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly List<int> _postedBy = [];

    /// <summary>The managed thread that posted each continuation so far.</summary>
    public IReadOnlyList<int> PostedBy
    {
        get
        {
            lock (_postedBy)
                return [.. _postedBy];
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_postedBy)
            _postedBy.Add(Environment.CurrentManagedThreadId);
        _queue.Add((d, state));
    }

    /// <summary>Calls <paramref name="start"/> with this as the current context, then puts the old one back.</summary>
    public TTask Start<TTask>(Func<TTask> start) where TTask : Task
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            return start();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>Runs the next continuation, waiting for it to be posted.</summary>
    public void RunNext()
    {
        if (!_queue.TryTake(out var next, _patience))
            throw new TimeoutException("Nothing was posted to the pump.");
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            next.Callback(next.State);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>Runs continuations until <paramref name="task"/> is done, and throws what it threw.</summary>
    public void Finish(Task task)
    {
        while (!task.IsCompleted)
            RunNext();
        task.GetAwaiter().GetResult();
    }
}
