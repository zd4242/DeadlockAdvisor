using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.MainWindow;

public partial class DataStatusView : ReactiveUserControl<DataStatusViewModel>
{
    public DataStatusView()
    {
        InitializeComponent();
    }
}
