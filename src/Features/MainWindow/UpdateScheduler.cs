using System.Reactive.Concurrency;
using System.Reactive.Disposables;

namespace DeadlockAdvisor.Features.MainWindow;

/// <summary>
/// Runs <c>pass</c> about every <see cref="Interval"/> while the app stays open, so a session left running for days
/// still notices a new patch, newer formulas and a new version. Each tick is scheduled from the one before, with its
/// own jitter, so a tick that comes late after the computer slept doesn't make up for the ones it missed.
/// </summary>
public sealed class UpdateScheduler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>How far a tick may stray from <see cref="Interval"/>, as a fraction of it, so installs don't all ask at once.</summary>
    public const double Spread = 0.1;

    private readonly IScheduler _clock;
    private readonly Func<double> _jitter;
    private readonly Action _pass;
    private readonly SerialDisposable _next = new();
    private bool _disposed;

    /// <param name="jitter">A number from 0 up to 1 (<see cref="Random.NextDouble"/>): 0.5 is a tick on time.</param>
    public UpdateScheduler(IScheduler clock, Func<double> jitter, Action pass)
    {
        _clock = clock;
        _jitter = jitter;
        _pass = pass;
    }

    /// <summary>Begin counting from now; starting again counts afresh.</summary>
    public void Start()
    {
        if (!_disposed)
            _next.Disposable = _clock.Schedule(NextDelay(), Tick);
    }

    public void Dispose()
    {
        _disposed = true;
        _next.Dispose();
    }

    private TimeSpan NextDelay() => Interval * (1 + Spread * (2 * Math.Clamp(_jitter(), 0, 1) - 1));

    private void Tick()
    {
        try
        {
            _pass();
        }
        finally
        {
            Start();
        }
    }
}
