using Avalonia.Input;

namespace DeadlockAdvisor.Features.Shared.Modals.Base;

public partial class ModalWindow : Window
{
    private bool _isClosingIntentionally;

    public ModalWindow()
    {
        InitializeComponent();

        // Prevent Alt+F4 or other OS-driven closes — always close via ModalService.CloseModal().
        Closing += (_, e) =>
        {
            if (!_isClosingIntentionally)
                e.Cancel = true;
        };
    }

    /// <inheritdoc cref="Modal.BackdropPressed"/>
    public event EventHandler<PointerPressedEventArgs>? BackdropPressed
    {
        add => ModalView.BackdropPressed += value;
        remove => ModalView.BackdropPressed -= value;
    }

    internal void CloseIntentionally()
    {
        _isClosingIntentionally = true;
        Close();
    }
}
