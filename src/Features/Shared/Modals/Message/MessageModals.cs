using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Message;

public static class MessageModals
{
    /// <summary>Show a title and a plain-text body with an OK button that closes it.</summary>
    public static void ShowMessage(this IModalService modals, string title, string body) =>
        modals.ShowModal(new MessageModalViewModel(title, body, ReactiveCommand.Create(modals.CloseModal)));
}
