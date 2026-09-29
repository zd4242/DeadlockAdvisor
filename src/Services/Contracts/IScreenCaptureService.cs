using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Services.Contracts;

/// <summary>
/// A band off the top of the game's window, in physical pixels, and the window's full size: the monitor's,
/// when the game runs fullscreen or borderless. <paramref name="FoundGame"/> is false when Deadlock's window
/// wasn't found and the primary monitor was read instead; <paramref name="Origin"/> is where the capture
/// started on the virtual screen.
/// </summary>
public sealed record ScreenCapture(RgbImage Band, int ScreenWidth, int ScreenHeight, bool FoundGame = true, PixelPoint Origin = default);

public sealed class CaptureException(string message) : Exception(message);

public interface IScreenCaptureService
{
    /// <summary>
    /// Grab the top of Deadlock's window (the primary monitor when it isn't running) as it shows on
    /// screen, first minimizing this app's window when <paramref name="minimize"/> and it overlaps what's
    /// captured. Throws <see cref="CaptureException"/> when the screen can't be read.
    /// </summary>
    Task<ScreenCapture> CaptureTopBandAsync(bool minimize);
}
