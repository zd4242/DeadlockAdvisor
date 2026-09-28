using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Services.Contracts;

/// <summary>A band off the top of the primary monitor, in physical pixels, and the monitor's full size.</summary>
public sealed record ScreenCapture(RgbImage Band, int ScreenWidth, int ScreenHeight);

public sealed class CaptureException(string message) : Exception(message);

public interface IScreenCaptureService
{
    /// <summary>
    /// Grab the top of the primary monitor as the game shows it, first minimizing this app's window
    /// when <paramref name="minimize"/> so it isn't captured over the game. Throws
    /// <see cref="CaptureException"/> when the screen can't be read.
    /// </summary>
    Task<ScreenCapture> CaptureTopBandAsync(bool minimize);
}
