using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Features.Shared.Notifications;

public class NotificationViewModel(Notification notification) : ViewModelBase
{
    public string Message { get; } = notification.Message;
    public NotificationSeverity Severity { get; } = notification.Severity;
    public Guid Id { get; } = notification.Id;
}
