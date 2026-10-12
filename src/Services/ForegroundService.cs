using System.Runtime.InteropServices;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>The foreground window's process, from Win32. Elsewhere, and in the moment no window has focus, it counts as ours.</summary>
public class ForegroundService : IForegroundService
{
    public bool IsAnotherAppInFront
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            var foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;
            Native.GetWindowThreadProcessId(foreground, out var processId);
            return processId != Environment.ProcessId;
        }
    }

    // Asked every few milliseconds by the scoreboard key's watcher, so the process behind a window is looked up once per window.
    private static readonly TimeSpan _recheckAfter = TimeSpan.FromSeconds(5);
    private IntPtr _lastWindow;
    private bool _lastIsGame;
    private long _lastCheckedAt;

    public bool IsGameInFront
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            var foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;
            var now = Environment.TickCount64;
            if (foreground != _lastWindow || now - _lastCheckedAt > _recheckAfter.TotalMilliseconds)
            {
                (_lastWindow, _lastCheckedAt) = (foreground, now);
                _lastIsGame = ScreenCaptureService.IsGameProcess(ScreenCaptureService.ForegroundProcessName(foreground));
            }
            return _lastIsGame;
        }
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
