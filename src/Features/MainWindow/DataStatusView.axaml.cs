using Avalonia.Controls.Primitives;
using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.MainWindow;

public partial class DataStatusView : ReactiveUserControl<DataStatusViewModel>
{
    public DataStatusView()
    {
        InitializeComponent();
        // Opened hours later, the card still says how old the data is now.
        ((PopupFlyoutBase)Chip.Flyout!).Opening += (_, _) => ViewModel?.Refresh();
    }
}
