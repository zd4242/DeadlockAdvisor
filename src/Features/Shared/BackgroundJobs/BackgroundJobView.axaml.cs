using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.BackgroundJobs;

public partial class BackgroundJobView : ReactiveUserControl<BackgroundJobViewModel>
{
    public BackgroundJobView()
    {
        InitializeComponent();
    }
}
