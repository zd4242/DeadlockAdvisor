using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Notifications;

public class NotificationOverlayViewModel : ViewModelBase
{
    private readonly ObservableCollection<NotificationViewModel> _notifications;
    public ReadOnlyObservableCollection<NotificationViewModel> Notifications { get; }

    public NotificationOverlayViewModel(INotificationService notificationService)
    {
        _notifications = [];
        Notifications = new ReadOnlyObservableCollection<NotificationViewModel>(_notifications);

        notificationService.Notifications
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notification =>
            {
                var vm = new NotificationViewModel(notification);
                _notifications.Add(vm);

                // Remove the notification after its duration. The timer completes on its own, so it
                // is not tracked in Disposables, which would otherwise grow with every notification.
                Observable.Timer(notification.Duration, RxApp.MainThreadScheduler)
                    .Subscribe(_ => _notifications.Remove(vm));
            })
            .DisposeWith(Disposables);
    }
}
