using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Document;

public partial class DocumentModalView : ReactiveUserControl<DocumentModalViewModel>
{
    public DocumentModalView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            // Small delay so the modal has rendered before taking focus.
            await Task.Delay(50);
            CloseButton.Focus();
        };
    }
}
