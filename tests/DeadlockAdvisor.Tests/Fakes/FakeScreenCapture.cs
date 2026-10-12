using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests.Fakes;

/// <summary>No screen in tests: a capture fails unless a test hands it an image.</summary>
public sealed class FakeScreenCapture : IScreenCaptureService
{
    public ScreenCapture? Next { get; set; }
    public int Captures { get; private set; }

    /// <summary>Whether the last capture asked for the window to be minimized first.</summary>
    public bool? Minimized { get; private set; }

    public Task<ScreenCapture> CaptureTopBandAsync(bool minimize)
    {
        Captures++;
        Minimized = minimize;
        return Next is { } capture ? Task.FromResult(capture) : throw new CaptureException("No screen in tests.");
    }

    /// <summary>The fractions asked for by taller captures, which never minimize anything.</summary>
    public List<double> Tall { get; } = [];

    /// <summary>Captures handed out, one per taller capture, before <see cref="Next"/> is used again.</summary>
    public Queue<ScreenCapture> Sequence { get; } = [];

    /// <summary>Run after each taller capture was taken, as the player might let go of a key in between.</summary>
    public Action? Captured { get; set; }

    public Task<ScreenCapture> CaptureTopAsync(double fraction)
    {
        Captures++;
        Tall.Add(fraction);
        var capture = Sequence.Count > 0 ? Sequence.Dequeue() : Next;
        Captured?.Invoke();
        return capture is not null ? Task.FromResult(capture) : throw new CaptureException("No screen in tests.");
    }
}
