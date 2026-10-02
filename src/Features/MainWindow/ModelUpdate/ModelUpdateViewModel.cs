using System.Reactive;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow.ModelUpdate;

/// <summary>One file you've changed that has a newer published version: replace it, or keep yours.</summary>
public sealed class ModelFileChoice(string file) : ReactiveObject
{
    public string File { get; } = file;

    /// <summary>"Hero trait ratings".</summary>
    public string Title { get; } = ModelManifest.Title(file);

    [Reactive] public bool Replace { get; set; }
}

/// <summary>
/// A newer published model, when some of the files it would replace have been changed here: each of those
/// is kept unless ticked. The files nobody changed are updated along with them.
/// </summary>
public sealed class ModelUpdateViewModel : ViewModelBase
{
    /// <param name="apply">Called with the edited files to replace; the rest of them are kept.</param>
    public ModelUpdateViewModel(IModalService modals, ModelUpdatePlan update, Action<IReadOnlySet<string>> apply)
    {
        Intro = $"A newer version of the hero ratings and item formulas was published on {update.Published.Published}. "
                + "You've changed some of the files it replaces since they were installed, by editing them or with Sync from Game API.";
        Choices = update.Edited.Select(file => new ModelFileChoice(file)).ToList();
        AlsoUpdated = update.Quiet.Count == 0
            ? ""
            : "Also updated, as you haven't changed them: " + string.Join(", ", update.Quiet.Select(ModelManifest.Title)) + ".";

        UpdateCommand = ReactiveCommand.Create(() =>
        {
            modals.CloseModal();
            apply(Choices.Where(choice => choice.Replace).Select(choice => choice.File).ToHashSet());
        });
        NotNowCommand = ReactiveCommand.Create(modals.CloseModal);
    }

    public string Title => "Formula update";

    public string Intro { get; }

    /// <summary>Tick one to replace your changes with the published version.</summary>
    public IReadOnlyList<ModelFileChoice> Choices { get; }

    public string AlsoUpdated { get; }

    public bool HasAlsoUpdated => AlsoUpdated.Length > 0;

    public string Footer =>
        "Unticked files keep your changes, and this version won't ask about them again (Data → Check for Formula Updates does). "
        + "Every replaced file keeps a backup in data\\.backups.";

    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> NotNowCommand { get; }
}
