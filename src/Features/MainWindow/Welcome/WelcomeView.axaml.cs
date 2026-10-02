using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.MainWindow.Welcome;

public partial class WelcomeView : ReactiveUserControl<WelcomeViewModel>
{
    public WelcomeView()
    {
        InitializeComponent();
    }
}
