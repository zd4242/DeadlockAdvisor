using Avalonia.Input;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using DeadlockAdvisor.Converters;

namespace DeadlockAdvisor.Features.Shared.Notifications;

public partial class NotificationOverlay : ReactiveUserControl<NotificationOverlayViewModel>
{
    public NotificationOverlay()
    {
        Resources.Add("SeverityToIconConverter", new SeverityToIconConverter());
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source
            && source.FindAncestorOfType<Border>(includeSelf: true) is { DataContext: NotificationViewModel notification }
            && ViewModel is { } overlay)
        {
            overlay.Dismiss(notification);
            e.Handled = true;
        }
    }
}
