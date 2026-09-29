using System.Windows.Input;
using DeadlockAdvisor.Core;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Shared.Modals.Confirmation;

public class ConfirmationModalViewModel : ViewModelBase
{
    [Reactive] public string Prompt { get; set; } = string.Empty;

    [Reactive] public string ConfirmText { get; set; } = "Yes";
    [Reactive] public string CancelText { get; set; } = "No";

    // Optional secondary confirm action. When SecondaryConfirmCommand is non-null,
    // a third button labeled SecondaryConfirmText is shown alongside Confirm/Cancel.
    [Reactive] public string? SecondaryConfirmText { get; set; }

    /// <summary>Confirming loses something: the confirm button is drawn as a warning and Cancel takes the focus.</summary>
    [Reactive] public bool IsDestructive { get; set; }

    public ICommand? ConfirmCommand { get; set; }
    public ICommand? CancelCommand { get; set; }
    public ICommand? SecondaryConfirmCommand { get; set; }
}
