using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;

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
        if (e.Source is StyledElement { DataContext: TierToggle tier } && DataContext is HeroItemsViewModel page)
        {
            page.ShowOnlyTier(tier);
            e.Handled = true;
        }
    }
}
