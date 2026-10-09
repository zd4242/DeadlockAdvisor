using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Shared.Modals.Document;

public static class DocumentModals
{
    /// <summary>Show a long text in a scrolling, selectable box with a Close button.</summary>
    public static void ShowDocument(this IModalService modals, string title, string text) =>
        modals.ShowModal(new DocumentModalViewModel(title, text, ReactiveCommand.Create(modals.CloseModal)));
}
