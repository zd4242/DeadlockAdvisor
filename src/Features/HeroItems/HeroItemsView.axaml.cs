using System.Reactive.Disposables;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using ReactiveUI;

namespace DeadlockAdvisor.Features.HeroItems;

public partial class HeroItemsView : ReactiveUserControl<HeroItemsViewModel>
{
    public HeroItemsView()
    {
        InitializeComponent();
        // handledEventsToo: a tier's toggle may already have handled the press.
        TierSwitch.AddHandler(InputElement.PointerPressedEvent, OnTierPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        // Tunnelled, to step the slider by whole percents before it steps by its own (far finer) positions.
        MinUsage.AddHandler(InputElement.KeyDownEvent, OnUsageKeyDown, RoutingStrategies.Tunnel);

        this.WhenActivated(disposables =>
        {
            ViewModel!.ViewInteraction
                .Subscribe(action =>
                {
                    // Without match data the picker is hidden, and there is nothing to search.
                    if (action == HeroItemsViewModel.FocusSearchAction && HeroPicker.IsEffectivelyVisible)
                    {
                        HeroPicker.Focus();
                        HeroPicker.IsDropDownOpen = true;
                    }
                })
                .DisposeWith(disposables);
        });
    }

    private void OnUsageKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not HeroItemsViewModel page)
            return;
        switch (e.Key)
        {
            case Key.Left or Key.Down:
                page.StepUsage(-1);
                break;
            case Key.Right or Key.Up:
                page.StepUsage(1);
                break;
            case Key.PageDown:
                page.StepUsage(-5);
                break;
            case Key.PageUp:
                page.StepUsage(5);
                break;
            case Key.Home:
                page.StepUsage(-HeroItemsViewModel.MaxMinUsagePercent);
                break;
            case Key.End:
                page.StepUsage(HeroItemsViewModel.MaxMinUsagePercent);
                break;
            default:
                return;
        }
        e.Handled = true;
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
