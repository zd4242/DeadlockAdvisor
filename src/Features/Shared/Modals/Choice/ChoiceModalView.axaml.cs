using Avalonia.ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Choice;

public partial class ChoiceModalView : ReactiveUserControl<ChoiceModalViewModel>
{
    public ChoiceModalView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            // Small delay so the modal has rendered before taking focus.
            await Task.Delay(50);
            Choices.Focus();
        };
    }
}
