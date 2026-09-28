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
}
