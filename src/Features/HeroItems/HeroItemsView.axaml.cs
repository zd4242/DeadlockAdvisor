using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;

namespace DeadlockAdvisor.Features.HeroItems;

public partial class HeroItemsView : ReactiveUserControl<HeroItemsViewModel>
{
    public HeroItemsView()
    {
        InitializeComponent();
        // handledEventsToo: a tier's toggle may already have handled the press.
        TierSwitch.AddHandler(InputElement.PointerPressedEvent, OnTierPressed, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnTierPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;
        // The press lands on the label inside the toggle, whose own DataContext is its text.
        if (e.Source is Visual source
            && source.FindAncestorOfType<ToggleButton>(includeSelf: true)?.DataContext is TierToggle tier
            && DataContext is HeroItemsViewModel page)
        {
            page.ShowOnlyTier(tier);
            e.Handled = true;
        }
    }
}
