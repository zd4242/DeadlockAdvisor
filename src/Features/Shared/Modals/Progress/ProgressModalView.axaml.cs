using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Progress;

public partial class ProgressModalView : ReactiveUserControl<ProgressModalViewModel>
{
    public ProgressModalView()
    {
        InitializeComponent();
    }
}
