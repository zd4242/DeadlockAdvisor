using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.MainWindow;

public partial class ConnectionView : ReactiveUserControl<ConnectionViewModel>
{
    public ConnectionView()
    {
        InitializeComponent();
    }
}
