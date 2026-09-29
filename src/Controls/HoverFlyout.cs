using Avalonia.Threading;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// Set <c>HoverFlyout.IsEnabled</c> on a button and resting the pointer on it opens its flyout, as a
/// click still does. Give the flyout <c>ShowMode="TransientWithDismissOnPointerMoveAway"</c> so it
/// closes again once the pointer leaves it.
/// </summary>
public sealed class HoverFlyout
{
    /// <summary>Long enough that sweeping the pointer across the button doesn't open it.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);

    private HoverFlyout()
    {
    }

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<HoverFlyout, Button, bool>("IsEnabled");

    static HoverFlyout()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>((button, change) =>
        {
            if (change.NewValue is true)
                Hook(button);
        });
    }

    public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);

    private static void Hook(Button button)
    {
        var timer = new DispatcherTimer { Interval = Delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (GetIsEnabled(button) && button.IsPointerOver && button.Flyout is { IsOpen: false } flyout)
                flyout.ShowAt(button);
        };
        button.PointerEntered += (_, _) => timer.Start();
        button.PointerExited += (_, _) => timer.Stop();
        button.DetachedFromVisualTree += (_, _) => timer.Stop();
    }
}
