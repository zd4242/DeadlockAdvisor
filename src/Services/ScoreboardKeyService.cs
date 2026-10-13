using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Services;

/// <summary>
/// Polls Win32's GetAsyncKeyState about every 30 ms while the setting is on, the game has focus and Alt isn't held
/// (Alt+Tab is the player leaving the game, not asking for the scoreboard). No hook, nothing that looks at the game's
/// process, and nothing at all while the setting is off.
/// </summary>
public sealed class ScoreboardKeyService : IScoreboardKeyService, IDisposable
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(30);

    private readonly ISettingsService _settings;
    private readonly IForegroundService _foreground;
    private readonly ILoggingService _log;
    private readonly Func<uint, bool> _isDown;
    private readonly Subject<Unit> _pressed = new();
    private readonly BehaviorSubject<ScoreboardKeyStatus> _status;
    private readonly CompositeDisposable _subscriptions = new();
    private bool _wasDown;

    public ScoreboardKeyService(ISettingsService settings, IForegroundService foreground, ILoggingService log)
        : this(settings, foreground, log, RxApp.MainThreadScheduler, OperatingSystem.IsWindows() ? Native.IsDown : static _ => false)
    {
    }

    /// <param name="scheduler">Where the polls happen, which is also the thread <see cref="Pressed"/> is raised on.</param>
    /// <param name="isDown">Whether a virtual key is down.</param>
    internal ScoreboardKeyService(ISettingsService settings, IForegroundService foreground, ILoggingService log, IScheduler scheduler, Func<uint, bool> isDown)
    {
        _settings = settings;
        _foreground = foreground;
        _log = log;
        _isDown = isDown;
        _status = new BehaviorSubject<ScoreboardKeyStatus>(OperatingSystem.IsWindows() ? Initial(settings.Current) : ScoreboardKeyStatus.Unsupported);

        if (!OperatingSystem.IsWindows())
            return;
        settings.SettingsChanged
            .Select(s => s.ReadBuildsOnTab)
            .DistinctUntilChanged()
            .Do(on =>
            {
                _status.OnNext(on ? ScoreboardKeyStatus.Watching : ScoreboardKeyStatus.Off);
                _log.Information(on ? $"Scoreboard: watching {ScoreboardKeys.Label(settings.Current.Get())} while the game is in front" : "Scoreboard: no longer watching the key");
            })
            .Select(on => on ? Observable.Interval(PollEvery, scheduler) : Observable.Never<long>())
            .Switch()
            .Subscribe(_ => Poll())
            .DisposeWith(_subscriptions);
    }

    public IObservable<Unit> Pressed => _pressed;

    public IObservable<ScoreboardKeyStatus> Status => _status.DistinctUntilChanged();

    public bool IsHeld => OperatingSystem.IsWindows() && ConfiguredKey() is { } key && _isDown(key) && !_isDown(Native.VkMenu);

    private static ScoreboardKeyStatus Initial(AppSettings settings) => settings.ReadBuildsOnTab ? ScoreboardKeyStatus.Watching : ScoreboardKeyStatus.Off;

    private uint? ConfiguredKey() => ScoreboardKeys.VirtualKey(_settings.Current.Get());

    private void Poll()
    {
        if (ConfiguredKey() is not { } key)
        {
            _wasDown = false;
            return;
        }
        // Not the game's key while the game isn't looked at, so the edge starts again from "up" when it comes back.
        if (!_foreground.IsGameInFront)
        {
            _wasDown = false;
            return;
        }
        // Alt+Tab usually lets go of Alt first: a Tab still down then is the switcher's, not a fresh press.
        if (_isDown(Native.VkMenu))
        {
            _wasDown = _isDown(key);
            return;
        }
        var down = _isDown(key);
        var pressed = down && !_wasDown;
        _wasDown = down;
        if (pressed)
            _pressed.OnNext(Unit.Default);
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _pressed.Dispose();
        _status.Dispose();
    }

    private static class Native
    {
        public const uint VkMenu = 0x12;

        /// <summary>Down right now: the high bit of GetAsyncKeyState.</summary>
        public static bool IsDown(uint virtualKey) => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
    }
}
