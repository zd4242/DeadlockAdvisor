using Avalonia.Interactivity;
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

    private async void CopyAll(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(ViewModel.Text);
    }
}
