using System.Reactive.Linq;
using System.Reactive.Subjects;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

public class NotificationService : INotificationService
{
    internal const int MaxHeld = 8;
    internal static readonly TimeSpan MaxHeldAge = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan _defaultDuration = TimeSpan.FromSeconds(3);

    private readonly Subject<Notification> _notificationSubject = new();
    private readonly ILoggingService _loggingService;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly Queue<(Notification Notification, DateTimeOffset At)> _held = new();
    private bool _hasSubscriber;

    /// <summary>Messages sent before anyone listens (during startup, before the window exists) go to the first subscriber only.</summary>
    public IObservable<Notification> Notifications { get; }

    public NotificationService(ILoggingService loggingService) : this(loggingService, () => DateTimeOffset.UtcNow)
    {
    }

    internal NotificationService(ILoggingService loggingService, Func<DateTimeOffset> utcNow)
    {
        _loggingService = loggingService;
        _utcNow = utcNow;
        Notifications = Observable.Create<Notification>(observer =>
        {
            lock (_gate)
            {
                if (!_hasSubscriber)
                {
                    _hasSubscriber = true;
                    var cutoff = _utcNow() - MaxHeldAge;
                    while (_held.TryDequeue(out var held))
                    {
                        if (held.At >= cutoff)
                            observer.OnNext(held.Notification);
                    }
                }
                return _notificationSubject.Subscribe(observer);
            }
        });
    }

    public void Show(string message, NotificationSeverity severity = NotificationSeverity.Info, TimeSpan? duration = null)
    {
        var notification = new Notification(
            message,
            severity,
            duration ?? _defaultDuration,
            Guid.NewGuid()
        );

        _loggingService.Debug($"Sent notification - {message}");
        lock (_gate)
        {
            if (!_hasSubscriber)
            {
                _held.Enqueue((notification, _utcNow()));
                while (_held.Count > MaxHeld)
                    _held.Dequeue();
                return;
            }
        }
        _notificationSubject.OnNext(notification);
    }

    public void ShowInformation(string message, TimeSpan? duration = null)
        => Show(message, NotificationSeverity.Info, duration);

    public void ShowSuccess(string message, TimeSpan? duration = null)
        => Show(message, NotificationSeverity.Success, duration);

    public void ShowError(string message, TimeSpan? duration = null)
        => Show(message, NotificationSeverity.Error, duration);

    public void ShowError(string message, Exception exception)
    {
        _loggingService.Error(message, exception);
        Show(message, NotificationSeverity.Error);
    }

    public void ShowWarning(string message, TimeSpan? duration = null)
        => Show(message, NotificationSeverity.Warning, duration);
}
