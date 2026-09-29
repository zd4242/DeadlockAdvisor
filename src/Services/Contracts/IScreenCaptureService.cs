using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Services.Contracts;

/// <summary>
/// A band off the top of the game's monitor, in physical pixels, and the monitor's full size.
/// <paramref name="FoundGame"/> is false when Deadlock's window wasn't found and the primary monitor was read instead.
/// </summary>
public sealed record ScreenCapture(RgbImage Band, int ScreenWidth, int ScreenHeight, bool FoundGame = true);

public sealed class CaptureException(string message) : Exception(message);

public interface IScreenCaptureService
{
    /// <summary>
    /// Grab the top of the monitor Deadlock is on (the primary one when it isn't running) as the game
    /// shows it, first minimizing this app's window when <paramref name="minimize"/> and it overlaps
    /// what's captured. Throws <see cref="CaptureException"/> when the screen can't be read.
    /// </summary>
    Task<ScreenCapture> CaptureTopBandAsync(bool minimize);
}
