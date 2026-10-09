using Avalonia.Controls.Primitives;
using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.MainWindow.Updates;

public partial class UpdatesView : ReactiveUserControl<UpdatesViewModel>
{
    public UpdatesView()
    {
        InitializeComponent();
        // Opened hours later, the flyout still says how long ago each check was.
        ((PopupFlyoutBase)UpdatesChip.Flyout!).Opening += (_, _) => ViewModel?.Refresh();
    }
}
