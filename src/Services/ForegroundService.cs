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

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
