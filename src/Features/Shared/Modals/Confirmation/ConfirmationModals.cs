using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Confirmation;

public static class ConfirmationModals
{
    /// <summary>
    /// Ask before doing something; every button closes the question first. A destructive question
    /// colours its confirm button as a warning and starts on Cancel, so a stray Enter backs out.
    /// </summary>
    public static void Confirm(this IModalService modals, string prompt, string confirmText, Action confirmed,
        string? secondaryText = null, Action? secondary = null, string cancelText = "Cancel", bool destructive = false)
    {
        modals.ShowModal(new ConfirmationModalViewModel
        {
            Prompt = prompt,
            ConfirmText = confirmText,
            CancelText = cancelText,
            SecondaryConfirmText = secondaryText,
            IsDestructive = destructive,
            ConfirmCommand = ReactiveCommand.Create(() =>
            {
                modals.CloseModal();
                confirmed();
            }),
            SecondaryConfirmCommand = secondary is null
                ? null
                : ReactiveCommand.Create(() =>
                {
                    modals.CloseModal();
                    secondary();
                }),
            CancelCommand = ReactiveCommand.Create(modals.CloseModal),
        });
    }
}
