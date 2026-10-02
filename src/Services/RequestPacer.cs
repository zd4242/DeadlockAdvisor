using System.Threading;

namespace DeadlockAdvisor.Services;

/// <summary>
/// Runs a batch of requests no closer together than a gap, a few at once so a slow answer doesn't hold
/// the next one up, and holds every start back while the API has asked to slow down.
/// </summary>
public sealed class RequestPacer
{
    private readonly TimeSpan _gap;
    private readonly int _maxInFlight;
    private readonly Func<TimeSpan> _elapsed;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _lock = new();
    private TimeSpan? _lastStart;
    private TimeSpan _pausedUntil;

    /// <param name="elapsed">A clock that only goes forward.</param>
    public RequestPacer(TimeSpan gap, int maxInFlight, Func<TimeSpan> elapsed, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _gap = gap;
        _maxInFlight = maxInFlight;
        _elapsed = elapsed;
        _delay = delay;
    }

    /// <summary>
    /// Start each request in order, once its turn comes, and return the answers in the same order. The
    /// first failure stops the requests still running and is rethrown.
    /// </summary>
    public async Task<T[]> RunAsync<T>(IReadOnlyList<Func<CancellationToken, Task<T>>> requests, CancellationToken cancellationToken)
    {
        var results = new T[requests.Count];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new List<Task>();
        try
        {
            for (var i = 0; i < requests.Count; i++)
            {
                while (inFlight.Count >= _maxInFlight)
                    await TakeFinishedAsync(inFlight);
                await WaitTurnAsync(stop.Token);
                // One that failed while this waited stops it here, before another starts.
                foreach (var finished in inFlight.Where(task => task.IsCompleted).ToList())
                {
                    inFlight.Remove(finished);
                    await finished;
                }
                var index = i;
                inFlight.Add(Run(index));
            }
            while (inFlight.Count > 0)
                await TakeFinishedAsync(inFlight);
        }
        catch
        {
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(inFlight);
            }
            catch (Exception)
            {
                // The others only stopped because of the failure being rethrown.
            }
            throw;
        }
        return results;

        async Task Run(int index) => results[index] = await requests[index](stop.Token);
    }

    /// <summary>Hold every start back for <paramref name="wait"/> from now, as the API asks with a 429.</summary>
    public void Pause(TimeSpan wait)
    {
        lock (_lock)
        {
            var until = _elapsed() + wait;
            if (until > _pausedUntil)
                _pausedUntil = until;
        }
    }

    private static async Task TakeFinishedAsync(List<Task> inFlight)
    {
        var finished = await Task.WhenAny(inFlight);
        inFlight.Remove(finished);
        await finished;
    }

    private async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_lock)
        {
            var next = _lastStart is { } last ? last + _gap : TimeSpan.Zero;
            if (_pausedUntil > next)
                next = _pausedUntil;
            wait = next - _elapsed();
        }
        if (wait > TimeSpan.Zero)
            await _delay(wait, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
            _lastStart = _elapsed();
    }
}
