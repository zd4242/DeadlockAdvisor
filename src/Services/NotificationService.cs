using System.Reactive.Subjects;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

public class NotificationService : INotificationService
{
    private readonly Subject<Notification> _notificationSubject = new();
    public IObservable<Notification> Notifications => _notificationSubject;

    private static readonly TimeSpan _defaultDuration = TimeSpan.FromSeconds(3);
    private readonly ILoggingService _loggingService;

    public NotificationService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
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
