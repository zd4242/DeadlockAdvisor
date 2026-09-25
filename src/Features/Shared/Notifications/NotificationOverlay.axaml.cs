using Avalonia.ReactiveUI;
using DeadlockAdvisor.Converters;

namespace DeadlockAdvisor.Features.Shared.Notifications;

public partial class NotificationOverlay : ReactiveUserControl<NotificationOverlayViewModel>
{
    public NotificationOverlay()
    {
        Resources.Add("SeverityToIconConverter", new SeverityToIconConverter());
        InitializeComponent();
    }
}
