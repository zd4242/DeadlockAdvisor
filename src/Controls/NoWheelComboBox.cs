using Avalonia.Input;

namespace DeadlockAdvisor.Controls;

/// <summary>A combo box the wheel scrolls past rather than through, so scrolling a list of them can't change one on the way by.</summary>
public class NoWheelComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
    }
}
