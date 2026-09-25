using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeadlockAdvisor.Features.Shared.ItemCard;

/// <summary>
/// Opt a control into the item card: set <c>ItemCardHover.ItemId</c> on it and hovering shows that
/// item's card after a short delay, hidden again on leave, press or wheel. The window's
/// <see cref="ItemCardPresenter"/> (found through the inherited <see cref="PresenterProperty"/>) shows it.
/// </summary>
public sealed class ItemCardHover
{
    private ItemCardHover()
    {
    }

    public static readonly AttachedProperty<string?> ItemIdProperty =
        AvaloniaProperty.RegisterAttached<ItemCardHover, Control, string?>("ItemId");

    public static readonly AttachedProperty<ItemCardPresenter?> PresenterProperty =
        AvaloniaProperty.RegisterAttached<ItemCardHover, Visual, ItemCardPresenter?>("Presenter", inherits: true);

    private static readonly AttachedProperty<bool> _hookedProperty =
        AvaloniaProperty.RegisterAttached<ItemCardHover, Control, bool>("Hooked");

    static ItemCardHover()
    {
        ItemIdProperty.Changed.AddClassHandler<Control>((control, _) => Hook(control));
    }

    public static string? GetItemId(Control control) => control.GetValue(ItemIdProperty);
    public static void SetItemId(Control control, string? value) => control.SetValue(ItemIdProperty, value);
    public static ItemCardPresenter? GetPresenter(Visual visual) => visual.GetValue(PresenterProperty);
    public static void SetPresenter(Visual visual, ItemCardPresenter? value) => visual.SetValue(PresenterProperty, value);

    private static void Hook(Control control)
    {
        if (control.GetValue(_hookedProperty))
            return;
        control.SetValue(_hookedProperty, true);

        control.PointerEntered += (_, _) => GetPresenter(control)?.Hover(control, GetItemId(control));
        control.PointerExited += (_, _) => GetPresenter(control)?.Hide(control);
        control.AddHandler(InputElement.PointerPressedEvent, (_, _) => GetPresenter(control)?.Hide(control), RoutingStrategies.Tunnel, handledEventsToo: true);
        control.PointerWheelChanged += (_, _) => GetPresenter(control)?.Hide(control);
        control.DetachedFromVisualTree += (_, _) => GetPresenter(control)?.Hide(control);
    }
}
