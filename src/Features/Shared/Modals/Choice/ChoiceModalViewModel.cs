using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Shared.Modals.Choice;

/// <summary>An entry of the pick-one dialog.</summary>
/// <param name="Detail">Faint after the label, and not searched, e.g. "T4 · 4 rules".</param>
/// <param name="ArtId">The hero portrait or item icon before the label; null for none.</param>
/// <param name="Tint">The placeholder's colour when the art is missing, e.g. an item's shop colour.</param>
public sealed record Choice(string Label, string? Detail = null, ArtKind Kind = ArtKind.Item, string? ArtId = null, Color? Tint = null);

/// <summary>
/// Pick one entry from a list, like Qt's <c>QInputDialog.getItem</c>, with a search that narrows the list as you
/// type, best match first and selected.
/// </summary>
public class ChoiceModalViewModel : ViewModelBase
{
    public ChoiceModalViewModel(IModalService modals, string title, string prompt, IReadOnlyList<Choice> choices, Action<int> chosen)
    {
        Title = title;
        Prompt = prompt;
        Choices = choices;
        Shown = choices;
        Selected = choices.FirstOrDefault();

        this.WhenAnyValue(vm => vm.SearchText)
            .Skip(1)
            .Subscribe(text =>
            {
                Shown = FuzzyMatch.Filter(Choices, text, choice => choice.Label);
                Selected = Shown.FirstOrDefault();
            })
            .DisposeWith(Disposables);

        OkCommand = ReactiveCommand.Create(() =>
        {
            var index = Choices.ToList().FindIndex(choice => ReferenceEquals(choice, Selected));
            modals.CloseModal();
            if (index >= 0)
                chosen(index);
        }, this.WhenAnyValue(vm => vm.Selected).Select(selected => selected is not null));
        CancelCommand = ReactiveCommand.Create(modals.CloseModal);
    }

    public string Title { get; }
    public string Prompt { get; }
    public IReadOnlyList<Choice> Choices { get; }

    [Reactive] public string SearchText { get; set; } = "";

    /// <summary>The choices the search matches, best first; all of them, in order, before anything's typed.</summary>
    [Reactive] public IReadOnlyList<Choice> Shown { get; private set; }

    [Reactive] public Choice? Selected { get; set; }

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Up and Down from the search box: the selection moves through what's shown, stopping at the ends.</summary>
    public void MoveSelection(int delta)
    {
        if (Shown.Count == 0)
            return;
        var at = Selected is null ? -1 : Shown.ToList().IndexOf(Selected);
        Selected = Shown[Math.Clamp(at + delta, 0, Shown.Count - 1)];
    }
}
