using System.Windows.Input;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Features.Shared.Modals.Message;

/// <summary>A read-once report: a title, a body of plain text, and an OK button.</summary>
public class MessageModalViewModel(string title, string body, ICommand closeCommand) : ViewModelBase
{
    public string Title { get; } = title;
    public string Body { get; } = body;
    public ICommand CloseCommand { get; } = closeCommand;
}
