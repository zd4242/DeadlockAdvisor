using System.Reactive.Disposables;
using System.Reactive.Linq;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Features.Shared.Notifications;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow;

public class MainWindowViewModel : ViewModelBase
{
    public NotificationOverlayViewModel NotificationOverlay { get; }

    [Reactive] public double UiScale { get; private set; }

    public MainWindowViewModel(NotificationOverlayViewModel notificationOverlay, ISettingsService settingsService)
    {
        NotificationOverlay = notificationOverlay;

        settingsService.SettingsChanged
            .Select(s => ZoomLevels.Steps[ZoomLevels.Clamp(s.ZoomIndex)])
            .DistinctUntilChanged()
            .Subscribe(scale => UiScale = scale)
            .DisposeWith(Disposables);
    }
}
