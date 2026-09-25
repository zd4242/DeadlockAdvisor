using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Confirmation;

public partial class ConfirmationModalView : ReactiveUserControl<ConfirmationModalViewModel>
{
    public static readonly string ConfirmButtonName = "ConfirmButton";

    public ConfirmationModalView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private async void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Small delay to ensure the modal has been rendered before taking focus.
        await Task.Delay(50);

        if (this.FindControl<Button>(ConfirmButtonName) is { } button)
            button.Focus();
    }
}
