namespace DeadlockAdvisor.Services.Contracts;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public record Notification(
    string Message,
    NotificationSeverity Severity,
    TimeSpan Duration,
    Guid Id = default,
    DateTimeOffset At = default
);

public interface INotificationService
{
    IObservable<Notification> Notifications { get; }

    /// <summary>The last few messages, oldest first, for looking one up after its toast is gone.</summary>
    IReadOnlyList<Notification> Recent { get; }

    void Show(string message, NotificationSeverity severity = NotificationSeverity.Info, TimeSpan? duration = null);
    void ShowInformation(string message, TimeSpan? duration = null);
    void ShowSuccess(string message, TimeSpan? duration = null);
    void ShowError(string message, TimeSpan? duration = null);

    /// <summary>
    /// Logs the exception and shows the user-facing message, for the view-model boundary where an
    /// operation's failure is reported to the user.
    /// </summary>
    void ShowError(string message, Exception exception);
    void ShowWarning(string message, TimeSpan? duration = null);
}
