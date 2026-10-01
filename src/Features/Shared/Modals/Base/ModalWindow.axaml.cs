using Avalonia.Input;
using DeadlockAdvisor.Behaviors;

namespace DeadlockAdvisor.Features.Shared.Modals.Base;

public partial class ModalWindow : Window
{
    private bool _isClosingIntentionally;

    public ModalWindow()
    {
        InitializeComponent();

        // The modal is a window of its own, so the main window's middle-click scrolling doesn't reach it.
        var autoScroll = new MiddleClickAutoScroll(this);
        Closed += (_, _) => autoScroll.Dispose();

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
