using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>
/// F9 held system-wide with Win32's RegisterHotKey, the way overlay and recording apps take their
/// hotkeys: no elevation, and nothing that looks at the game's own process. Windows posts WM_HOTKEY
/// to the main window whichever app has focus, and no other app receives F9 while it's held.
/// </summary>
public sealed class GlobalHotkeyService(ISettingsService settings, ILoggingService log) : IGlobalHotkeyService, IDisposable
{
    private const int HotkeyId = 0x0D9;

    private readonly Subject<Unit> _pressed = new();
    private readonly BehaviorSubject<HotkeyStatus> _status = new(HotkeyStatus.Unsupported);
    private readonly SerialDisposable _attachment = new();
    private IntPtr _window;
    private bool _registered;

    public IObservable<Unit> Pressed => _pressed;
    public IObservable<HotkeyStatus> Status => _status.DistinctUntilChanged();

    public void Attach(TopLevel window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
        {
            _attachment.Disposable = null;
            _status.OnNext(HotkeyStatus.Unsupported);
            return;
        }

        _window = handle.Handle;
        Win32Properties.CustomWndProcHookCallback hook = OnWindowMessage;
        Win32Properties.AddWndProcHookCallback(window, hook);
        var follow = settings.SettingsChanged
            .Select(s => s.DetectFromAnywhere)
            .DistinctUntilChanged()
            .Subscribe(on =>
            {
                if (on)
                    Register();
                else
                    Unregister(HotkeyStatus.Off);
            });
        var closed = Observable.FromEventPattern(h => window.Closed += h, h => window.Closed -= h)
            .Subscribe(_ => _attachment.Disposable = null);
        _attachment.Disposable = new CompositeDisposable(follow, closed, Disposable.Create(() =>
        {
            Unregister(HotkeyStatus.Unsupported);
            Win32Properties.RemoveWndProcHookCallback(window, hook);
            _window = IntPtr.Zero;
        }));
    }

    private void Register()
    {
        if (_registered)
            return;
        _registered = Native.RegisterHotKey(_window, HotkeyId, Native.ModNoRepeat, Native.VkF9);
        if (_registered)
        {
            log.Information("F9 now detects from any app");
            _status.OnNext(HotkeyStatus.Registered);
            return;
        }
        log.Warning($"Couldn't take F9 system-wide (error {Marshal.GetLastPInvokeError()}); another app probably holds it");
        _status.OnNext(HotkeyStatus.Taken);
    }

    private void Unregister(HotkeyStatus status)
    {
        if (_registered)
            Native.UnregisterHotKey(_window, HotkeyId);
        _registered = false;
        _status.OnNext(status);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            _pressed.OnNext(Unit.Default);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _attachment.Dispose();
        _pressed.Dispose();
        _status.Dispose();
    }

    private static class Native
    {
        public const uint WmHotkey = 0x0312;
        public const uint VkF9 = 0x78;

        /// <summary>One press, one detection: holding the key doesn't repeat it.</summary>
        public const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
