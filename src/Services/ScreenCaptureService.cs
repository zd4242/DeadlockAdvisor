using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Services;

/// <summary>
/// GDI screen capture of the monitor Deadlock's window is on, falling back to the primary one. Avalonia
/// makes the process per-monitor DPI aware, so sizes, positions and pixels are physical: display scaling
/// would otherwise hand back a stretched copy of the game and put every measurement out by the scale factor.
/// </summary>
public class ScreenCaptureService : IScreenCaptureService
{
    /// <summary>The strip lives well inside the top fifth of the screen even at the largest HUD scale.</summary>
    public const double BandFraction = 0.22;

    public const int MinBandHeight = 64;

    private const string GameProcess = "deadlock";

    // Long enough for Windows' minimise animation to finish, so the window isn't caught mid-fade.
    private static readonly TimeSpan _minimiseDelay = TimeSpan.FromMilliseconds(350);

    public async Task<ScreenCapture> CaptureTopBandAsync(bool minimize)
    {
        if (!OperatingSystem.IsWindows())
            throw new CaptureException("Screen capture is only supported on Windows.");

        var (monitor, foundGame) = GameMonitor();
        var height = monitor.Bottom - monitor.Top;
        var band = monitor with { Bottom = monitor.Top + Math.Min(height, Math.Max(MinBandHeight, (int)(height * BandFraction))) };

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var restoreTo = minimize && window is { IsVisible: true } && window.WindowState != WindowState.Minimized && Overlaps(window, band)
            ? window.WindowState
            : (WindowState?)null;
        if (restoreTo is not null)
        {
            // The advisor sitting over the top bar would be captured instead of the game.
            window!.WindowState = WindowState.Minimized;
            await Task.Delay(_minimiseDelay);
        }

        try
        {
            return new ScreenCapture(Grab(band), monitor.Right - monitor.Left, height, foundGame);
        }
        finally
        {
            if (restoreTo is { } state)
            {
                window!.WindowState = state;
                window.Activate();
            }
        }
    }

    /// <summary>The bounds of the monitor showing most of the game, or of the primary one when it isn't running.</summary>
    private static (Native.Bounds Monitor, bool FoundGame) GameMonitor()
    {
        var game = FindGameWindow();
        var monitor = game != IntPtr.Zero
            ? Native.MonitorFromWindow(game, Native.MonitorDefaultToPrimary)
            : Native.MonitorFromPoint(default, Native.MonitorDefaultToPrimary);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info) || info.Monitor.Right <= info.Monitor.Left || info.Monitor.Bottom <= info.Monitor.Top)
            throw new CaptureException("Couldn't read the size of the game's monitor.");
        return (info.Monitor, game != IntPtr.Zero);
    }

    private static IntPtr FindGameWindow()
    {
        var processes = Process.GetProcessesByName(GameProcess);
        try
        {
            return processes.Select(MainWindowOf).FirstOrDefault(handle => handle != IntPtr.Zero);
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static IntPtr MainWindowOf(Process process)
    {
        try
        {
            return process.MainWindowHandle;
        }
        catch (InvalidOperationException)
        {
            // It exited while we looked.
            return IntPtr.Zero;
        }
    }

    /// <summary>Whether the advisor's window covers any of <paramref name="area"/>; assumed so when its bounds can't be read.</summary>
    private static bool Overlaps(Window window, Native.Bounds area)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle || !Native.GetWindowRect(handle, out var bounds))
            return true;
        return bounds.Left < area.Right && area.Left < bounds.Right && bounds.Top < area.Bottom && area.Top < bounds.Bottom;
    }

    /// <summary>Copy <paramref name="area"/> of the virtual screen, whose origin is the primary monitor's top-left.</summary>
    private static RgbImage Grab(Native.Bounds area)
    {
        var width = area.Right - area.Left;
        var height = area.Bottom - area.Top;
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
            throw new CaptureException("Couldn't open the screen for capture.");
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            memory = Native.CreateCompatibleDC(screen);
            bitmap = Native.CreateCompatibleBitmap(screen, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new CaptureException("Couldn't allocate a bitmap for the capture.");
            var previous = Native.SelectObject(memory, bitmap);
            var copied = Native.BitBlt(memory, 0, 0, width, height, screen, area.Left, area.Top, Native.SrcCopy | Native.CaptureBlt);
            Native.SelectObject(memory, previous);
            if (!copied)
                throw new CaptureException($"Copying the screen failed (error {Marshal.GetLastPInvokeError()}).");

            // A negative height asks for rows top-down.
            var header = new Native.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            };
            var bgra = new byte[width * height * 4];
            if (Native.GetDIBits(memory, bitmap, 0, (uint)height, bgra, ref header, 0) != height)
                throw new CaptureException("Reading the captured pixels failed.");

            var rgb = new byte[width * height * 3];
            for (int source = 0, target = 0; source < bgra.Length; source += 4, target += 3)
            {
                rgb[target] = bgra[source + 2];
                rgb[target + 1] = bgra[source + 1];
                rgb[target + 2] = bgra[source];
            }
            return new RgbImage(width, height, rgb);
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                Native.DeleteObject(bitmap);
            if (memory != IntPtr.Zero)
                Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static class Native
    {
        public const uint SrcCopy = 0x00CC0020;
        public const uint MonitorDefaultToPrimary = 1;

        /// <summary>Include layered windows, as mss does.</summary>
        public const uint CaptureBlt = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        public record struct Bounds(int Left, int Top, int Right, int Bottom);

        [StructLayout(LayoutKind.Sequential)]
        public struct ScreenPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public uint Size;
            public Bounds Monitor;
            public Bounds WorkArea;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(ScreenPoint point, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr window, out Bounds bounds);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint startLine, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr gdiObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr dc);
    }
}
