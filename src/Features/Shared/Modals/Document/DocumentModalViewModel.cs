using System.Windows.Input;
using DeadlockAdvisor.Core;

namespace DeadlockAdvisor.Features.Shared.Modals.Document;

/// <summary>A long read-only text, such as a license file: a title, a scrolling body that can be selected and copied, and a Close button.</summary>
public class DocumentModalViewModel(string title, string text, ICommand closeCommand) : ViewModelBase
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public ICommand CloseCommand { get; } = closeCommand;
}
