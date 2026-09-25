using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Message;

public partial class MessageModalView : ReactiveUserControl<MessageModalViewModel>
{
    public MessageModalView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            // Small delay so the modal has rendered before taking focus.
            await Task.Delay(50);
            OkButton.Focus();
        };
    }
}
