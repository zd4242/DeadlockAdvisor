using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using Avalonia.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>
/// Detect's key held system-wide with Win32's RegisterHotKey, the way overlay and recording apps take
/// their hotkeys: no elevation, and nothing that looks at the game's own process. Windows posts
/// WM_HOTKEY to the main window whichever app has focus, and no other app receives the key while it's held.
/// </summary>
public sealed class GlobalHotkeyService(ISettingsService settings, ILoggingService log) : IGlobalHotkeyService, IDisposable
{
    private const int HotkeyId = 0x0D9;

    private readonly Subject<Unit> _pressed = new();
    private readonly BehaviorSubject<HotkeyStatus> _status = new(HotkeyStatus.Unsupported);
    private readonly BehaviorSubject<int> _suspensions = new(0);
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
            .Select(s => s.DetectFromAnywhere ? s.Gesture(ShortcutAction.Detect) : null)
            .DistinctUntilChanged()
            .CombineLatest(_suspensions.Select(count => count > 0).DistinctUntilChanged(), (gesture, suspended) => (gesture, suspended))
            .Subscribe(state => Follow(state.gesture, state.suspended));
        var closed = Observable.FromEventPattern(h => window.Closed += h, h => window.Closed -= h)
            .Subscribe(_ => _attachment.Disposable = null);
        _attachment.Disposable = new CompositeDisposable(follow, closed, Disposable.Create(() =>
        {
            Release();
            _status.OnNext(HotkeyStatus.Unsupported);
            Win32Properties.RemoveWndProcHookCallback(window, hook);
            _window = IntPtr.Zero;
        }));
    }

    public IDisposable Suspend()
    {
        _suspensions.OnNext(_suspensions.Value + 1);
        return Disposable.Create(() => _suspensions.OnNext(_suspensions.Value - 1));
    }

    /// <summary>
    /// Re-register for a new key (Windows holds one per id). While suspended the status stays as it
    /// was, since the key comes back in a moment.
    /// </summary>
    private void Follow(KeyGesture? gesture, bool suspended)
    {
        Release();
        if (suspended)
            return;
        if (gesture is null)
        {
            _status.OnNext(HotkeyStatus.Off);
            return;
        }

        var label = ShortcutKeys.Label(gesture);
        if (VirtualKey(gesture.Key) is not { } key)
        {
            log.Warning($"{label} has no Windows key code, so it only detects while this window has focus");
            _status.OnNext(HotkeyStatus.Taken);
            return;
        }
        _registered = Native.RegisterHotKey(_window, HotkeyId, Native.ModNoRepeat | Modifiers(gesture.KeyModifiers), key);
        if (_registered)
        {
            log.Information($"{label} now detects from any app");
            _status.OnNext(HotkeyStatus.Registered);
            return;
        }
        log.Warning($"Couldn't take {label} system-wide (error {Marshal.GetLastPInvokeError()}); another app probably holds it");
        _status.OnNext(HotkeyStatus.Taken);
    }

    private void Release()
    {
        if (_registered)
            Native.UnregisterHotKey(_window, HotkeyId);
        _registered = false;
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

    /// <summary>The Windows virtual-key code for each key <see cref="ShortcutKeys.IsSupported"/> allows.</summary>
    internal static uint? VirtualKey(Key key) => key switch
    {
        >= Key.F1 and <= Key.F24 => 0x70u + (uint)(key - Key.F1),
        >= Key.A and <= Key.Z => 0x41u + (uint)(key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30u + (uint)(key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60u + (uint)(key - Key.NumPad0),
        Key.Insert => 0x2D,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.Pause => 0x13,
        Key.Scroll => 0x91,
        _ => null,
    };

    internal static uint Modifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Alt) ? Native.ModAlt : 0)
        | (modifiers.HasFlag(KeyModifiers.Control) ? Native.ModControl : 0)
        | (modifiers.HasFlag(KeyModifiers.Shift) ? Native.ModShift : 0);

    public void Dispose()
    {
        _attachment.Dispose();
        _pressed.Dispose();
        _status.Dispose();
        _suspensions.Dispose();
    }

    private static class Native
    {
        public const uint WmHotkey = 0x0312;
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;

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
