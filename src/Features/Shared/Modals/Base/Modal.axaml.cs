using Avalonia.Input;
using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Base;

public partial class Modal : ReactiveUserControl<ModalViewModel>
{
    public Modal()
    {
        InitializeComponent();
        OverlayPanel.PointerPressed += (_, e) => BackdropPressed?.Invoke(this, e);
    }

    /// <summary>A press on the dim around the modal, rather than on the modal itself.</summary>
    public event EventHandler<PointerPressedEventArgs>? BackdropPressed;
}
