using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Base;

public partial class Modal : ReactiveUserControl<ModalViewModel>
{
    public Modal()
    {
        InitializeComponent();
    }
}
