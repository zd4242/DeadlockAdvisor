using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DeadlockAdvisor.Core;

/// <summary>
/// Lets one copy of the app run per data folder. The first copy holds a named mutex and listens on a named event;
/// a later copy sets the event and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowSuffix = ".show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly ManualResetEventSlim _stopped = new();
    private Thread? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// <summary>One name per data folder, so a copy started with another DEADLOCK_ADVISOR_HOME runs beside the real one.</summary>
    public static string NameFor(string appDataPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(appDataPath).TrimEnd('\\', '/').ToLowerInvariant()));
        return "DeadlockAdvisor-" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>The handle if this is the first copy for the name, otherwise null. Keep it for the life of the process.</summary>
    public static SingleInstance? TryBecomePrimary(string name)
    {
        // No ownership to release: the mutex exists while any copy has it open, and a crashed copy's handle closes with it.
        var mutex = new Mutex(false, name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        // Made now, not in Listen, so a signal sent while the window is still opening isn't lost.
        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, name + ShowSuffix));
    }

    /// <summary>Asks the first copy to come forward. False if it isn't there to ask.</summary>
    public static bool SignalPrimary(string name)
    {
        if (!OperatingSystem.IsWindows() || !EventWaitHandle.TryOpenExisting(name + ShowSuffix, out var show))
            return false;

        using (show)
        {
            AllowForeground();
            return show.Set();
        }
    }

    /// <summary>Runs <paramref name="onSignal"/> on a background thread each time another copy calls <see cref="SignalPrimary"/>.</summary>
    public void Listen(Action onSignal)
    {
        _listener = new Thread(() =>
        {
            WaitHandle[] handles = [_show, _stopped.WaitHandle];
            while (WaitHandle.WaitAny(handles) == 0)
                onSignal();
        })
        {
            IsBackground = true,
            Name = "Single instance listener",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        _stopped.Set();
        _listener?.Join();
        _show.Dispose();
        _mutex.Dispose();
        _stopped.Dispose();
    }

    // Windows lets only the process in front, or one it names, take the foreground: name the copies already running.
    private static void AllowForeground()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var self = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(self.ProcessName))
        {
            using (other)
            {
                if (other.Id != self.Id)
                    AllowSetForegroundWindow((uint)other.Id);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
