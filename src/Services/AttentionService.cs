using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>The system's own sounds (so they follow the user's sound scheme and volume) and the taskbar flash, from Win32. Elsewhere, nothing.</summary>
public class AttentionService : IAttentionService
{
    public void Chime(AttentionKind kind)
    {
        if (OperatingSystem.IsWindows())
            Native.MessageBeep(kind == AttentionKind.Done ? Native.IconAsterisk : Native.IconExclamation);
    }

    public void FlashWindow()
    {
        if (!OperatingSystem.IsWindows()
            || (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.TryGetPlatformHandle()?.Handle is not { } handle)
            return;
        var info = new Native.FlashInfo
        {
            Size = (uint)Marshal.SizeOf<Native.FlashInfo>(),
            Window = handle,
            Flags = Native.FlashTray | Native.FlashUntilForeground,
            Count = uint.MaxValue,
        };
        Native.FlashWindowEx(ref info);
    }

    private static class Native
    {
        public const uint IconExclamation = 0x30;
        public const uint IconAsterisk = 0x40;
        public const uint FlashTray = 0x2;
        public const uint FlashUntilForeground = 0xC;

        [StructLayout(LayoutKind.Sequential)]
        public struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MessageBeep(uint type);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FlashInfo info);
    }
}
