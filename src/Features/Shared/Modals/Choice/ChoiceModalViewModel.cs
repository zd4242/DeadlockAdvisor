using System.Reactive;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Shared.Modals.Choice;

/// <summary>Pick one entry from a list, like Qt's <c>QInputDialog.getItem</c>; the first is preselected.</summary>
public class ChoiceModalViewModel : ViewModelBase
{
    public ChoiceModalViewModel(IModalService modals, string title, string prompt, IReadOnlyList<string> choices, Action<int> chosen)
    {
        Title = title;
        Prompt = prompt;
        Choices = choices;
        SelectedIndex = choices.Count > 0 ? 0 : -1;

        OkCommand = ReactiveCommand.Create(() =>
        {
            var index = SelectedIndex;
            modals.CloseModal();
            if (index >= 0)
                chosen(index);
        });
        CancelCommand = ReactiveCommand.Create(modals.CloseModal);
    }

    public string Title { get; }
    public string Prompt { get; }
    public IReadOnlyList<string> Choices { get; }
    [Reactive] public int SelectedIndex { get; set; }

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
}
