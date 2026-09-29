using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;

namespace DeadlockAdvisor.Features.Shared.Modals.Choice;

public partial class ChoiceModalView : ReactiveUserControl<ChoiceModalViewModel>
{
    public ChoiceModalView()
    {
        InitializeComponent();

        // The search box keeps focus through the keyboard flow: Up/Down move the pick and Enter takes it.
        Search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        List.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
                Choose();
        };

        AttachedToVisualTree += async (_, _) =>
        {
            // Small delay so the modal has rendered before taking focus.
            await Task.Delay(50);
            Search.Focus();
        };
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
            return;
        switch (e.Key)
        {
            case Key.Down:
                ViewModel.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                ViewModel.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Like OK, which is disabled while the search matches nothing.</summary>
    private void Choose()
    {
        if (ViewModel is { Selected: not null } vm)
            vm.OkCommand.Execute().Subscribe();
    }
}
