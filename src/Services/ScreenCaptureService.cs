using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using DeadlockAdvisor.Services.Contracts;
using DeadlockAdvisor.Vision;

namespace DeadlockAdvisor.Services;

/// <summary>
/// GDI screen capture of the primary monitor, as the Python app's mss does it. Avalonia makes the
/// process per-monitor DPI aware, so sizes and pixels are physical: display scaling would otherwise
/// hand back a stretched copy of the game and put every measurement out by the scale factor.
/// </summary>
public class ScreenCaptureService : IScreenCaptureService
{
    /// <summary>The strip lives well inside the top fifth of the screen even at the largest HUD scale.</summary>
    public const double BandFraction = 0.22;

    public const int MinBandHeight = 64;

    // Long enough for Windows' minimise animation to finish, so the window isn't caught mid-fade.
    private static readonly TimeSpan _minimiseDelay = TimeSpan.FromMilliseconds(350);

    public async Task<ScreenCapture> CaptureTopBandAsync(bool minimize)
    {
        if (!OperatingSystem.IsWindows())
            throw new CaptureException("Screen capture is only supported on Windows.");

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var restoreTo = minimize && window is { IsVisible: true } && window.WindowState != WindowState.Minimized
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
            return GrabTopBand();
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

    private static ScreenCapture GrabTopBand()
    {
        var width = Native.GetSystemMetrics(Native.SmCxScreen);
        var height = Native.GetSystemMetrics(Native.SmCyScreen);
        if (width <= 0 || height <= 0)
            throw new CaptureException("Couldn't read the size of the primary monitor.");
        var bandHeight = Math.Min(height, Math.Max(MinBandHeight, (int)(height * BandFraction)));

        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
            throw new CaptureException("Couldn't open the screen for capture.");
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            memory = Native.CreateCompatibleDC(screen);
            bitmap = Native.CreateCompatibleBitmap(screen, width, bandHeight);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new CaptureException("Couldn't allocate a bitmap for the capture.");
            var previous = Native.SelectObject(memory, bitmap);
            var copied = Native.BitBlt(memory, 0, 0, width, bandHeight, screen, 0, 0, Native.SrcCopy | Native.CaptureBlt);
            Native.SelectObject(memory, previous);
            if (!copied)
                throw new CaptureException($"Copying the screen failed (error {Marshal.GetLastPInvokeError()}).");

            // A negative height asks for rows top-down.
            var header = new Native.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
                Width = width,
                Height = -bandHeight,
                Planes = 1,
                BitCount = 32,
            };
            var bgra = new byte[width * bandHeight * 4];
            if (Native.GetDIBits(memory, bitmap, 0, (uint)bandHeight, bgra, ref header, 0) != bandHeight)
                throw new CaptureException("Reading the captured pixels failed.");

            var rgb = new byte[width * bandHeight * 3];
            for (int source = 0, target = 0; source < bgra.Length; source += 4, target += 3)
            {
                rgb[target] = bgra[source + 2];
                rgb[target + 1] = bgra[source + 1];
                rgb[target + 2] = bgra[source];
            }
            return new ScreenCapture(new RgbImage(width, bandHeight, rgb), width, height);
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
        public const int SmCxScreen = 0;
        public const int SmCyScreen = 1;
        public const uint SrcCopy = 0x00CC0020;

        /// <summary>Include layered windows, as mss does.</summary>
        public const uint CaptureBlt = 0x40000000;

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
        public static extern int GetSystemMetrics(int index);

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
