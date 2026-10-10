using Avalonia.Automation;
using Avalonia.Automation.Peers;

namespace DeadlockAdvisor.Controls;

/// <summary>
/// What a screen reader gets from a control that paints itself: a role, a name and, where the control
/// explains itself, a help text. An <c>AutomationProperties.Name</c> set in XAML wins over the
/// control's own wording. <paramref name="decorative"/> hides it from the content view, for a picture
/// whose meaning is already in the text beside it.
/// </summary>
internal sealed class PaintedPeer(
    Control owner,
    AutomationControlType role,
    Func<string?> name,
    Func<string?>? help = null,
    bool decorative = false) : ControlAutomationPeer(owner)
{
    protected override string? GetNameCore() => AutomationProperties.GetName(owner) ?? name();

    protected override string? GetHelpTextCore() => help?.Invoke();

    protected override AutomationControlType GetAutomationControlTypeCore() => role;

    protected override bool IsContentElementCore() => !decorative;

    protected override bool IsControlElementCore() => !decorative;
}
